using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using BillingService.Application.Abstractions;
using BillingService.Application.Common;
using BillingService.Application.Messaging;
using BillingService.Domain.Common;
using BillingService.Domain.Entities;
using static BillingService.Infrastructure.Persistence.BillingItems;

namespace BillingService.Infrastructure.Persistence;

public class DynamoDbBillingStore(IAmazonDynamoDB dynamo, string tableName) : IBillingStore, IOutboxStore
{
    private const string OutboxPartition = "OUTBOX";

    public async Task<IReadOnlyList<Service>> ListServicesAsync(CancellationToken cancellationToken) =>
        (await QueryPartitionAsync(ServicePartition, cancellationToken)).Select(ToService).ToList();

    public async Task<Service?> GetServiceAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await GetAsync(ServicePartition, id, cancellationToken);
        return item is null ? null : ToService(item);
    }

    public async Task<IReadOnlyList<Part>> ListPartsAsync(CancellationToken cancellationToken) =>
        (await QueryPartitionAsync(PartPartition, cancellationToken)).Select(ToPart).ToList();

    public async Task<Part?> GetPartAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await GetAsync(PartPartition, id, cancellationToken);
        return item is null ? null : ToPart(item);
    }

    public async Task<PaymentTransaction?> GetPaymentAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await GetAsync(PaymentPartition, id, cancellationToken);
        return item is null ? null : ToPayment(item);
    }

    public async Task CommitAsync(BillingChanges changes, CancellationToken cancellationToken)
    {
        var writes = new List<TransactWriteItem>();
        writes.AddRange(changes.Services.Select(s => VersionedPut(ToItem(s), s.Version)));
        writes.AddRange(changes.Parts.Select(p => VersionedPut(ToItem(p), p.Version)));
        writes.AddRange(changes.Payments.Select(p => VersionedPut(ToItem(p), p.Version)));
        writes.AddRange(changes.Outbox.Select(m => new TransactWriteItem { Put = new Put { TableName = tableName, Item = ToOutboxItem(m) } }));

        if (writes.Count == 0)
            return;

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, cancellationToken);
        }
        catch (TransactionCanceledException ex) when (ex.CancellationReasons.Exists(IsConflict))
        {
            throw new ConcurrencyConflictException("The record was changed by another operation. Please try again.", ex);
        }

        foreach (var entity in changes.Services.Cast<VersionedEntity>().Concat(changes.Parts).Concat(changes.Payments))
            entity.MarkPersisted();
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int limit, CancellationToken cancellationToken)
    {
        var request = PartitionQuery(OutboxPartition);
        request.Limit = limit;

        var response = await dynamo.QueryAsync(request, cancellationToken);
        return (response.Items ?? []).Select(ToOutboxMessage).ToList();
    }

    public async Task MarkPublishedAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        await dynamo.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = tableName,
            Key = OutboxKey(message)
        }, cancellationToken);
    }

    public async Task MarkFailedAsync(OutboxMessage message, string error, CancellationToken cancellationToken)
    {
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = tableName,
            Key = OutboxKey(message),
            UpdateExpression = "SET lastError = :error ADD attempts :one",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":error"] = S(OutboxMessage.TruncateError(error)),
                [":one"] = N(1)
            }
        }, cancellationToken);
    }

    private TransactWriteItem VersionedPut(Dictionary<string, AttributeValue> item, int version)
    {
        item["version"] = N(version + 1);

        var put = new Put { TableName = tableName, Item = item };
        if (version == 0)
        {
            put.ConditionExpression = "attribute_not_exists(pk)";
        }
        else
        {
            put.ConditionExpression = "#version = :expected";
            put.ExpressionAttributeNames = new Dictionary<string, string> { ["#version"] = "version" };
            put.ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":expected"] = N(version) };
        }

        return new TransactWriteItem { Put = put };
    }

    private async Task<Dictionary<string, AttributeValue>?> GetAsync(string pk, Guid id, CancellationToken cancellationToken)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            ConsistentRead = true,
            Key = Key(pk, id.ToString("D"))
        }, cancellationToken);

        return response.Item is { Count: > 0 } item ? item : null;
    }

    private async Task<List<Dictionary<string, AttributeValue>>> QueryPartitionAsync(string pk, CancellationToken cancellationToken)
    {
        var request = PartitionQuery(pk);
        var items = new List<Dictionary<string, AttributeValue>>();
        do
        {
            var response = await dynamo.QueryAsync(request, cancellationToken);
            if (response.Items is not null)
                items.AddRange(response.Items);
            request.ExclusiveStartKey = response.LastEvaluatedKey;
        } while (request.ExclusiveStartKey is { Count: > 0 });

        return items;
    }

    private QueryRequest PartitionQuery(string pk) => new()
    {
        TableName = tableName,
        ConsistentRead = true,
        KeyConditionExpression = "pk = :pk",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = S(pk) }
    };

    private static Dictionary<string, AttributeValue> ToOutboxItem(OutboxMessage message)
    {
        var item = new Dictionary<string, AttributeValue>(OutboxKey(message))
        {
            ["entityType"] = S("Outbox"),
            ["id"] = S(message.Id.ToString("D")),
            ["eventType"] = S(message.EventType),
            ["correlationId"] = S(message.CorrelationId.ToString("D")),
            ["content"] = S(message.Content),
            ["occurredAt"] = S(Timestamp(message.OccurredAt)),
            ["attempts"] = N(0)
        };

        if (!string.IsNullOrEmpty(message.TraceParent))
            item["traceParent"] = S(message.TraceParent);

        return item;
    }

    private static OutboxMessage ToOutboxMessage(Dictionary<string, AttributeValue> item) => OutboxMessage.Restore(
        Guid.Parse(item["id"].S),
        item["eventType"].S,
        Guid.Parse(item["correlationId"].S),
        item["content"].S,
        item.TryGetValue("traceParent", out var trace) ? trace.S : null,
        DateTime.Parse(item["occurredAt"].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        int.Parse(item["attempts"].N, CultureInfo.InvariantCulture),
        item.TryGetValue("lastError", out var error) ? error.S : null);

    private static Dictionary<string, AttributeValue> OutboxKey(OutboxMessage message) =>
        Key(OutboxPartition, $"{Timestamp(message.OccurredAt)}#{message.Id:D}");

    private static Dictionary<string, AttributeValue> Key(string pk, string sk) => new()
    {
        ["pk"] = S(pk),
        ["sk"] = S(sk)
    };

    private static bool IsConflict(CancellationReason reason) =>
        reason.Code is "ConditionalCheckFailed" or "TransactionConflict";
}

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using BillingService.Application.Abstractions;
using BillingService.Application.Common;
using BillingService.Application.Messaging;
using BillingService.Domain.Enums;
using BillingService.Infrastructure.Persistence;
using BillingService.Tests.Helpers;
using Moq;

namespace BillingService.Tests.Infrastructure;

public class DynamoDbBillingStoreTests
{
    private const string Table = "oficina-billing";

    private readonly Mock<IAmazonDynamoDB> _dynamo = new();
    private DynamoDbBillingStore Store => new(_dynamo.Object, Table);

    private TransactWriteItemsRequest CaptureCommit()
    {
        var captured = new TransactWriteItemsRequest();
        _dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((r, _) => captured.TransactItems = r.TransactItems)
            .ReturnsAsync(new TransactWriteItemsResponse());
        return captured;
    }

    private void ReturnItem(Dictionary<string, AttributeValue>? item) =>
        _dynamo.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { Item = item ?? [] });

    [Fact]
    public async Task Commit_NewEntities_ShouldWriteDeclaredColumnsAndRequireItemNotToExist()
    {
        var request = CaptureCommit();
        var service = TestData.NewService();
        var part = TestData.NewPart(stock: 3);
        var payment = TestData.NewPayment();

        await Store.CommitAsync(new BillingChanges().Save(service).Save(part).Save(payment), CancellationToken.None);

        var puts = request.TransactItems.Select(t => t.Put).ToList();
        Assert.All(puts, p => Assert.Equal(Table, p.TableName));
        Assert.All(puts, p => Assert.Equal("attribute_not_exists(pk)", p.ConditionExpression));
        Assert.All(puts, p => Assert.Equal("1", p.Item["version"].N));

        Assert.Equal("SERVICE", puts[0].Item["pk"].S);
        Assert.Equal(service.Id.ToString("D"), puts[0].Item["id"].S);
        Assert.Equal("150", puts[0].Item["defaultPrice"].N);
        Assert.False(puts[0].Item["inactive"].BOOL);

        Assert.Equal("PART", puts[1].Item["pk"].S);
        Assert.Equal(part.Code, puts[1].Item["code"].S);
        Assert.Equal("3", puts[1].Item["quantityInStock"].N);
        Assert.Equal("80.50", puts[1].Item["price"].N);

        Assert.Equal("PAYMENT", puts[2].Item["pk"].S);
        Assert.Equal(payment.ServiceOrderId.ToString("D"), puts[2].Item["serviceOrderId"].S);
        Assert.Equal("311", puts[2].Item["totalAmount"].N);
        Assert.Equal("pref-1", puts[2].Item["gatewayTransactionId"].S);
        Assert.Equal("https://mp.test/x", puts[2].Item["paymentUrl"].S);
        Assert.Equal("Pending", puts[2].Item["status"].S);

        Assert.Equal(1, payment.Version);
    }

    [Fact]
    public async Task Commit_ExistingEntity_ShouldRequireReadVersion()
    {
        var request = CaptureCommit();
        var payment = TestData.NewPayment();
        payment.MarkPersisted();
        payment.Pay();

        await Store.CommitAsync(new BillingChanges().Save(payment), CancellationToken.None);

        var put = Assert.Single(request.TransactItems).Put;
        Assert.Equal("#version = :expected", put.ConditionExpression);
        Assert.Equal("1", put.ExpressionAttributeValues[":expected"].N);
        Assert.Equal("2", put.Item["version"].N);
        Assert.Equal("Paid", put.Item["status"].S);
    }

    [Fact]
    public async Task Commit_ShouldWriteEntityAndOutboxInSameTransaction()
    {
        var request = CaptureCommit();
        var part = TestData.NewPart();

        await Store.CommitAsync(
            new BillingChanges().Save(part).Publish(EventTypes.PartStockAdded, part.Id, new { }),
            CancellationToken.None);

        Assert.Equal(2, request.TransactItems.Count);
        Assert.Equal("OUTBOX", request.TransactItems[1].Put.Item["pk"].S);
        Assert.Equal(EventTypes.PartStockAdded, request.TransactItems[1].Put.Item["eventType"].S);
    }

    [Fact]
    public async Task Commit_WithoutChanges_ShouldNotCallDynamo()
    {
        await Store.CommitAsync(new BillingChanges(), CancellationToken.None);

        _dynamo.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ConditionalCheckFailed")]
    [InlineData("TransactionConflict")]
    public async Task Commit_WithConflict_ShouldThrowConcurrencyConflict(string code)
    {
        _dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransactionCanceledException("cancelled")
            {
                CancellationReasons = [new CancellationReason { Code = "None" }, new CancellationReason { Code = code }]
            });
        var part = TestData.NewPart();

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            Store.CommitAsync(new BillingChanges().Save(part), CancellationToken.None));
        Assert.Equal(0, part.Version);
    }

    [Fact]
    public async Task Commit_WithOtherCancellation_ShouldPropagate()
    {
        _dynamo.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransactionCanceledException("cancelled")
            {
                CancellationReasons = [new CancellationReason { Code = "ThrottlingError" }]
            });

        await Assert.ThrowsAsync<TransactionCanceledException>(() =>
            Store.CommitAsync(new BillingChanges().Save(TestData.NewPart()), CancellationToken.None));
    }

    [Fact]
    public async Task Reads_ShouldRestoreEntitiesFromWrittenItems()
    {
        var writes = CaptureCommit();
        var service = TestData.NewService();
        var part = TestData.NewPart(stock: 4);
        var payment = TestData.NewPayment();
        await Store.CommitAsync(new BillingChanges().Save(service).Save(part).Save(payment), CancellationToken.None);
        var items = writes.TransactItems.Select(t => t.Put.Item).ToList();

        ReturnItem(items[0]);
        var loadedService = (await Store.GetServiceAsync(service.Id, CancellationToken.None))!;
        Assert.Equal(service.Description, loadedService.Description);
        Assert.Equal(1, loadedService.Version);

        ReturnItem(items[1]);
        var loadedPart = (await Store.GetPartAsync(part.Id, CancellationToken.None))!;
        Assert.Equal(4, loadedPart.QuantityInStock);
        Assert.Equal(80.50m, loadedPart.Price);

        ReturnItem(items[2]);
        var loadedPayment = (await Store.GetPaymentAsync(payment.Id, CancellationToken.None))!;
        Assert.Equal(PaymentStatus.Pending, loadedPayment.Status);
        Assert.Equal("pref-1", loadedPayment.GatewayTransactionId);
        Assert.Equal(TestData.Now, loadedPayment.CreatedAt);
    }

    [Fact]
    public async Task Lists_ShouldReadAllPagesOfThePartition()
    {
        var writes = CaptureCommit();
        var first = TestData.NewPart();
        var second = TestData.NewPart();
        await Store.CommitAsync(new BillingChanges().Save(first).Save(second), CancellationToken.None);

        _dynamo.SetupSequence(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse
            {
                Items = [writes.TransactItems[0].Put.Item],
                LastEvaluatedKey = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = "x" } }
            })
            .ReturnsAsync(new QueryResponse { Items = [writes.TransactItems[1].Put.Item] });

        var parts = await Store.ListPartsAsync(CancellationToken.None);

        Assert.Equal(2, parts.Count);
        _dynamo.Verify(x => x.QueryAsync(It.Is<QueryRequest>(r => r.ExpressionAttributeValues[":pk"].S == "PART"),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ListServices_ShouldQueryServicePartition()
    {
        var writes = CaptureCommit();
        await Store.CommitAsync(new BillingChanges().Save(TestData.NewService()), CancellationToken.None);
        _dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [writes.TransactItems[0].Put.Item] });

        Assert.Single(await Store.ListServicesAsync(CancellationToken.None));
        _dynamo.Verify(x => x.QueryAsync(It.Is<QueryRequest>(r => r.ExpressionAttributeValues[":pk"].S == "SERVICE"),
            It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_Claim_ShouldGuardActivePaymentOfTheOrder(bool hasPrevious)
    {
        var request = CaptureCommit();
        var orderId = Guid.NewGuid();
        var previous = Guid.NewGuid();
        var next = Guid.NewGuid();

        await Store.CommitAsync(new BillingChanges().ClaimOrder(orderId, hasPrevious ? previous : null, next), CancellationToken.None);

        var put = Assert.Single(request.TransactItems).Put;
        Assert.Equal($"ORDER#{orderId:D}", put.Item["pk"].S);
        Assert.Equal("ACTIVE_PAYMENT", put.Item["sk"].S);
        Assert.Equal(next.ToString("D"), put.Item["activePaymentId"].S);
        if (hasPrevious)
        {
            Assert.Equal("attribute_not_exists(pk) OR activePaymentId = :expected", put.ConditionExpression);
            Assert.Equal(previous.ToString("D"), put.ExpressionAttributeValues[":expected"].S);
        }
        else
        {
            Assert.Equal("attribute_not_exists(pk)", put.ConditionExpression);
        }
    }

    [Fact]
    public async Task ListPayments_ByOrder_ShouldFilterThePaymentPartition()
    {
        var writes = CaptureCommit();
        var orderId = Guid.NewGuid();
        await Store.CommitAsync(new BillingChanges().Save(TestData.NewPayment(orderId)), CancellationToken.None);
        _dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [writes.TransactItems[0].Put.Item] });

        var payments = await Store.ListPaymentsAsync(orderId, CancellationToken.None);

        Assert.Equal(orderId, Assert.Single(payments).ServiceOrderId);
        _dynamo.Verify(x => x.QueryAsync(It.Is<QueryRequest>(r =>
            r.FilterExpression == "serviceOrderId = :order" &&
            r.ExpressionAttributeValues[":order"].S == orderId.ToString("D") &&
            r.ExpressionAttributeValues[":pk"].S == "PAYMENT"), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ListPayments_WithoutOrder_ShouldNotFilter()
    {
        _dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [] });

        Assert.Empty(await Store.ListPaymentsAsync(null, CancellationToken.None));

        _dynamo.Verify(x => x.QueryAsync(It.Is<QueryRequest>(r => r.FilterExpression == null), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Commit_ProcessedEvent_ShouldBeConditionalWithTtl()
    {
        var request = CaptureCommit();
        var envelope = new EventEnvelope(Guid.NewGuid(), EventTypes.ServiceOrderApproved, TestData.Now, Guid.NewGuid(),
            System.Text.Json.JsonSerializer.SerializeToElement(new { }));

        await Store.CommitAsync(new BillingChanges().MarkProcessed(envelope), CancellationToken.None);

        var put = Assert.Single(request.TransactItems).Put;
        Assert.Equal($"EVENT#{envelope.EventId:D}", put.Item["pk"].S);
        Assert.Equal("PROCESSED", put.Item["sk"].S);
        Assert.Equal(EventTypes.ServiceOrderApproved, put.Item["eventType"].S);
        Assert.Equal("attribute_not_exists(pk)", put.ConditionExpression);
        Assert.True(long.Parse(put.Item["ttl"].N) > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task IsEventProcessed_ShouldReflectItemExistence()
    {
        _dynamo.SetupSequence(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { Item = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = "x" } } })
            .ReturnsAsync(new GetItemResponse { Item = [] });

        Assert.True(await Store.IsEventProcessedAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.False(await Store.IsEventProcessedAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Gets_WhenMissing_ShouldReturnNull()
    {
        ReturnItem(null);

        Assert.Null(await Store.GetServiceAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await Store.GetPartAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await Store.GetPaymentAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Outbox_ShouldRestoreMessageAndUseSameKeyToDeleteAndUpdate()
    {
        var writes = CaptureCommit();
        await Store.CommitAsync(new BillingChanges().Publish("Event", Guid.NewGuid(), new { }), CancellationToken.None);
        var item = writes.TransactItems[0].Put.Item;
        item["lastError"] = new AttributeValue { S = "error" };
        item["traceParent"] = new AttributeValue { S = "00-a-b-01" };
        _dynamo.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [item] });

        var message = Assert.Single(await Store.GetPendingAsync(10, CancellationToken.None));
        Assert.Equal("Event", message.EventType);
        Assert.Equal("error", message.LastError);
        Assert.Equal("00-a-b-01", message.TraceParent);

        await Store.MarkPublishedAsync(message, CancellationToken.None);
        await Store.MarkFailedAsync(message, "failure", CancellationToken.None);

        _dynamo.Verify(x => x.DeleteItemAsync(It.Is<DeleteItemRequest>(r => r.Key["sk"].S == item["sk"].S), It.IsAny<CancellationToken>()));
        _dynamo.Verify(x => x.UpdateItemAsync(It.Is<UpdateItemRequest>(r => r.Key["sk"].S == item["sk"].S), It.IsAny<CancellationToken>()));
    }
}

public class DynamoDbLocalIntegrationTests : IAsyncLifetime
{
    private sealed class DynamoDbLocalFactAttribute : FactAttribute
    {
        public DynamoDbLocalFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DYNAMODB_LOCAL_URL")))
                Skip = "DYNAMODB_LOCAL_URL is not set";
        }
    }

    private readonly string _table = $"oficina-billing-{Guid.NewGuid():N}";
    private AmazonDynamoDBClient? _client;
    private DynamoDbBillingStore Store => new(_client!, _table);

    public async Task InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("DYNAMODB_LOCAL_URL");
        if (string.IsNullOrWhiteSpace(url))
            return;

        _client = new AmazonDynamoDBClient(
            new Amazon.Runtime.BasicAWSCredentials("local", "local"),
            new AmazonDynamoDBConfig { ServiceURL = url });

        await _client.CreateTableAsync(new CreateTableRequest
        {
            TableName = _table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gatewayTransactionId", ScalarAttributeType.S)
            ],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "by-gateway-transaction",
                    KeySchema = [new KeySchemaElement("gatewayTransactionId", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL }
                }
            ]
        });
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    [DynamoDbLocalFact]
    public async Task FullCycle_ShouldWorkOnRealTable()
    {
        var ct = CancellationToken.None;

        var service = TestData.NewService();
        var part = TestData.NewPart(stock: 5);
        var payment = TestData.NewPayment();
        await Store.CommitAsync(new BillingChanges().Save(service).Save(part).Save(payment), ct);

        Assert.Single(await Store.ListServicesAsync(ct));
        Assert.Single(await Store.ListPartsAsync(ct));

        var fresh = (await Store.GetPartAsync(part.Id, ct))!;
        var stale = (await Store.GetPartAsync(part.Id, ct))!;
        fresh.AddStock(2);
        await Store.CommitAsync(new BillingChanges().Save(fresh).Publish(EventTypes.PartStockAdded, part.Id, new { }), ct);
        stale.RemoveStock(1);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Store.CommitAsync(new BillingChanges().Save(stale), ct));
        Assert.Equal(7, (await Store.GetPartAsync(part.Id, ct))!.QuantityInStock);

        await Store.CommitAsync(new BillingChanges().ClaimOrder(payment.ServiceOrderId, null, payment.Id), ct);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            Store.CommitAsync(new BillingChanges().ClaimOrder(payment.ServiceOrderId, null, Guid.NewGuid()), ct));
        await Store.CommitAsync(new BillingChanges().ClaimOrder(payment.ServiceOrderId, payment.Id, Guid.NewGuid()), ct);
        Assert.Single(await Store.ListPaymentsAsync(payment.ServiceOrderId, ct));
        Assert.Empty(await Store.ListPaymentsAsync(Guid.NewGuid(), ct));

        var loaded = (await Store.GetPaymentAsync(payment.Id, ct))!;
        loaded.Pay();
        await Store.CommitAsync(new BillingChanges().Save(loaded), ct);
        Assert.Equal(PaymentStatus.Paid, (await Store.GetPaymentAsync(payment.Id, ct))!.Status);

        var gsi = await _client!.QueryAsync(new QueryRequest
        {
            TableName = _table,
            IndexName = "by-gateway-transaction",
            KeyConditionExpression = "gatewayTransactionId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":id"] = new() { S = "pref-1" } }
        }, ct);
        Assert.Single(gsi.Items);

        var pending = Assert.Single(await Store.GetPendingAsync(10, ct));
        await Store.MarkFailedAsync(pending, "failure", ct);
        pending = Assert.Single(await Store.GetPendingAsync(10, ct));
        Assert.Equal(1, pending.Attempts);
        await Store.MarkPublishedAsync(pending, ct);
        Assert.Empty(await Store.GetPendingAsync(10, ct));
    }
}

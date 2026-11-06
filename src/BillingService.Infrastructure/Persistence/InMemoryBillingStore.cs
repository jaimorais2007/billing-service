using Amazon.DynamoDBv2.Model;
using BillingService.Application.Abstractions;
using BillingService.Application.Common;
using BillingService.Application.Messaging;
using BillingService.Domain.Common;
using BillingService.Domain.Entities;
using static BillingService.Infrastructure.Persistence.BillingItems;

namespace BillingService.Infrastructure.Persistence;

public class InMemoryBillingStore : IBillingStore, IOutboxStore
{
    private readonly object _lock = new();
    private readonly Dictionary<(string Partition, Guid Id), Dictionary<string, AttributeValue>> _items = [];
    private readonly Dictionary<Guid, Guid> _activePayments = [];
    private readonly HashSet<Guid> _processed = [];
    private readonly List<OutboxMessage> _outbox = [];

    public Task<IReadOnlyList<Service>> ListServicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Service>>(List(ServicePartition).Select(ToService).ToList());

    public Task<Service?> GetServiceAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Get(ServicePartition, id) is { } item ? ToService(item) : null);

    public Task<IReadOnlyList<Part>> ListPartsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Part>>(List(PartPartition).Select(ToPart).ToList());

    public Task<Part?> GetPartAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Get(PartPartition, id) is { } item ? ToPart(item) : null);

    public Task<PaymentTransaction?> GetPaymentAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Get(PaymentPartition, id) is { } item ? ToPayment(item) : null);

    public Task<IReadOnlyList<PaymentTransaction>> ListPaymentsAsync(Guid? serviceOrderId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaymentTransaction>>(List(PaymentPartition)
            .Select(ToPayment)
            .Where(p => serviceOrderId is null || p.ServiceOrderId == serviceOrderId)
            .ToList());

    public Task<bool> IsEventProcessedAsync(Guid eventId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_processed.Contains(eventId));
        }
    }

    public Task CommitAsync(BillingChanges changes, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var conflict =
                changes.Services.Any(s => CurrentVersion(ServicePartition, s.Id) != s.Version)
                || changes.Parts.Any(p => CurrentVersion(PartPartition, p.Id) != p.Version)
                || changes.Payments.Any(p => CurrentVersion(PaymentPartition, p.Id) != p.Version)
                || (changes.Claim is { } claim
                    && _activePayments.TryGetValue(claim.ServiceOrderId, out var active)
                    && active != claim.ExpectedActivePaymentId)
                || (changes.ProcessedEvent is { } processed && _processed.Contains(processed.EventId));

            if (conflict)
                throw new ConcurrencyConflictException("The record was changed by another operation. Please try again.");

            foreach (var s in changes.Services)
                Put(ServicePartition, s.Id, ToItem(s), s);
            foreach (var p in changes.Parts)
                Put(PartPartition, p.Id, ToItem(p), p);
            foreach (var p in changes.Payments)
                Put(PaymentPartition, p.Id, ToItem(p), p);

            if (changes.Claim is { } newClaim)
                _activePayments[newClaim.ServiceOrderId] = newClaim.NewActivePaymentId;
            if (changes.ProcessedEvent is { } newProcessed)
                _processed.Add(newProcessed.EventId);

            _outbox.AddRange(changes.Outbox);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(_outbox.OrderBy(m => m.OccurredAt).Take(limit).ToList());
        }
    }

    public Task MarkPublishedAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _outbox.RemoveAll(m => m.Id == message.Id);
        }

        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(OutboxMessage message, string error, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = _outbox.FindIndex(m => m.Id == message.Id);
            if (index >= 0)
            {
                _outbox[index] = OutboxMessage.Restore(message.Id, message.EventType, message.CorrelationId, message.Content,
                    message.TraceParent, message.OccurredAt, message.Attempts + 1, OutboxMessage.TruncateError(error));
            }
        }

        return Task.CompletedTask;
    }

    private void Put(string partition, Guid id, Dictionary<string, AttributeValue> item, VersionedEntity entity)
    {
        item["version"] = N(entity.Version + 1);
        _items[(partition, id)] = item;
        entity.MarkPersisted();
    }

    private Dictionary<string, AttributeValue>? Get(string partition, Guid id)
    {
        lock (_lock)
        {
            return _items.GetValueOrDefault((partition, id));
        }
    }

    private List<Dictionary<string, AttributeValue>> List(string partition)
    {
        lock (_lock)
        {
            return _items.Where(i => i.Key.Partition == partition).Select(i => i.Value).ToList();
        }
    }

    private int CurrentVersion(string partition, Guid id) =>
        _items.TryGetValue((partition, id), out var item) ? int.Parse(item["version"].N) : 0;
}

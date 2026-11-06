using BillingService.Application.Messaging;
using BillingService.Domain.Entities;

namespace BillingService.Application.Abstractions;

public sealed record OrderPaymentClaim(Guid ServiceOrderId, Guid? ExpectedActivePaymentId, Guid NewActivePaymentId);

public sealed class BillingChanges
{
    private readonly Dictionary<Guid, Service> _services = [];
    private readonly Dictionary<Guid, Part> _parts = [];
    private readonly Dictionary<Guid, PaymentTransaction> _payments = [];
    private readonly List<OutboxMessage> _outbox = [];

    public IReadOnlyCollection<Service> Services => _services.Values;
    public IReadOnlyCollection<Part> Parts => _parts.Values;
    public IReadOnlyCollection<PaymentTransaction> Payments => _payments.Values;
    public IReadOnlyList<OutboxMessage> Outbox => _outbox;

    public OrderPaymentClaim? Claim { get; private set; }
    public EventEnvelope? ProcessedEvent { get; private set; }

    public bool HasChanges =>
        _services.Count > 0 || _parts.Count > 0 || _payments.Count > 0 || _outbox.Count > 0
        || Claim is not null || ProcessedEvent is not null;

    public BillingChanges MarkProcessed(EventEnvelope envelope)
    {
        ProcessedEvent = envelope;
        return this;
    }

    public BillingChanges ClaimOrder(Guid serviceOrderId, Guid? expectedActivePaymentId, Guid newActivePaymentId)
    {
        Claim = new OrderPaymentClaim(serviceOrderId, expectedActivePaymentId, newActivePaymentId);
        return this;
    }

    public BillingChanges Save(Service service)
    {
        _services[service.Id] = service;
        return this;
    }

    public BillingChanges Save(Part part)
    {
        _parts[part.Id] = part;
        return this;
    }

    public BillingChanges Save(PaymentTransaction payment)
    {
        _payments[payment.Id] = payment;
        return this;
    }

    public BillingChanges Publish<TPayload>(string eventType, Guid correlationId, TPayload payload)
    {
        _outbox.Add(OutboxMessage.Create(eventType, correlationId, payload));
        return this;
    }
}

public interface IBillingStore
{
    Task<IReadOnlyList<Service>> ListServicesAsync(CancellationToken cancellationToken);

    Task<Service?> GetServiceAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Part>> ListPartsAsync(CancellationToken cancellationToken);

    Task<Part?> GetPartAsync(Guid id, CancellationToken cancellationToken);

    Task<PaymentTransaction?> GetPaymentAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PaymentTransaction>> ListPaymentsAsync(Guid? serviceOrderId, CancellationToken cancellationToken);

    Task<bool> IsEventProcessedAsync(Guid eventId, CancellationToken cancellationToken);

    Task CommitAsync(BillingChanges changes, CancellationToken cancellationToken);
}

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int limit, CancellationToken cancellationToken);

    Task MarkPublishedAsync(OutboxMessage message, CancellationToken cancellationToken);

    Task MarkFailedAsync(OutboxMessage message, string error, CancellationToken cancellationToken);
}

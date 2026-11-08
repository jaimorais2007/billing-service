using System.Text.Json;
using BillingService.Application.Abstractions;
using BillingService.Application.Messaging;
using BillingService.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BillingService.Application.Payments;

public class ServiceOrderRejectedHandler(
    IBillingStore store,
    ILogger<ServiceOrderRejectedHandler> logger) : IIntegrationEventHandler
{
    public string EventType => EventTypes.ServiceOrderRejected;

    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await store.IsEventProcessedAsync(envelope.EventId, cancellationToken))
        {
            logger.LogInformation("Event {EventId} already processed, ignoring duplicate delivery", envelope.EventId);
            return;
        }

        var payload = envelope.Payload.Deserialize<ServiceOrderRejectedPayload>(EventJson.Options)
                      ?? throw new InvalidOperationException($"Empty payload in event {envelope.EventId}.");

        var serviceOrderId = payload.ServiceOrderId != Guid.Empty ? payload.ServiceOrderId : envelope.CorrelationId;
        var changes = new BillingChanges().MarkProcessed(envelope);

        var payments = await store.ListPaymentsAsync(serviceOrderId, cancellationToken);
        foreach (var payment in payments.Where(p => p.Status == PaymentStatus.Pending))
        {
            if (payment.Cancel())
            {
                changes.Save(payment);
                logger.LogInformation(
                    "Pending payment {PaymentId} cancelled due to rejected service order {ServiceOrderId}",
                    payment.Id,
                    serviceOrderId);
            }
        }

        await store.CommitAsync(changes, cancellationToken);
    }
}

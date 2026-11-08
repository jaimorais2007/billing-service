using System.Text.Json;
using BillingService.Application.Abstractions;
using BillingService.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace BillingService.Application.Payments;

public class BudgetApprovedHandler(
    IBillingStore store,
    PaymentAppService paymentAppService,
    ILogger<BudgetApprovedHandler> logger) : IIntegrationEventHandler
{
    public string EventType => EventTypes.BudgetApproved;

    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await store.IsEventProcessedAsync(envelope.EventId, cancellationToken))
        {
            logger.LogInformation("Event {EventId} already processed, ignoring duplicate delivery", envelope.EventId);
            return;
        }

        var payload = envelope.Payload.Deserialize<BudgetApprovedPayload>(EventJson.Options)
                      ?? throw new InvalidOperationException($"Empty payload in event {envelope.EventId}.");

        await paymentAppService.CreateFromBudgetApprovedAsync(envelope, payload, cancellationToken);
    }
}

using System.Text.Json;
using BillingService.Application.Abstractions;
using BillingService.Application.Messaging;
using BillingService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace BillingService.Application.Catalog;

public class ServiceOrderApprovedHandler(IBillingStore store, ILogger<ServiceOrderApprovedHandler> logger) : IIntegrationEventHandler
{
    public string EventType => EventTypes.ServiceOrderApproved;

    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await store.IsEventProcessedAsync(envelope.EventId, cancellationToken))
        {
            logger.LogInformation("Event {EventId} already processed, ignoring duplicate delivery", envelope.EventId);
            return;
        }

        var payload = envelope.Payload.Deserialize<ServiceOrderApprovedPayload>(EventJson.Options)
                      ?? throw new InvalidOperationException($"Empty payload in event {envelope.EventId}.");
        var serviceOrderId = payload.ServiceOrderId != Guid.Empty ? payload.ServiceOrderId : envelope.CorrelationId;

        var changes = new BillingChanges().MarkProcessed(envelope);
        var parts = new Dictionary<Guid, Part>();

        foreach (var item in payload.Parts ?? [])
        {
            if (!parts.TryGetValue(item.PartId, out var part))
            {
                part = await store.GetPartAsync(item.PartId, cancellationToken);
                if (part is null)
                {
                    logger.LogWarning("Part {PartId} of service order {ServiceOrderId} not found; stock not ensured",
                        item.PartId, serviceOrderId);
                    continue;
                }

                parts[part.Id] = part;
            }

            if (!part.TryDebitForServiceOrder(item.Quantity))
            {
                logger.LogInformation(
                    "Insufficient stock for part {PartId} of service order {ServiceOrderId}: requested {Quantity}, in stock {Stock}",
                    part.Id, serviceOrderId, item.Quantity, part.QuantityInStock);
                continue;
            }

            changes.Save(part).Publish(
                EventTypes.PartStockEnsured,
                serviceOrderId,
                new PartStockEnsuredPayload(serviceOrderId, part.Id, item.Quantity));
        }

        await store.CommitAsync(changes, cancellationToken);
    }
}

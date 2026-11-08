using System.Text.Json;
using BillingService.Application.Abstractions;
using BillingService.Application.Messaging;
using BillingService.Domain.Entities;
using BillingService.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BillingService.Application.Catalog;

public class ServiceOrderApprovedHandler(
    IBillingStore store,
    IPaymentGateway gateway,
    ILogger<ServiceOrderApprovedHandler> logger) : IIntegrationEventHandler
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

        var requestedItems = payload.Parts ?? [];
        if (requestedItems.Count == 0)
        {
            await store.CommitAsync(new BillingChanges().MarkProcessed(envelope), cancellationToken);
            return;
        }

        var parts = new Dictionary<Guid, Part>();
        string? failureReason = null;

        foreach (var item in requestedItems)
        {
            if (item.Quantity <= 0)
            {
                failureReason = $"Invalid requested quantity ({item.Quantity}) for part {item.PartId}.";
                logger.LogWarning(
                    "Invalid quantity {Quantity} for part {PartId} of service order {ServiceOrderId}",
                    item.Quantity,
                    item.PartId,
                    serviceOrderId);
                break;
            }

            if (!parts.TryGetValue(item.PartId, out var part))
            {
                part = await store.GetPartAsync(item.PartId, cancellationToken);
                if (part is null)
                {
                    failureReason = $"Part {item.PartId} not found in catalog.";
                    logger.LogWarning(
                        "Part {PartId} of service order {ServiceOrderId} not found; stock cannot be ensured",
                        item.PartId,
                        serviceOrderId);
                    break;
                }

                parts[part.Id] = part;
            }
        }

        if (failureReason is null)
        {
            foreach (var group in requestedItems.GroupBy(i => i.PartId))
            {
                var part = parts[group.Key];
                var totalRequested = group.Sum(i => i.Quantity);
                if (part.QuantityInStock < totalRequested)
                {
                    failureReason = $"Insufficient stock for part {part.Id}: requested {totalRequested}, available {part.QuantityInStock}.";
                    logger.LogInformation(
                        "Insufficient stock for part {PartId} of service order {ServiceOrderId}: requested {Quantity}, in stock {Stock}",
                        part.Id,
                        serviceOrderId,
                        totalRequested,
                        part.QuantityInStock);
                    break;
                }
            }
        }

        var changes = new BillingChanges().MarkProcessed(envelope);

        if (failureReason is not null)
        {
            var payments = await store.ListPaymentsAsync(serviceOrderId, cancellationToken);
            var paidPayment = payments.FirstOrDefault(p => p.Status == PaymentStatus.Paid);

            if (paidPayment is not null)
            {
                var gatewayRefundId = !string.IsNullOrWhiteSpace(paidPayment.GatewayPaymentId)
                    ? paidPayment.GatewayPaymentId
                    : paidPayment.GatewayTransactionId;

                await gateway.RefundPaymentAsync(gatewayRefundId, cancellationToken);

                if (paidPayment.Refund())
                {
                    changes.Save(paidPayment).Publish(
                        EventTypes.PaymentRefunded,
                        serviceOrderId,
                        new PaymentRefundedPayload(serviceOrderId, paidPayment.GatewayTransactionId, failureReason));

                    logger.LogWarning(
                        "Compensation 2 executed for service order {ServiceOrderId}: payment {PaymentId} refunded ({Reason})",
                        serviceOrderId,
                        paidPayment.Id,
                        failureReason);
                }
            }

            await store.CommitAsync(changes, cancellationToken);
            return;
        }

        foreach (var item in requestedItems)
        {
            var part = parts[item.PartId];
            part.TryDebitForServiceOrder(item.Quantity);

            changes.Save(part).Publish(
                EventTypes.PartStockEnsured,
                serviceOrderId,
                new PartStockEnsuredPayload(serviceOrderId, part.Id, item.Quantity));
        }

        await store.CommitAsync(changes, cancellationToken);
    }
}

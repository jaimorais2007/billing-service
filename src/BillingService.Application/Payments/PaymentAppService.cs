using BillingService.Application.Abstractions;
using BillingService.Application.Messaging;
using BillingService.Domain.Entities;
using BillingService.Domain.Enums;
using BillingService.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace BillingService.Application.Payments;

public sealed record PaymentPartRequest(Guid PartId, int Quantity);

public sealed record CreatePaymentRequest(
    decimal Value,
    Guid ServiceOrderId,
    IReadOnlyList<Guid>? ServiceIds,
    IReadOnlyList<PaymentPartRequest>? Parts);

public sealed record PaymentCreatedDto(string PaymentUrl, PaymentStatus Status, decimal TotalAmount);

public enum NotificationResult
{
    Processed,
    TransactionNotFound
}

public class PaymentAppService(
    IBillingStore store,
    IPaymentGateway gateway,
    TimeProvider clock,
    ILogger<PaymentAppService> logger)
{
    private const string ApprovedStatus = "approved";
    private static readonly string[] RefusedStatuses = ["rejected", "cancelled"];

    public async Task<PaymentCreatedDto> CreateAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        if (request.Value <= 0)
            throw new DomainException("The payment value must be greater than zero.");
        if (request.ServiceOrderId == Guid.Empty)
            throw new DomainException("The payment must be linked to a service order.");

        await EnsureItemsAreValidAsync(request, cancellationToken);

        var paymentId = Guid.NewGuid();
        var charge = await gateway.CreateChargeAsync(
            new ChargeRequest(paymentId, request.ServiceOrderId, request.Value),
            cancellationToken);

        var payment = PaymentTransaction.Create(
            paymentId, request.ServiceOrderId, request.Value, charge.GatewayTransactionId, charge.PaymentUrl, clock.GetUtcNow().UtcDateTime);

        await store.CommitAsync(new BillingChanges().Save(payment), cancellationToken);

        logger.LogInformation("Payment {PaymentId} created for service order {ServiceOrderId}", paymentId, request.ServiceOrderId);
        return new PaymentCreatedDto(payment.PaymentUrl, payment.Status, payment.TotalAmount);
    }

    public async Task<NotificationResult> HandleGatewayNotificationAsync(string transactionId, CancellationToken cancellationToken)
    {
        var transaction = await gateway.GetPaymentAsync(transactionId, cancellationToken);
        var payment = Guid.TryParse(transaction?.ExternalReference, out var paymentId)
            ? await store.GetPaymentAsync(paymentId, cancellationToken)
            : null;

        if (transaction is null || payment is null)
        {
            logger.LogWarning("Payment transaction for gateway transaction {TransactionId} not found", transactionId);
            return NotificationResult.TransactionNotFound;
        }

        var changes = new BillingChanges();

        if (transaction.Status == ApprovedStatus)
        {
            if (AmountMatches(payment, transaction) && payment.Pay())
                changes.Save(payment).Publish(
                    EventTypes.PaymentConfirmed,
                    payment.ServiceOrderId,
                    new PaymentConfirmedPayload(payment.ServiceOrderId, payment.GatewayTransactionId));
        }
        else if (RefusedStatuses.Contains(transaction.Status) && payment.Refuse())
        {
            changes.Save(payment).Publish(
                EventTypes.PaymentExpired,
                payment.ServiceOrderId,
                new PaymentExpiredPayload(payment.ServiceOrderId, payment.GatewayTransactionId));
        }

        if (changes.HasChanges)
            await store.CommitAsync(changes, cancellationToken);

        return NotificationResult.Processed;
    }

    private bool AmountMatches(PaymentTransaction payment, GatewayPayment transaction)
    {
        if (transaction.Amount == payment.TotalAmount
            && string.Equals(transaction.Currency, PaymentTransaction.Currency, StringComparison.OrdinalIgnoreCase))
            return true;

        logger.LogError(
            "Transaction {TransactionId} of {Amount} {Currency} does not match payment {PaymentId} of {Expected} {ExpectedCurrency}; it will not be marked as paid",
            transaction.TransactionId, transaction.Amount, transaction.Currency, payment.Id, payment.TotalAmount, PaymentTransaction.Currency);
        return false;
    }

    private async Task EnsureItemsAreValidAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var parts = request.Parts ?? [];
        if (parts.Any(p => p.Quantity <= 0))
            throw new DomainException("Invalid or inactive part or service.");

        foreach (var serviceId in (request.ServiceIds ?? []).Distinct())
        {
            if (await store.GetServiceAsync(serviceId, cancellationToken) is not { Inactive: false })
                throw new DomainException("Invalid or inactive part or service.");
        }

        foreach (var partId in parts.Select(p => p.PartId).Distinct())
        {
            if (await store.GetPartAsync(partId, cancellationToken) is not { Inactive: false })
                throw new DomainException("Invalid or inactive part or service.");
        }
    }
}

using BillingService.Application.Abstractions;
using BillingService.Application.Common;
using BillingService.Application.Messaging;
using BillingService.Domain.Entities;
using BillingService.Domain.Enums;
using BillingService.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace BillingService.Application.Payments;

public sealed record PaymentPartRequest(Guid PartId, int Quantity);

public sealed record CreatePaymentRequest(
    Guid ServiceOrderId,
    decimal? Value = null,
    decimal? TotalAmount = null,
    IReadOnlyList<Guid>? ServiceIds = null,
    IReadOnlyList<PaymentPartRequest>? Parts = null);

public sealed record PaymentCreatedDto(string PaymentUrl, PaymentStatus Status, decimal TotalAmount, string TransactionId);

public sealed record CreatePaymentOutcome(PaymentCreatedDto Payment, bool Created);

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

    public async Task<CreatePaymentOutcome> CreateAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        if (request.Value is not null && request.TotalAmount is not null && request.Value != request.TotalAmount)
            throw new DomainException("Value and TotalAmount must be equal when both are informed.");

        var amount = request.Value ?? request.TotalAmount ?? 0m;
        if (amount <= 0)
            throw new DomainException("The payment value must be greater than zero.");
        if (request.ServiceOrderId == Guid.Empty)
            throw new DomainException("The payment must be linked to a service order.");

        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await store.ListPaymentsAsync(request.ServiceOrderId, cancellationToken);

        if (existing.Any(p => p.Status == PaymentStatus.Paid))
            throw new DomainException("This service order already has a paid payment.");

        var open = existing.FirstOrDefault(p => p.IsOpenAt(now, gateway.CheckoutLifetime));
        if (open is not null)
        {
            if (open.TotalAmount != amount)
                throw new ConcurrencyConflictException("This service order already has an open payment with a different value.");

            return new CreatePaymentOutcome(ToDto(open), false);
        }

        await EnsureItemsAreValidAsync(request, cancellationToken);

        var paymentId = Guid.NewGuid();
        var charge = await gateway.CreateChargeAsync(
            new ChargeRequest(paymentId, request.ServiceOrderId, amount, now.Add(gateway.CheckoutLifetime)),
            cancellationToken);

        var payment = PaymentTransaction.Create(
            paymentId, request.ServiceOrderId, amount, charge.GatewayTransactionId, charge.PaymentUrl, now);
        var latest = existing.Reverse().OrderByDescending(p => p.CreatedAt).FirstOrDefault();

        await store.CommitAsync(
            new BillingChanges().Save(payment).ClaimOrder(request.ServiceOrderId, latest?.Id, paymentId),
            cancellationToken);

        logger.LogInformation("Payment {PaymentId} created for service order {ServiceOrderId}", paymentId, request.ServiceOrderId);
        return new CreatePaymentOutcome(ToDto(payment), true);
    }

    public async Task CreateFromBudgetApprovedAsync(
        EventEnvelope envelope,
        BudgetApprovedPayload payload,
        CancellationToken cancellationToken)
    {
        var serviceOrderId = payload.ServiceOrderId != Guid.Empty ? payload.ServiceOrderId : envelope.CorrelationId;

        try
        {
            if (serviceOrderId == Guid.Empty)
                throw new DomainException("The payment must be linked to a service order.");

            if (payload.TotalAmount <= 0)
                throw new DomainException("The payment value must be greater than zero.");

            var now = clock.GetUtcNow().UtcDateTime;
            var existing = await store.ListPaymentsAsync(serviceOrderId, cancellationToken);

            if (existing.Any(p => p.Status == PaymentStatus.Paid))
                throw new DomainException("This service order already has a paid payment.");

            var open = existing.FirstOrDefault(p => p.IsOpenAt(now, gateway.CheckoutLifetime));
            if (open is not null)
            {
                if (open.TotalAmount != payload.TotalAmount)
                    throw new ConcurrencyConflictException("This service order already has an open payment with a different value.");

                await store.CommitAsync(
                    new BillingChanges()
                        .MarkProcessed(envelope)
                        .Publish(
                            EventTypes.PaymentCreated,
                            serviceOrderId,
                            new PaymentCreatedPayload(serviceOrderId, open.Id, open.PaymentUrl, open.GatewayTransactionId)),
                    cancellationToken);
                return;
            }

            var request = new CreatePaymentRequest(
                serviceOrderId,
                TotalAmount: payload.TotalAmount,
                ServiceIds: payload.ServiceIds,
                Parts: payload.Parts?.Select(p => new PaymentPartRequest(p.PartId, p.Quantity)).ToList());

            await EnsureItemsAreValidAsync(request, cancellationToken);

            var paymentId = Guid.NewGuid();
            var charge = await gateway.CreateChargeAsync(
                new ChargeRequest(paymentId, serviceOrderId, payload.TotalAmount, now.Add(gateway.CheckoutLifetime)),
                cancellationToken);

            var payment = PaymentTransaction.Create(
                paymentId, serviceOrderId, payload.TotalAmount, charge.GatewayTransactionId, charge.PaymentUrl, now);
            var latest = existing.Reverse().OrderByDescending(p => p.CreatedAt).FirstOrDefault();

            await store.CommitAsync(
                new BillingChanges()
                    .Save(payment)
                    .ClaimOrder(serviceOrderId, latest?.Id, paymentId)
                    .MarkProcessed(envelope)
                    .Publish(
                        EventTypes.PaymentCreated,
                        serviceOrderId,
                        new PaymentCreatedPayload(serviceOrderId, payment.Id, payment.PaymentUrl, payment.GatewayTransactionId)),
                cancellationToken);

            logger.LogInformation(
                "Payment {PaymentId} created via Saga BudgetApproved for service order {ServiceOrderId}",
                paymentId,
                serviceOrderId);
        }
        catch (Exception ex) when (ex is DomainException or ConcurrencyConflictException or PaymentGatewayException)
        {
            logger.LogWarning(
                ex,
                "Failed to create payment via Saga BudgetApproved for service order {ServiceOrderId}: {Reason}",
                serviceOrderId,
                ex.Message);

            await store.CommitAsync(
                new BillingChanges()
                    .MarkProcessed(envelope)
                    .Publish(
                        EventTypes.PaymentCreationFailed,
                        serviceOrderId,
                        new PaymentCreationFailedPayload(serviceOrderId, ex.Message)),
                cancellationToken);
        }
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

        await ApplyAsync(payment, transaction, cancellationToken);
        return NotificationResult.Processed;
    }

    public async Task ExpireOverdueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var overdue = (await store.ListPaymentsAsync(null, cancellationToken))
            .Where(p => p.Status == PaymentStatus.Pending && !p.IsOpenAt(now, gateway.CheckoutLifetime))
            .ToList();

        foreach (var payment in overdue)
        {
            var approved = (await gateway.SearchPaymentsAsync(payment.Id.ToString("D"), cancellationToken))
                .FirstOrDefault(t => t.Status == ApprovedStatus);
            if (approved is not null)
                await ApplyAsync(payment, approved, cancellationToken);

            if (payment.Cancel())
            {
                await store.CommitAsync(
                    new BillingChanges()
                        .Save(payment)
                        .Publish(EventTypes.PaymentExpired, payment.ServiceOrderId,
                            new PaymentExpiredPayload(payment.ServiceOrderId, payment.GatewayTransactionId)),
                    cancellationToken);

                logger.LogInformation("Payment {PaymentId} for service order {ServiceOrderId} expired", payment.Id, payment.ServiceOrderId);
            }
        }
    }

    private async Task ApplyAsync(PaymentTransaction payment, GatewayPayment transaction, CancellationToken cancellationToken)
    {
        var changes = new BillingChanges();

        if (transaction.Status == ApprovedStatus)
        {
            if (await CanPayAsync(payment, transaction, cancellationToken) && payment.Pay(transaction.TransactionId))
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
    }

    private async Task<bool> CanPayAsync(PaymentTransaction payment, GatewayPayment transaction, CancellationToken cancellationToken)
    {
        if (transaction.Amount != payment.TotalAmount
            || !string.Equals(transaction.Currency, PaymentTransaction.Currency, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "Transaction {TransactionId} of {Amount} {Currency} does not match payment {PaymentId} of {Expected} {ExpectedCurrency}; it will not be marked as paid",
                transaction.TransactionId, transaction.Amount, transaction.Currency, payment.Id, payment.TotalAmount, PaymentTransaction.Currency);
            return false;
        }

        var others = await store.ListPaymentsAsync(payment.ServiceOrderId, cancellationToken);
        if (others.Any(p => p.Id != payment.Id && p.Status == PaymentStatus.Paid))
        {
            logger.LogError(
                "Duplicate charge on service order {ServiceOrderId}: transaction {TransactionId} approved while another payment is already paid. Manual refund required",
                payment.ServiceOrderId, transaction.TransactionId);
            return false;
        }

        return true;
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

    private static PaymentCreatedDto ToDto(PaymentTransaction payment) =>
        new(payment.PaymentUrl, payment.Status, payment.TotalAmount, payment.GatewayTransactionId);
}

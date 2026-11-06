using BillingService.Domain.Common;
using BillingService.Domain.Enums;
using BillingService.Domain.Exceptions;

namespace BillingService.Domain.Entities;

public class PaymentTransaction : VersionedEntity
{
    public const string Currency = "BRL";

    public Guid Id { get; private set; }
    public Guid ServiceOrderId { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string GatewayTransactionId { get; private set; } = string.Empty;
    public string PaymentUrl { get; private set; } = string.Empty;
    public PaymentStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private PaymentTransaction()
    {
    }

    public static PaymentTransaction Create(
        Guid id,
        Guid serviceOrderId,
        decimal totalAmount,
        string gatewayTransactionId,
        string paymentUrl,
        DateTime now)
    {
        if (serviceOrderId == Guid.Empty)
            throw new DomainException("The payment must be linked to a service order.");
        if (totalAmount <= 0)
            throw new DomainException("The payment amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(gatewayTransactionId) || string.IsNullOrWhiteSpace(paymentUrl))
            throw new DomainException("The payment needs a charge created by the payment gateway.");

        return new PaymentTransaction
        {
            Id = id,
            ServiceOrderId = serviceOrderId,
            TotalAmount = totalAmount,
            GatewayTransactionId = gatewayTransactionId,
            PaymentUrl = paymentUrl,
            Status = PaymentStatus.Pending,
            CreatedAt = now
        };
    }

    public static PaymentTransaction Restore(
        Guid id,
        Guid serviceOrderId,
        decimal totalAmount,
        string gatewayTransactionId,
        string paymentUrl,
        PaymentStatus status,
        DateTime createdAt,
        int version) => new()
    {
        Id = id,
        ServiceOrderId = serviceOrderId,
        TotalAmount = totalAmount,
        GatewayTransactionId = gatewayTransactionId,
        PaymentUrl = paymentUrl,
        Status = status,
        CreatedAt = createdAt,
        Version = version
    };

    public bool IsOpenAt(DateTime now, TimeSpan lifetime) =>
        Status is PaymentStatus.Pending or PaymentStatus.Refused && CreatedAt.Add(lifetime) > now;

    public bool Pay()
    {
        if (Status is not (PaymentStatus.Pending or PaymentStatus.Refused or PaymentStatus.Cancelled))
            return false;

        Status = PaymentStatus.Paid;
        return true;
    }

    public bool Cancel()
    {
        if (Status != PaymentStatus.Pending)
            return false;

        Status = PaymentStatus.Cancelled;
        return true;
    }

    public bool Refuse()
    {
        if (Status != PaymentStatus.Pending)
            return false;

        Status = PaymentStatus.Refused;
        return true;
    }
}

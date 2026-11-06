namespace BillingService.Application.Abstractions;

public sealed record ChargeRequest(Guid PaymentId, Guid ServiceOrderId, decimal TotalAmount);

public sealed record ChargeResult(string GatewayTransactionId, string PaymentUrl);

public sealed record GatewayPayment(
    string TransactionId,
    string Status,
    string? ExternalReference,
    decimal Amount,
    string? Currency);

public interface IPaymentGateway
{
    Task<ChargeResult> CreateChargeAsync(ChargeRequest request, CancellationToken cancellationToken);

    Task<GatewayPayment?> GetPaymentAsync(string transactionId, CancellationToken cancellationToken);
}

public interface IWebhookSignatureValidator
{
    bool IsValid(string? signatureHeader, string? requestId, string? dataId);
}

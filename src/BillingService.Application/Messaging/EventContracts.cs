using System.Text.Json;
using System.Text.Json.Serialization;

namespace BillingService.Application.Messaging;

public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    DateTime OccurredAt,
    Guid CorrelationId,
    JsonElement Payload);

public interface IIntegrationEventHandler
{
    string EventType { get; }

    Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}

public static class EventTypes
{
    public const string ServiceOrderApproved = "ServiceOrderApproved";

    public const string PartStockEnsured = "PartStockEnsured";
    public const string ServiceDeleted = "ServiceDeleted";
    public const string PartDeleted = "PartDeleted";
    public const string PartStockAdded = "PartStockAdded";
    public const string PaymentConfirmed = "PaymentConfirmed";
    public const string PaymentExpired = "PaymentExpired";
}

public static class EventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record ServiceOrderApprovedPart(Guid PartId, int Quantity);

public sealed record ServiceOrderApprovedPayload(Guid ServiceOrderId, IReadOnlyList<ServiceOrderApprovedPart>? Parts);

public sealed record PartStockEnsuredPayload(Guid ServiceOrderId, Guid PartId, int Quantity);

public sealed record ServiceDeletedPayload(Guid ServiceId);

public sealed record PartDeletedPayload(Guid PartId);

public sealed record PartStockAddedPayload(Guid PartId, int AvailableQuantity);

public sealed record PaymentConfirmedPayload(Guid ServiceOrderId, string GatewayTransactionId);

public sealed record PaymentExpiredPayload(Guid ServiceOrderId, string GatewayTransactionId);

using System.Text.Json;
using System.Text.Json.Serialization;

namespace BillingService.Application.Messaging;

public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    DateTime OccurredAt,
    Guid CorrelationId,
    JsonElement Payload);

public static class EventTypes
{
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

public sealed record ServiceDeletedPayload(Guid ServiceId);

public sealed record PartDeletedPayload(Guid PartId);

public sealed record PartStockAddedPayload(Guid PartId, int AvailableQuantity);

public sealed record PaymentConfirmedPayload(Guid ServiceOrderId, string GatewayTransactionId);

public sealed record PaymentExpiredPayload(Guid ServiceOrderId, string GatewayTransactionId);

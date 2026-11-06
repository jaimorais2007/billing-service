using System.Diagnostics;
using System.Text.Json;

namespace BillingService.Application.Messaging;

public class OutboxMessage
{
    public const int MaxErrorLength = 2000;

    public Guid Id { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public Guid CorrelationId { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public string? TraceParent { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    private OutboxMessage()
    {
    }

    public static OutboxMessage Create<TPayload>(string eventType, Guid correlationId, TPayload payload)
    {
        var id = Guid.NewGuid();
        var occurredAt = DateTime.UtcNow;
        var envelope = new EventEnvelope(
            id,
            eventType,
            occurredAt,
            correlationId,
            JsonSerializer.SerializeToElement(payload, EventJson.Options));

        return new OutboxMessage
        {
            Id = id,
            EventType = eventType,
            CorrelationId = correlationId,
            Content = JsonSerializer.Serialize(envelope, EventJson.Options),
            TraceParent = Activity.Current?.Id,
            OccurredAt = occurredAt
        };
    }

    public static OutboxMessage Restore(
        Guid id,
        string eventType,
        Guid correlationId,
        string content,
        string? traceParent,
        DateTime occurredAt,
        int attempts,
        string? lastError) => new()
    {
        Id = id,
        EventType = eventType,
        CorrelationId = correlationId,
        Content = content,
        TraceParent = traceParent,
        OccurredAt = occurredAt,
        Attempts = attempts,
        LastError = lastError
    };

    public static string TruncateError(string error) =>
        error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
}

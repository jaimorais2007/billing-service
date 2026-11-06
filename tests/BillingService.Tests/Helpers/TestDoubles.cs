using System.Text.Json;
using BillingService.Application.Abstractions;
using BillingService.Application.Messaging;
using BillingService.Domain.Entities;

namespace BillingService.Tests.Helpers;

public class FakeClock(DateTime start) : TimeProvider
{
    public DateTime Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

public class FakeGateway : IPaymentGateway
{
    public List<ChargeRequest> Charges { get; } = [];
    public Dictionary<string, GatewayPayment> Transactions { get; } = [];
    public Exception? ChargeFailure { get; set; }

    public Task<ChargeResult> CreateChargeAsync(ChargeRequest request, CancellationToken cancellationToken)
    {
        if (ChargeFailure is not null)
            throw ChargeFailure;

        Charges.Add(request);
        return Task.FromResult(new ChargeResult($"pref-{request.PaymentId:N}", $"https://mp.test/{request.PaymentId:N}"));
    }

    public Task<GatewayPayment?> GetPaymentAsync(string transactionId, CancellationToken cancellationToken) =>
        Task.FromResult(Transactions.GetValueOrDefault(transactionId));
}

public static class TestData
{
    public static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    public static Part NewPart(int stock = 10, string? code = null) =>
        Part.Create("Brake pad", code ?? $"BP-{Guid.NewGuid():N}", stock, 80.50m, Now);

    public static Service NewService() => Service.Create("Brake pad replacement", "Front axle", 150m, Now);

    public static PaymentTransaction NewPayment(Guid? serviceOrderId = null) =>
        PaymentTransaction.Create(Guid.NewGuid(), serviceOrderId ?? Guid.NewGuid(), 311m, "pref-1", "https://mp.test/x", Now);

    public static async Task<List<EventEnvelope>> PendingEventsAsync(IOutboxStore store) =>
        (await store.GetPendingAsync(100, CancellationToken.None))
        .Select(m => JsonSerializer.Deserialize<EventEnvelope>(m.Content, EventJson.Options)!)
        .ToList();
}

using BillingService.Application.Abstractions;
using BillingService.Application.Common;
using BillingService.Application.Messaging;
using BillingService.Application.Payments;
using BillingService.Domain.Entities;
using BillingService.Domain.Enums;
using BillingService.Domain.Exceptions;
using BillingService.Infrastructure.Persistence;
using BillingService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingService.Tests.Application;

public class PaymentFlowTests
{
    private const decimal Value = 311m;

    private readonly InMemoryBillingStore _store = new();
    private readonly FakeGateway _gateway = new();
    private readonly FakeClock _clock = new(TestData.Now);
    private readonly CancellationToken _ct = CancellationToken.None;

    private PaymentAppService Service => new(_store, _gateway, _clock, NullLogger<PaymentAppService>.Instance);

    private async Task<(Service Service, Part Part)> SeedCatalogAsync()
    {
        var service = TestData.NewService();
        var part = TestData.NewPart();
        await _store.CommitAsync(new BillingChanges().Save(service).Save(part), _ct);
        return (service, part);
    }

    private async Task<PaymentTransaction> CreatePaymentAsync(Guid? orderId = null)
    {
        await Service.CreateAsync(new CreatePaymentRequest(Value, orderId ?? Guid.NewGuid(), null, null), _ct);
        return (await _store.GetPaymentAsync(_gateway.Charges[^1].PaymentId, _ct))!;
    }

    private void Transaction(PaymentTransaction payment, string id, string status, decimal? amount = null, string currency = "BRL") =>
        _gateway.Transactions[id] = new GatewayPayment(id, status, payment.Id.ToString("D"), amount ?? payment.TotalAmount, currency);

    [Fact]
    public async Task Create_ShouldChargeGatewayAndStorePendingTransaction()
    {
        var orderId = Guid.NewGuid();
        var (service, part) = await SeedCatalogAsync();

        var created = await Service.CreateAsync(
            new CreatePaymentRequest(Value, orderId, [service.Id], [new PaymentPartRequest(part.Id, 2)]), _ct);

        var charge = Assert.Single(_gateway.Charges);
        Assert.Equal(orderId, charge.ServiceOrderId);
        Assert.Equal(Value, charge.TotalAmount);
        Assert.Equal(new PaymentCreatedDto($"https://mp.test/{charge.PaymentId:N}", PaymentStatus.Pending, Value), created);

        var stored = (await _store.GetPaymentAsync(charge.PaymentId, _ct))!;
        Assert.Equal(orderId, stored.ServiceOrderId);
        Assert.Equal($"pref-{charge.PaymentId:N}", stored.GatewayTransactionId);
        Assert.Equal(created.PaymentUrl, stored.PaymentUrl);
        Assert.Equal(TestData.Now, stored.CreatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_WithNonPositiveValue_ShouldThrow(int value) =>
        await Assert.ThrowsAsync<DomainException>(() =>
            Service.CreateAsync(new CreatePaymentRequest(value, Guid.NewGuid(), null, null), _ct));

    [Fact]
    public async Task Create_WithoutServiceOrder_ShouldThrow() =>
        await Assert.ThrowsAsync<DomainException>(() =>
            Service.CreateAsync(new CreatePaymentRequest(Value, Guid.Empty, null, null), _ct));

    [Fact]
    public async Task Create_WithMissingOrInactiveItems_ShouldThrowWithoutCharging()
    {
        var (service, part) = await SeedCatalogAsync();
        var inactiveService = TestData.NewService();
        inactiveService.SoftDelete();
        var inactivePart = TestData.NewPart();
        inactivePart.SoftDelete();
        await _store.CommitAsync(new BillingChanges().Save(inactiveService).Save(inactivePart), _ct);

        var invalid = new[]
        {
            new CreatePaymentRequest(Value, Guid.NewGuid(), [Guid.NewGuid()], null),
            new CreatePaymentRequest(Value, Guid.NewGuid(), [inactiveService.Id], null),
            new CreatePaymentRequest(Value, Guid.NewGuid(), [service.Id], [new PaymentPartRequest(Guid.NewGuid(), 1)]),
            new CreatePaymentRequest(Value, Guid.NewGuid(), null, [new PaymentPartRequest(inactivePart.Id, 1)]),
            new CreatePaymentRequest(Value, Guid.NewGuid(), null, [new PaymentPartRequest(part.Id, 0)])
        };

        foreach (var request in invalid)
        {
            var ex = await Assert.ThrowsAsync<DomainException>(() => Service.CreateAsync(request, _ct));
            Assert.Equal("Invalid or inactive part or service.", ex.Message);
        }

        Assert.Empty(_gateway.Charges);
    }

    [Fact]
    public async Task Create_WithGatewayFailure_ShouldPropagateWithoutStoring()
    {
        _gateway.ChargeFailure = new PaymentGatewayException("down");

        await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            Service.CreateAsync(new CreatePaymentRequest(Value, Guid.NewGuid(), null, null), _ct));

        Assert.Empty(await TestData.PendingEventsAsync(_store));
    }

    [Fact]
    public async Task Webhook_Approved_ShouldMarkPaidAndPublishPaymentConfirmed()
    {
        var payment = await CreatePaymentAsync();
        Transaction(payment, "99", "approved");

        var result = await Service.HandleGatewayNotificationAsync("99", _ct);

        Assert.Equal(NotificationResult.Processed, result);
        Assert.Equal(PaymentStatus.Paid, (await _store.GetPaymentAsync(payment.Id, _ct))!.Status);
        var confirmed = Assert.Single(await TestData.PendingEventsAsync(_store));
        Assert.Equal(EventTypes.PaymentConfirmed, confirmed.EventType);
        Assert.Equal(payment.ServiceOrderId, confirmed.Payload.GetProperty("serviceOrderId").GetGuid());
        Assert.Equal(payment.GatewayTransactionId, confirmed.Payload.GetProperty("gatewayTransactionId").GetString());
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public async Task Webhook_RefusedOrExpired_ShouldMarkRefusedAndPublishPaymentExpired(string status)
    {
        var payment = await CreatePaymentAsync();
        Transaction(payment, "1", status);

        await Service.HandleGatewayNotificationAsync("1", _ct);

        Assert.Equal(PaymentStatus.Refused, (await _store.GetPaymentAsync(payment.Id, _ct))!.Status);
        var expired = Assert.Single(await TestData.PendingEventsAsync(_store));
        Assert.Equal(EventTypes.PaymentExpired, expired.EventType);
        Assert.Equal(payment.ServiceOrderId, expired.Payload.GetProperty("serviceOrderId").GetGuid());
        Assert.Equal(payment.GatewayTransactionId, expired.Payload.GetProperty("gatewayTransactionId").GetString());
    }

    [Fact]
    public async Task Webhook_Repeated_ShouldNotPublishAgain()
    {
        var payment = await CreatePaymentAsync();
        Transaction(payment, "1", "approved");

        await Service.HandleGatewayNotificationAsync("1", _ct);
        await Service.HandleGatewayNotificationAsync("1", _ct);

        Assert.Single(await TestData.PendingEventsAsync(_store));
    }

    [Fact]
    public async Task Webhook_ApprovedAfterRefused_ShouldMarkPaid()
    {
        var payment = await CreatePaymentAsync();
        Transaction(payment, "1", "rejected");
        Transaction(payment, "2", "approved");

        await Service.HandleGatewayNotificationAsync("1", _ct);
        await Service.HandleGatewayNotificationAsync("2", _ct);

        Assert.Equal(PaymentStatus.Paid, (await _store.GetPaymentAsync(payment.Id, _ct))!.Status);
    }

    [Fact]
    public async Task Webhook_RefusedAfterPaid_ShouldNotChangePayment()
    {
        var payment = await CreatePaymentAsync();
        Transaction(payment, "1", "approved");
        Transaction(payment, "2", "rejected");

        await Service.HandleGatewayNotificationAsync("1", _ct);
        await Service.HandleGatewayNotificationAsync("2", _ct);

        Assert.Equal(PaymentStatus.Paid, (await _store.GetPaymentAsync(payment.Id, _ct))!.Status);
        Assert.Single(await TestData.PendingEventsAsync(_store));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("in_process")]
    public async Task Webhook_InProgress_ShouldKeepPending(string status)
    {
        var payment = await CreatePaymentAsync();
        Transaction(payment, "1", status);

        Assert.Equal(NotificationResult.Processed, await Service.HandleGatewayNotificationAsync("1", _ct));
        Assert.Equal(PaymentStatus.Pending, (await _store.GetPaymentAsync(payment.Id, _ct))!.Status);
        Assert.Empty(await TestData.PendingEventsAsync(_store));
    }

    [Theory]
    [InlineData(1, "BRL")]
    [InlineData(311, "USD")]
    public async Task Webhook_WithMismatchedAmountOrCurrency_ShouldNotMarkPaid(int amount, string currency)
    {
        var payment = await CreatePaymentAsync();
        Transaction(payment, "1", "approved", amount, currency);

        await Service.HandleGatewayNotificationAsync("1", _ct);

        Assert.Equal(PaymentStatus.Pending, (await _store.GetPaymentAsync(payment.Id, _ct))!.Status);
        Assert.Empty(await TestData.PendingEventsAsync(_store));
    }

    [Fact]
    public async Task Webhook_TransactionNotFound_ShouldReturnNotFound()
    {
        Assert.Equal(NotificationResult.TransactionNotFound, await Service.HandleGatewayNotificationAsync("missing", _ct));

        _gateway.Transactions["2"] = new GatewayPayment("2", "approved", "another-system", 1m, "BRL");
        Assert.Equal(NotificationResult.TransactionNotFound, await Service.HandleGatewayNotificationAsync("2", _ct));

        _gateway.Transactions["3"] = new GatewayPayment("3", "approved", Guid.NewGuid().ToString(), 1m, "BRL");
        Assert.Equal(NotificationResult.TransactionNotFound, await Service.HandleGatewayNotificationAsync("3", _ct));

        Assert.Empty(await TestData.PendingEventsAsync(_store));
    }
}

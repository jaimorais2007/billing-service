using System.Text.Json;
using BillingService.Application.Abstractions;
using BillingService.Application.Catalog;
using BillingService.Application.Common;
using BillingService.Application.Messaging;
using BillingService.Application.Payments;
using BillingService.Domain.Entities;
using BillingService.Domain.Enums;
using BillingService.Infrastructure.Persistence;
using BillingService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingService.Tests.Application;

public class SagaChoreographyTests
{
    private readonly InMemoryBillingStore _store = new();
    private readonly FakeGateway _gateway = new();
    private readonly FakeClock _clock = new(TestData.Now);
    private readonly CancellationToken _ct = CancellationToken.None;

    private PaymentAppService PaymentService => new(_store, _gateway, _clock, NullLogger<PaymentAppService>.Instance);
    private BudgetApprovedHandler BudgetHandler => new(_store, PaymentService, NullLogger<BudgetApprovedHandler>.Instance);
    private ServiceOrderRejectedHandler RejectedHandler => new(_store, NullLogger<ServiceOrderRejectedHandler>.Instance);
    private ServiceOrderApprovedHandler StockHandler => new(_store, _gateway, NullLogger<ServiceOrderApprovedHandler>.Instance);

    private static EventEnvelope MakeEnvelope<TPayload>(string eventType, Guid correlationId, TPayload payload) =>
        new(Guid.NewGuid(), eventType, TestData.Now, correlationId, JsonSerializer.SerializeToElement(payload, EventJson.Options));

    [Fact]
    public async Task BudgetApproved_WhenValid_ShouldCreateChargeAndPublishPaymentCreated()
    {
        var part = TestData.NewPart(stock: 5);
        var service = TestData.NewService();
        await _store.CommitAsync(new BillingChanges().Save(part).Save(service), _ct);

        var orderId = Guid.NewGuid();
        var envelope = MakeEnvelope(
            EventTypes.BudgetApproved,
            orderId,
            new BudgetApprovedPayload(orderId, 311m, [service.Id], [new BudgetApprovedPart(part.Id, 2)]));

        await BudgetHandler.HandleAsync(envelope, _ct);

        var payments = await _store.ListPaymentsAsync(orderId, _ct);
        var created = Assert.Single(payments);
        Assert.Equal(PaymentStatus.Pending, created.Status);
        Assert.Equal(311m, created.TotalAmount);

        var outboxEvents = await TestData.PendingEventsAsync(_store);
        var paymentCreated = Assert.Single(outboxEvents);
        Assert.Equal(EventTypes.PaymentCreated, paymentCreated.EventType);
        Assert.Equal(orderId, paymentCreated.Payload.GetProperty("serviceOrderId").GetGuid());
        Assert.Equal(created.PaymentUrl, paymentCreated.Payload.GetProperty("paymentUrl").GetString());
    }

    [Fact]
    public async Task BudgetApproved_WhenGatewayFails_ShouldPublishPaymentCreationFailed()
    {
        _gateway.ChargeFailure = new PaymentGatewayException("Mercado Pago unavailable");
        var orderId = Guid.NewGuid();

        var envelope = MakeEnvelope(
            EventTypes.BudgetApproved,
            orderId,
            new BudgetApprovedPayload(orderId, 250m, [], []));

        await BudgetHandler.HandleAsync(envelope, _ct);

        Assert.Empty(await _store.ListPaymentsAsync(orderId, _ct));
        var failed = Assert.Single(await TestData.PendingEventsAsync(_store));
        Assert.Equal(EventTypes.PaymentCreationFailed, failed.EventType);
        Assert.Equal(orderId, failed.Payload.GetProperty("serviceOrderId").GetGuid());
        Assert.Contains("Mercado Pago unavailable", failed.Payload.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task BudgetApproved_WhenInactiveCatalogItem_ShouldPublishPaymentCreationFailed()
    {
        var part = TestData.NewPart(stock: 5);
        part.SoftDelete();
        await _store.CommitAsync(new BillingChanges().Save(part), _ct);

        var orderId = Guid.NewGuid();
        var envelope = MakeEnvelope(
            EventTypes.BudgetApproved,
            orderId,
            new BudgetApprovedPayload(orderId, 200m, [], [new BudgetApprovedPart(part.Id, 1)]));

        await BudgetHandler.HandleAsync(envelope, _ct);

        Assert.Empty(await _store.ListPaymentsAsync(orderId, _ct));
        var failed = Assert.Single(await TestData.PendingEventsAsync(_store));
        Assert.Equal(EventTypes.PaymentCreationFailed, failed.EventType);
    }

    [Fact]
    public async Task ServiceOrderRejected_ShouldCancelPendingPayment()
    {
        var orderId = Guid.NewGuid();
        await BudgetHandler.HandleAsync(
            MakeEnvelope(EventTypes.BudgetApproved, orderId, new BudgetApprovedPayload(orderId, 400m, [], [])),
            _ct);

        var before = Assert.Single(await _store.ListPaymentsAsync(orderId, _ct));
        Assert.Equal(PaymentStatus.Pending, before.Status);

        await RejectedHandler.HandleAsync(
            MakeEnvelope(EventTypes.ServiceOrderRejected, orderId, new ServiceOrderRejectedPayload(orderId)),
            _ct);

        var after = Assert.Single(await _store.ListPaymentsAsync(orderId, _ct));
        Assert.Equal(PaymentStatus.Cancelled, after.Status);
    }

    [Fact]
    public async Task FullCompensation2AndReapproval_ShouldRefundOutOfStockAndAllowNewPaymentAfterRestock()
    {
        var part = TestData.NewPart(stock: 1);
        await _store.CommitAsync(new BillingChanges().Save(part), _ct);

        var orderId = Guid.NewGuid();

        // 1. BudgetApproved -> PaymentCreated
        await BudgetHandler.HandleAsync(
            MakeEnvelope(EventTypes.BudgetApproved, orderId, new BudgetApprovedPayload(orderId, 161m, [], [new(part.Id, 2)])),
            _ct);

        var firstPayment = Assert.Single(await _store.ListPaymentsAsync(orderId, _ct));

        // 2. Webhook approves payment -> PaymentConfirmed + saves GatewayPaymentId
        _gateway.Transactions["777888"] = new GatewayPayment("777888", "approved", firstPayment.Id.ToString("D"), 161m, "BRL");
        await PaymentService.HandleGatewayNotificationAsync("777888", _ct);

        var paidPayment = await _store.GetPaymentAsync(firstPayment.Id, _ct);
        Assert.Equal(PaymentStatus.Paid, paidPayment!.Status);
        Assert.Equal("777888", paidPayment.GatewayPaymentId);

        // 3. ServiceOrderApproved arrives, stock is 1 < requested 2 -> Refunds MP + PaymentStatus.Refunded + PaymentRefunded
        await StockHandler.HandleAsync(
            MakeEnvelope(EventTypes.ServiceOrderApproved, orderId, new ServiceOrderApprovedPayload(orderId, [new(part.Id, 2)])),
            _ct);

        Assert.Equal(["777888"], _gateway.Refunds);
        Assert.Equal(1, (await _store.GetPartAsync(part.Id, _ct))!.QuantityInStock);
        Assert.Equal(PaymentStatus.Refunded, (await _store.GetPaymentAsync(firstPayment.Id, _ct))!.Status);

        // 4. Customer re-approves after OS rolled back to AwaitingApproval -> New payment is created cleanly!
        await BudgetHandler.HandleAsync(
            MakeEnvelope(EventTypes.BudgetApproved, orderId, new BudgetApprovedPayload(orderId, 161m, [], [new(part.Id, 2)])),
            _ct);

        var allPayments = await _store.ListPaymentsAsync(orderId, _ct);
        Assert.Equal(2, allPayments.Count);
        Assert.Contains(allPayments, p => p.Status == PaymentStatus.Refunded);
        Assert.Contains(allPayments, p => p.Status == PaymentStatus.Pending);
    }
}

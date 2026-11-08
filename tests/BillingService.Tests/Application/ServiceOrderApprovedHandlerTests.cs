using System.Text.Json;
using BillingService.Application.Abstractions;
using BillingService.Application.Catalog;
using BillingService.Application.Messaging;
using BillingService.Domain.Entities;
using BillingService.Domain.Enums;
using BillingService.Infrastructure.Persistence;
using BillingService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingService.Tests.Application;

public class ServiceOrderApprovedHandlerTests
{
    private readonly InMemoryBillingStore _store = new();
    private readonly FakeGateway _gateway = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private ServiceOrderApprovedHandler Handler => new(_store, _gateway, NullLogger<ServiceOrderApprovedHandler>.Instance);

    private async Task<Part> SeedPartAsync(int stock)
    {
        var part = TestData.NewPart(stock);
        await _store.CommitAsync(new BillingChanges().Save(part), _ct);
        return part;
    }

    private async Task<PaymentTransaction> SeedPaidPaymentAsync(Guid serviceOrderId, string gatewayPaymentId = "99887766")
    {
        var payment = TestData.NewPayment(serviceOrderId);
        payment.Pay(gatewayPaymentId);
        await _store.CommitAsync(new BillingChanges().Save(payment), _ct);
        return payment;
    }

    private static EventEnvelope Envelope(Guid serviceOrderId, params ServiceOrderApprovedPart[] parts) => new(
        Guid.NewGuid(),
        EventTypes.ServiceOrderApproved,
        TestData.Now,
        serviceOrderId,
        JsonSerializer.SerializeToElement(new ServiceOrderApprovedPayload(serviceOrderId, parts), EventJson.Options));

    private async Task<int> StockAsync(Part part) => (await _store.GetPartAsync(part.Id, _ct))!.QuantityInStock;

    [Fact]
    public async Task Handle_WithEnoughStock_ShouldDebitAndPublishPartStockEnsuredPerPart()
    {
        var pads = await SeedPartAsync(5);
        var filter = await SeedPartAsync(3);
        var orderId = Guid.NewGuid();

        await Handler.HandleAsync(Envelope(orderId, new(pads.Id, 2), new(filter.Id, 3)), _ct);

        Assert.Equal(3, await StockAsync(pads));
        Assert.Equal(0, await StockAsync(filter));
        var events = await TestData.PendingEventsAsync(_store);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(EventTypes.PartStockEnsured, e.EventType));
        Assert.All(events, e => Assert.Equal(orderId, e.Payload.GetProperty("serviceOrderId").GetGuid()));
        var first = events.Single(e => e.Payload.GetProperty("partId").GetGuid() == pads.Id);
        Assert.Equal(2, first.Payload.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Handle_WithInsufficientStock_ShouldNotDebitAnyPartAndShouldRefundPaidPayment()
    {
        var scarce = await SeedPartAsync(1);
        var available = await SeedPartAsync(4);
        var orderId = Guid.NewGuid();
        var paid = await SeedPaidPaymentAsync(orderId, "123456789");

        await Handler.HandleAsync(Envelope(orderId, new(scarce.Id, 2), new(available.Id, 1)), _ct);

        Assert.Equal(1, await StockAsync(scarce));
        Assert.Equal(4, await StockAsync(available));
        Assert.Equal(["123456789"], _gateway.Refunds);

        var updatedPayment = await _store.GetPaymentAsync(paid.Id, _ct);
        Assert.NotNull(updatedPayment);
        Assert.Equal(PaymentStatus.Refunded, updatedPayment.Status);

        var refundedEvent = Assert.Single(await TestData.PendingEventsAsync(_store));
        Assert.Equal(EventTypes.PaymentRefunded, refundedEvent.EventType);
        Assert.Equal(orderId, refundedEvent.Payload.GetProperty("serviceOrderId").GetGuid());
        Assert.Equal(paid.GatewayTransactionId, refundedEvent.Payload.GetProperty("gatewayTransactionId").GetString());
    }

    [Fact]
    public async Task Handle_SamePartTwice_WhenExceedingStock_ShouldNotDebitPartialAndShouldRefund()
    {
        var part = await SeedPartAsync(3);
        var orderId = Guid.NewGuid();
        var paid = await SeedPaidPaymentAsync(orderId, "555000");

        await Handler.HandleAsync(Envelope(orderId, new(part.Id, 2), new(part.Id, 2)), _ct);

        Assert.Equal(3, await StockAsync(part));
        Assert.Equal(["555000"], _gateway.Refunds);
        Assert.Equal(PaymentStatus.Refunded, (await _store.GetPaymentAsync(paid.Id, _ct))!.Status);
        var evt = Assert.Single(await TestData.PendingEventsAsync(_store));
        Assert.Equal(EventTypes.PaymentRefunded, evt.EventType);
    }

    [Fact]
    public async Task Handle_SamePartTwice_WhenWithinStock_ShouldDebitBothLines()
    {
        var part = await SeedPartAsync(5);
        var orderId = Guid.NewGuid();

        await Handler.HandleAsync(Envelope(orderId, new(part.Id, 2), new(part.Id, 2)), _ct);

        Assert.Equal(1, await StockAsync(part));
        Assert.Equal(2, (await TestData.PendingEventsAsync(_store)).Count);
    }

    [Fact]
    public async Task Handle_InactivePart_ShouldStillDebitPhysicalStock()
    {
        var part = TestData.NewPart(2);
        part.SoftDelete();
        await _store.CommitAsync(new BillingChanges().Save(part), _ct);

        await Handler.HandleAsync(Envelope(Guid.NewGuid(), new ServiceOrderApprovedPart(part.Id, 1)), _ct);

        Assert.Equal(1, await StockAsync(part));
    }

    [Fact]
    public async Task Handle_MissingPartOrInvalidQuantity_WithoutPaidPayment_ShouldNotDebitAndMarkProcessed()
    {
        var part = await SeedPartAsync(5);

        await Handler.HandleAsync(Envelope(Guid.NewGuid(), new(Guid.NewGuid(), 1), new(part.Id, 0)), _ct);

        Assert.Equal(5, await StockAsync(part));
        Assert.Empty(await TestData.PendingEventsAsync(_store));
    }

    [Fact]
    public async Task Handle_Duplicate_ShouldDebitOnce()
    {
        var part = await SeedPartAsync(5);
        var envelope = Envelope(Guid.NewGuid(), new ServiceOrderApprovedPart(part.Id, 2));

        await Handler.HandleAsync(envelope, _ct);
        await Handler.HandleAsync(envelope, _ct);

        Assert.Equal(3, await StockAsync(part));
        Assert.True(await _store.IsEventProcessedAsync(envelope.EventId, _ct));
    }

    [Fact]
    public async Task Handle_RetryWithPendingPartsAfterStockAdded_ShouldEnsureThem()
    {
        var part = await SeedPartAsync(1);
        var orderId = Guid.NewGuid();
        await Handler.HandleAsync(Envelope(orderId, new ServiceOrderApprovedPart(part.Id, 3)), _ct);
        var current = (await _store.GetPartAsync(part.Id, _ct))!;
        current.AddStock(5);
        await _store.CommitAsync(new BillingChanges().Save(current), _ct);

        await Handler.HandleAsync(Envelope(orderId, new ServiceOrderApprovedPart(part.Id, 3)), _ct);

        Assert.Equal(3, await StockAsync(part));
        Assert.Single(await TestData.PendingEventsAsync(_store));
    }

    [Fact]
    public async Task Handle_WithoutServiceOrderIdInPayload_ShouldUseCorrelationId()
    {
        var part = await SeedPartAsync(5);
        var orderId = Guid.NewGuid();
        var envelope = new EventEnvelope(Guid.NewGuid(), EventTypes.ServiceOrderApproved, TestData.Now, orderId,
            JsonSerializer.SerializeToElement(new ServiceOrderApprovedPayload(Guid.Empty, [new(part.Id, 1)]), EventJson.Options));

        await Handler.HandleAsync(envelope, _ct);

        Assert.Equal(orderId, Assert.Single(await TestData.PendingEventsAsync(_store)).Payload.GetProperty("serviceOrderId").GetGuid());
    }

    [Fact]
    public async Task Handle_WithoutParts_ShouldOnlyMarkProcessed()
    {
        var envelope = Envelope(Guid.NewGuid());

        await Handler.HandleAsync(envelope, _ct);

        Assert.True(await _store.IsEventProcessedAsync(envelope.EventId, _ct));
        Assert.Empty(await TestData.PendingEventsAsync(_store));
    }

    [Fact]
    public async Task Handle_NullPayload_ShouldFailSoItGoesToDlq()
    {
        var envelope = new EventEnvelope(Guid.NewGuid(), EventTypes.ServiceOrderApproved, TestData.Now, Guid.NewGuid(),
            JsonSerializer.SerializeToElement<object?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler.HandleAsync(envelope, _ct));
    }
}

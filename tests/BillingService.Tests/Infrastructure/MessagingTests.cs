using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using BillingService.Application.Abstractions;
using BillingService.Application.Messaging;
using BillingService.Infrastructure.Messaging;
using BillingService.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BillingService.Tests.Infrastructure;

public class SnsEventPublisherTests
{
    [Fact]
    public async Task PublishAsync_ShouldUseFullArnAndAttributes()
    {
        var sns = new Mock<IAmazonSimpleNotificationService>();
        sns.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse { MessageId = "m1" });
        var publisher = new SnsEventPublisher(sns.Object, "arn:aws:sns:us-east-1:168126498555:oficina", NullLogger<SnsEventPublisher>.Instance);

        await publisher.PublishAsync("PaymentConfirmed", "{}", "00-trace-01", CancellationToken.None);

        sns.Verify(x => x.PublishAsync(It.Is<PublishRequest>(r =>
            r.TopicArn == "arn:aws:sns:us-east-1:168126498555:oficina-PaymentConfirmed" &&
            r.Message == "{}" &&
            r.MessageAttributes["eventType"].StringValue == "PaymentConfirmed" &&
            r.MessageAttributes["traceparent"].StringValue == "00-trace-01"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WithoutTrace_ShouldNotSendAttribute()
    {
        var sns = new Mock<IAmazonSimpleNotificationService>();
        sns.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse());
        var publisher = new SnsEventPublisher(sns.Object, "arn", NullLogger<SnsEventPublisher>.Instance);

        await publisher.PublishAsync("X", "{}", null, CancellationToken.None);

        sns.Verify(x => x.PublishAsync(It.Is<PublishRequest>(r => !r.MessageAttributes.ContainsKey("traceparent")), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task LoggingEventPublisher_ShouldNotFail()
    {
        var publisher = new LoggingEventPublisher(NullLogger<LoggingEventPublisher>.Instance);

        Assert.Null(await Record.ExceptionAsync(() => publisher.PublishAsync("X", "{}", null, CancellationToken.None)));
    }
}

public class OutboxPublisherTests
{
    private static async Task<InMemoryBillingStore> StoreWithEventsAsync(params string[] eventTypes)
    {
        var store = new InMemoryBillingStore();
        var changes = new BillingChanges();
        foreach (var eventType in eventTypes)
            changes.Publish(eventType, Guid.NewGuid(), new { });
        await store.CommitAsync(changes, CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task PublishPending_ShouldPublishAndRemoveFromOutbox()
    {
        var store = await StoreWithEventsAsync("A", "B");
        var publisher = new Mock<IEventPublisher>();
        var service = new OutboxPublisherBackgroundService(store, publisher.Object, NullLogger<OutboxPublisherBackgroundService>.Instance);

        Assert.True(await service.PublishPendingAsync(CancellationToken.None));

        publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Empty(await store.GetPendingAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task PublishPending_WithFailure_ShouldKeepMessageAndRecordError()
    {
        var store = await StoreWithEventsAsync("Failing", "Ok");
        var publisher = new Mock<IEventPublisher>();
        publisher.Setup(p => p.PublishAsync("Failing", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(new string('e', OutboxMessage.MaxErrorLength + 5)));
        var service = new OutboxPublisherBackgroundService(store, publisher.Object, NullLogger<OutboxPublisherBackgroundService>.Instance);

        Assert.False(await service.PublishPendingAsync(CancellationToken.None));

        var pending = Assert.Single(await store.GetPendingAsync(10, CancellationToken.None));
        Assert.Equal("Failing", pending.EventType);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(OutboxMessage.MaxErrorLength, pending.LastError!.Length);
    }

    [Fact]
    public async Task Hosted_ShouldPublishAndStopOnCancellation()
    {
        var store = await StoreWithEventsAsync("A");
        var published = new TaskCompletionSource();
        var publisher = new Mock<IEventPublisher>();
        publisher.Setup(p => p.PublishAsync("A", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => published.TrySetResult())
            .Returns(Task.CompletedTask);
        var service = new OutboxPublisherBackgroundService(store, publisher.Object, NullLogger<OutboxPublisherBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask!.IsCompleted);
    }
}

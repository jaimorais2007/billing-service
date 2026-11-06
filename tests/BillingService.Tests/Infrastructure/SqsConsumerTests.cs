using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using BillingService.Application.Messaging;
using BillingService.Infrastructure.Messaging;
using BillingService.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BillingService.Tests.Infrastructure;

public class SqsConsumerTests
{
    private const string QueueUrl = "https://sqs.test/oficina-billing-events";

    private sealed class RecordingHandler(string eventType, Exception? failure = null) : IIntegrationEventHandler
    {
        public List<EventEnvelope> Received { get; } = [];
        public string EventType => eventType;

        public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
        {
            if (failure is not null)
                throw failure;
            Received.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private static SqsConsumerBackgroundService Consumer(IAmazonSQS? sqs, string? queueUrl, params IIntegrationEventHandler[] handlers)
    {
        var services = new ServiceCollection();
        foreach (var handler in handlers)
            services.AddSingleton(handler);
        if (sqs is not null)
            services.AddSingleton(sqs);
        var provider = services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AWS:SqsQueueUrl"] = queueUrl })
            .Build();

        return new SqsConsumerBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(), provider, configuration, NullLogger<SqsConsumerBackgroundService>.Instance);
    }

    private static Message ValidMessage(string eventType, Dictionary<string, MessageAttributeValue>? attributes = null)
    {
        var envelope = new EventEnvelope(Guid.NewGuid(), eventType, TestData.Now, Guid.NewGuid(), JsonSerializer.SerializeToElement(new { }));
        return new Message
        {
            MessageId = "m1",
            ReceiptHandle = "r1",
            Body = JsonSerializer.Serialize(envelope, EventJson.Options),
            MessageAttributes = attributes ?? []
        };
    }

    private static void VerifyDeleted(Mock<IAmazonSQS> sqs, Times times) =>
        sqs.Verify(x => x.DeleteMessageAsync(It.Is<DeleteMessageRequest>(r => r.ReceiptHandle == "r1" && r.QueueUrl == QueueUrl), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task Process_WithHandler_ShouldHandleAndDelete()
    {
        var sqs = new Mock<IAmazonSQS>();
        var handler = new RecordingHandler(EventTypes.ServiceOrderApproved);
        var attributes = new Dictionary<string, MessageAttributeValue>
        {
            ["traceparent"] = new() { DataType = "String", StringValue = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01" }
        };

        await Consumer(sqs.Object, QueueUrl, handler)
            .ProcessMessageAsync(sqs.Object, ValidMessage(EventTypes.ServiceOrderApproved, attributes), QueueUrl, CancellationToken.None);

        Assert.Single(handler.Received);
        VerifyDeleted(sqs, Times.Once());
    }

    [Fact]
    public async Task Process_WithHandlerFailure_ShouldNotDeleteSoItGoesToDlq()
    {
        var sqs = new Mock<IAmazonSQS>();
        var handler = new RecordingHandler(EventTypes.ServiceOrderApproved, new InvalidOperationException("failed"));

        await Consumer(sqs.Object, QueueUrl, handler)
            .ProcessMessageAsync(sqs.Object, ValidMessage(EventTypes.ServiceOrderApproved), QueueUrl, CancellationToken.None);

        VerifyDeleted(sqs, Times.Never());
    }

    [Fact]
    public async Task Process_WithoutHandler_ShouldDiscard()
    {
        var sqs = new Mock<IAmazonSQS>();

        await Consumer(sqs.Object, QueueUrl)
            .ProcessMessageAsync(sqs.Object, ValidMessage("UnknownEvent"), QueueUrl, CancellationToken.None);

        VerifyDeleted(sqs, Times.Once());
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{}")]
    public async Task Process_WithInvalidEnvelope_ShouldDiscard(string body)
    {
        var sqs = new Mock<IAmazonSQS>();
        var message = new Message { MessageId = "m1", ReceiptHandle = "r1", Body = body };

        await Consumer(sqs.Object, QueueUrl).ProcessMessageAsync(sqs.Object, message, QueueUrl, CancellationToken.None);

        VerifyDeleted(sqs, Times.Once());
    }

    [Fact]
    public async Task Hosted_WithoutConfiguredQueue_ShouldBeDisabled()
    {
        var service = Consumer(null, null);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Hosted_ShouldReceiveProcessAndStop()
    {
        var sqs = new Mock<IAmazonSQS>();
        var handler = new RecordingHandler(EventTypes.ServiceOrderApproved);
        var deleted = new TaskCompletionSource();
        var firstCall = true;
        sqs.Setup(x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .Returns<ReceiveMessageRequest, CancellationToken>(async (_, ct) =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return new ReceiveMessageResponse { Messages = [ValidMessage(EventTypes.ServiceOrderApproved)] };
                }

                await Task.Delay(Timeout.Infinite, ct);
                return new ReceiveMessageResponse();
            });
        sqs.Setup(x => x.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => deleted.TrySetResult())
            .ReturnsAsync(new DeleteMessageResponse());
        var service = Consumer(sqs.Object, QueueUrl, handler);

        await service.StartAsync(CancellationToken.None);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.Single(handler.Received);
        Assert.True(service.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Hosted_WithReceiveFailure_ShouldKeepRunningUntilStopped()
    {
        var sqs = new Mock<IAmazonSQS>();
        var failed = new TaskCompletionSource();
        sqs.Setup(x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => failed.TrySetResult())
            .ThrowsAsync(new AmazonSQSException("unavailable"));
        var service = Consumer(sqs.Object, QueueUrl);

        await service.StartAsync(CancellationToken.None);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask!.IsCompleted);
    }
}

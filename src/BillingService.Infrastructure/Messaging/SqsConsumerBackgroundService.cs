using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using BillingService.Application.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BillingService.Infrastructure.Messaging;

public class SqsConsumerBackgroundService(
    IServiceScopeFactory scopeFactory,
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<SqsConsumerBackgroundService> logger) : BackgroundService
{
    public const string ActivitySourceName = "BillingService";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueUrl = configuration["AWS:SqsQueueUrl"];
        var sqsClient = serviceProvider.GetService<IAmazonSQS>();

        if (string.IsNullOrWhiteSpace(queueUrl) || sqsClient == null)
        {
            logger.LogInformation("SQS queue URL not configured or SQS client not available. SqsConsumer disabled.");
            return;
        }

        logger.LogInformation("SqsConsumer listening on {QueueUrl}", queueUrl);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var response = await sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MaxNumberOfMessages = 10,
                        WaitTimeSeconds = 20,
                        MessageAttributeNames = ["All"]
                    }, stoppingToken);

                    foreach (var message in response.Messages ?? [])
                    {
                        await ProcessMessageAsync(sqsClient, message, queueUrl, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error receiving messages from SQS");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("SqsConsumer stopped");
        }
    }

    public async Task ProcessMessageAsync(IAmazonSQS sqsClient, Message message, string queueUrl, CancellationToken cancellationToken)
    {
        string? parentId = null;
        if (message.MessageAttributes != null && message.MessageAttributes.TryGetValue("traceparent", out var traceAttr))
            parentId = traceAttr.StringValue;

        using var activity = ActivitySource.StartActivity("sqs.process", ActivityKind.Consumer, parentId);

        try
        {
            var envelope = TryParseEnvelope(message);

            if (envelope == null || string.IsNullOrWhiteSpace(envelope.EventType))
            {
                logger.LogWarning("Message {MessageId} skipped: invalid envelope or empty EventType", message.MessageId);
                await DeleteMessageAsync(sqsClient, message.ReceiptHandle, queueUrl, cancellationToken);
                return;
            }

            activity?.SetTag("messaging.event_type", envelope.EventType);

            using var scope = scopeFactory.CreateScope();
            var handlers = scope.ServiceProvider.GetServices<IIntegrationEventHandler>()
                .Where(h => h.EventType.Equals(envelope.EventType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (handlers.Count == 0)
                logger.LogInformation("No handler registered for event type {EventType}. Discarding message.", envelope.EventType);

            foreach (var handler in handlers)
            {
                await handler.HandleAsync(envelope, cancellationToken);
            }

            await DeleteMessageAsync(sqsClient, message.ReceiptHandle, queueUrl, cancellationToken);
            logger.LogInformation("Message {MessageId} ({EventType}) processed and deleted", message.MessageId, envelope.EventType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "Error processing message {MessageId}. It will be returned to queue / DLQ.", message.MessageId);
        }
    }

    private EventEnvelope? TryParseEnvelope(Message message)
    {
        try
        {
            return JsonSerializer.Deserialize<EventEnvelope>(message.Body, EventJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unable to deserialize message {MessageId} as EventEnvelope", message.MessageId);
            return null;
        }
    }

    private static async Task DeleteMessageAsync(IAmazonSQS sqsClient, string receiptHandle, string queueUrl, CancellationToken cancellationToken)
    {
        await sqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = receiptHandle
        }, cancellationToken);
    }
}

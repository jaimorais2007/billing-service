using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;

namespace BillingService.Infrastructure.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(string eventType, string content, string? traceParent, CancellationToken cancellationToken);
}

public class SnsEventPublisher(
    IAmazonSimpleNotificationService sns,
    string topicArnPrefix,
    ILogger<SnsEventPublisher> logger) : IEventPublisher
{
    public async Task PublishAsync(string eventType, string content, string? traceParent, CancellationToken cancellationToken)
    {
        var request = new PublishRequest
        {
            TopicArn = $"{topicArnPrefix}-{eventType}",
            Message = content
        };

        request.MessageAttributes["eventType"] = new MessageAttributeValue
        {
            DataType = "String",
            StringValue = eventType
        };

        if (!string.IsNullOrWhiteSpace(traceParent))
        {
            request.MessageAttributes["traceparent"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = traceParent
            };
        }

        var response = await sns.PublishAsync(request, cancellationToken);
        logger.LogInformation("Event {EventType} published to SNS topic {TopicArn} (MessageId: {MessageId})",
            eventType, request.TopicArn, response.MessageId);
    }
}

public class LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(string eventType, string content, string? traceParent, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Simulated SNS] {EventType}: {Content}", eventType, content);
        return Task.CompletedTask;
    }
}

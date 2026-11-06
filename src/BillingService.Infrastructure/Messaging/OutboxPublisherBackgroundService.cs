using BillingService.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BillingService.Infrastructure.Messaging;

public class OutboxPublisherBackgroundService(
    IOutboxStore outbox,
    IEventPublisher publisher,
    ILogger<OutboxPublisherBackgroundService> logger) : BackgroundService
{
    private const int BatchSize = 25;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxPublisher background service started");

        var delay = BaseDelay;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var allPublished = await PublishPendingAsync(stoppingToken);
                    delay = allPublished ? BaseDelay : Increase(delay);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Unexpected error in OutboxPublisher");
                    delay = Increase(delay);
                }

                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("OutboxPublisher background service stopped");
        }
    }

    public async Task<bool> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var allPublished = true;

        foreach (var message in await outbox.GetPendingAsync(BatchSize, cancellationToken))
        {
            try
            {
                await publisher.PublishAsync(message.EventType, message.Content, message.TraceParent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to publish outbox message {MessageId} of type {EventType}", message.Id, message.EventType);
                await outbox.MarkFailedAsync(message, ex.Message, cancellationToken);
                allPublished = false;
                continue;
            }

            await outbox.MarkPublishedAsync(message, cancellationToken);
        }

        return allPublished;
    }

    private static TimeSpan Increase(TimeSpan delay)
    {
        var next = delay * 2;
        return next > MaxDelay ? MaxDelay : next;
    }
}

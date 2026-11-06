using Amazon;
using Amazon.DynamoDBv2;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using BillingService.Application.Abstractions;
using BillingService.Infrastructure.MercadoPago;
using BillingService.Infrastructure.Messaging;
using BillingService.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BillingService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var table = configuration["AWS:DynamoDbTable"];

        if (!string.IsNullOrWhiteSpace(table))
        {
            AddAws(services, configuration, table);
        }
        else if (environment.IsDevelopment())
        {
            services.AddSingleton<InMemoryBillingStore>();
            services.AddSingleton<IBillingStore>(sp => sp.GetRequiredService<InMemoryBillingStore>());
            services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryBillingStore>());
            services.AddSingleton<IEventPublisher, LoggingEventPublisher>();
        }
        else
        {
            throw new InvalidOperationException(
                "AWS:DynamoDbTable is not configured (DYNAMODB_TABLE key in billing-service-secret).");
        }

        services.Configure<MercadoPagoOptions>(configuration.GetSection(MercadoPagoOptions.SectionName));
        services.AddHttpClient<IPaymentGateway, MercadoPagoClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<MercadoPagoOptions>>().Value.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddSingleton<IWebhookSignatureValidator, MercadoPagoSignatureValidator>();

        services.AddHostedService<OutboxPublisherBackgroundService>();
        services.AddHostedService<PaymentExpirationBackgroundService>();
        services.AddHostedService<SqsConsumerBackgroundService>();

        return services;
    }

    private static void AddAws(IServiceCollection services, IConfiguration configuration, string table)
    {
        var topicArnPrefix = configuration["AWS:SnsTopicArnPrefix"];
        if (string.IsNullOrWhiteSpace(topicArnPrefix))
            throw new InvalidOperationException("AWS:SnsTopicArnPrefix is not configured.");
        if (string.IsNullOrWhiteSpace(configuration["AWS:SqsQueueUrl"]))
            throw new InvalidOperationException(
                "AWS:SqsQueueUrl is not configured (SQS_QUEUE_URL key in billing-service-secret).");

        var region = RegionEndpoint.GetBySystemName(configuration["AWS:Region"] ?? "us-east-1");

        services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient(region));
        services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(region));
        services.AddSingleton<IAmazonSimpleNotificationService>(_ => new AmazonSimpleNotificationServiceClient(region));

        services.AddSingleton(sp => new DynamoDbBillingStore(sp.GetRequiredService<IAmazonDynamoDB>(), table));
        services.AddSingleton<IBillingStore>(sp => sp.GetRequiredService<DynamoDbBillingStore>());
        services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<DynamoDbBillingStore>());
        services.AddSingleton<IEventPublisher>(sp => new SnsEventPublisher(
            sp.GetRequiredService<IAmazonSimpleNotificationService>(),
            topicArnPrefix,
            sp.GetRequiredService<ILogger<SnsEventPublisher>>()));
    }
}

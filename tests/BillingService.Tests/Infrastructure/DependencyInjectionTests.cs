using Amazon.DynamoDBv2;
using BillingService.Application.Abstractions;
using BillingService.Infrastructure;
using BillingService.Infrastructure.Messaging;
using BillingService.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace BillingService.Tests.Infrastructure;

public class DependencyInjectionTests
{
    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static ServiceCollection Build(string environment, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration, new FakeEnvironment(environment));
        return services;
    }

    private static readonly Dictionary<string, string?> AwsSettings = new()
    {
        ["AWS:DynamoDbTable"] = "oficina-billing",
        ["AWS:SnsTopicArnPrefix"] = "arn:aws:sns:us-east-1:168126498555:oficina",
        ["AWS:SqsQueueUrl"] = "https://sqs.test/oficina-billing-events",
        ["AWS:Region"] = "us-east-1"
    };

    [Fact]
    public void Production_WithoutTable_ShouldNotStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Environments.Production, []));

        Assert.Contains("DYNAMODB_TABLE", ex.Message);
    }

    [Theory]
    [InlineData("AWS:SnsTopicArnPrefix", "SnsTopicArnPrefix")]
    [InlineData("AWS:SqsQueueUrl", "SQS_QUEUE_URL")]
    public void Production_WithoutMessaging_ShouldNotStart(string key, string expected)
    {
        var settings = new Dictionary<string, string?>(AwsSettings) { [key] = "" };

        var ex = Assert.Throws<InvalidOperationException>(() => Build(Environments.Production, settings));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Production_WithAws_ShouldUseDynamoDbAndSns()
    {
        using var provider = Build(Environments.Production, AwsSettings).BuildServiceProvider();

        Assert.IsType<DynamoDbBillingStore>(provider.GetRequiredService<IBillingStore>());
        Assert.IsType<DynamoDbBillingStore>(provider.GetRequiredService<IOutboxStore>());
        Assert.IsType<SnsEventPublisher>(provider.GetRequiredService<IEventPublisher>());
        Assert.NotNull(provider.GetRequiredService<IAmazonDynamoDB>());
        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Equal(3, hosted.Count);
        Assert.Contains(hosted, h => h is OutboxPublisherBackgroundService);
        Assert.Contains(hosted, h => h is PaymentExpirationBackgroundService);
        Assert.Contains(hosted, h => h is SqsConsumerBackgroundService);
        Assert.NotNull(provider.GetRequiredService<Amazon.SQS.IAmazonSQS>());
    }

    [Fact]
    public void Development_WithoutAws_ShouldUseInMemoryStore()
    {
        using var provider = Build(Environments.Development, []).BuildServiceProvider();

        Assert.IsType<InMemoryBillingStore>(provider.GetRequiredService<IBillingStore>());
        Assert.IsType<LoggingEventPublisher>(provider.GetRequiredService<IEventPublisher>());
        Assert.NotNull(provider.GetRequiredService<IPaymentGateway>());
    }
}

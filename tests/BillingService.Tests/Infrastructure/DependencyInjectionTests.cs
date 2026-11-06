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
        ["AWS:Region"] = "us-east-1"
    };

    [Fact]
    public void Production_WithoutTable_ShouldNotStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Environments.Production, []));

        Assert.Contains("DYNAMODB_TABLE", ex.Message);
    }

    [Fact]
    public void Production_WithoutTopicPrefix_ShouldNotStart()
    {
        var settings = new Dictionary<string, string?>(AwsSettings) { ["AWS:SnsTopicArnPrefix"] = "" };

        var ex = Assert.Throws<InvalidOperationException>(() => Build(Environments.Production, settings));

        Assert.Contains("SnsTopicArnPrefix", ex.Message);
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
        Assert.Equal(2, hosted.Count);
        Assert.Contains(hosted, h => h is OutboxPublisherBackgroundService);
        Assert.Contains(hosted, h => h is PaymentExpirationBackgroundService);
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

using BillingService.Application.Abstractions;
using BillingService.Application.Payments;
using BillingService.Domain.Enums;
using BillingService.Infrastructure.Messaging;
using BillingService.Infrastructure.Persistence;
using BillingService.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BillingService.Tests.Infrastructure;

public class PaymentExpirationBackgroundServiceTests
{
    private static ServiceProvider Provider(InMemoryBillingStore store, IPaymentGateway gateway, FakeClock clock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBillingStore>(store);
        services.AddSingleton(gateway);
        services.AddSingleton<TimeProvider>(clock);
        services.AddScoped<PaymentAppService>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Hosted_ShouldPeriodicallyCancelOverduePayments()
    {
        var store = new InMemoryBillingStore();
        var payment = TestData.NewPayment();
        await store.CommitAsync(new BillingChanges().Save(payment), CancellationToken.None);
        var clock = new FakeClock(TestData.Now.AddHours(2));
        using var provider = Provider(store, new FakeGateway(), clock);
        var service = new PaymentExpirationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PaymentExpirationBackgroundService>.Instance,
            TimeSpan.FromMilliseconds(20));

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await store.GetPaymentAsync(payment.Id, CancellationToken.None))!.Status != PaymentStatus.Cancelled && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(PaymentStatus.Cancelled, (await store.GetPaymentAsync(payment.Id, CancellationToken.None))!.Status);
        Assert.True(service.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Expire_WithGatewayFailure_ShouldLogAndNotThrow()
    {
        var store = new InMemoryBillingStore();
        await store.CommitAsync(new BillingChanges().Save(TestData.NewPayment()), CancellationToken.None);
        var gateway = new Mock<IPaymentGateway>();
        gateway.SetupGet(g => g.CheckoutLifetime).Returns(TimeSpan.FromHours(1));
        gateway.Setup(g => g.SearchPaymentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("down"));
        using var provider = Provider(store, gateway.Object, new FakeClock(TestData.Now.AddHours(2)));
        var logger = new Mock<ILogger<PaymentExpirationBackgroundService>>();
        var service = new PaymentExpirationBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(), logger.Object);

        await service.ExpireAsync(CancellationToken.None);

        logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}

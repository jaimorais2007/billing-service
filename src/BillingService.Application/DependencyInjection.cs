using BillingService.Application.Catalog;
using BillingService.Application.Messaging;
using BillingService.Application.Payments;
using Microsoft.Extensions.DependencyInjection;

namespace BillingService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ServiceCatalogAppService>();
        services.AddScoped<PartAppService>();
        services.AddScoped<PaymentAppService>();
        services.AddScoped<IIntegrationEventHandler, BudgetApprovedHandler>();
        services.AddScoped<IIntegrationEventHandler, ServiceOrderApprovedHandler>();
        services.AddScoped<IIntegrationEventHandler, ServiceOrderRejectedHandler>();
        return services;
    }
}

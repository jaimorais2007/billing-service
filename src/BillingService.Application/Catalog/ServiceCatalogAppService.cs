using BillingService.Application.Abstractions;
using BillingService.Application.Common;
using BillingService.Application.Messaging;
using BillingService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace BillingService.Application.Catalog;

public sealed record SaveServiceRequest(string Name, string? Description, decimal DefaultPrice);

public sealed record ServiceDto(Guid Id, string Name, string Description, decimal DefaultPrice)
{
    public static ServiceDto From(Service s) => new(s.Id, s.Name, s.Description, s.DefaultPrice);
}

public class ServiceCatalogAppService(IBillingStore store, TimeProvider clock, ILogger<ServiceCatalogAppService> logger)
{
    public async Task<IReadOnlyList<ServiceDto>> GetAllAsync(CancellationToken cancellationToken) =>
        (await store.ListServicesAsync(cancellationToken))
        .Where(s => !s.Inactive)
        .OrderBy(s => s.Name)
        .Select(ServiceDto.From)
        .ToList();

    public async Task<ServiceDto> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        ServiceDto.From(await LoadAsync(id, cancellationToken));

    public async Task<ServiceDto> CreateAsync(SaveServiceRequest request, CancellationToken cancellationToken)
    {
        var service = Service.Create(request.Name, request.Description, request.DefaultPrice, clock.GetUtcNow().UtcDateTime);

        await store.CommitAsync(new BillingChanges().Save(service), cancellationToken);

        logger.LogInformation("Service {ServiceId} created", service.Id);
        return ServiceDto.From(service);
    }

    public async Task<ServiceDto> UpdateAsync(Guid id, SaveServiceRequest request, CancellationToken cancellationToken)
    {
        var service = await LoadAsync(id, cancellationToken);
        service.Update(request.Name, request.Description, request.DefaultPrice);

        await store.CommitAsync(new BillingChanges().Save(service), cancellationToken);
        return ServiceDto.From(service);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var service = await LoadAsync(id, cancellationToken);
        service.SoftDelete();

        await store.CommitAsync(
            new BillingChanges().Save(service).Publish(EventTypes.ServiceDeleted, service.Id, new ServiceDeletedPayload(service.Id)),
            cancellationToken);

        logger.LogInformation("Service {ServiceId} deleted", id);
    }

    private async Task<Service> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var service = await store.GetServiceAsync(id, cancellationToken);
        return service is { Inactive: false } ? service : throw new NotFoundException("Service not found.");
    }
}

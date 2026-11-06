using BillingService.Application.Abstractions;
using BillingService.Application.Common;
using BillingService.Application.Messaging;
using BillingService.Domain.Entities;
using BillingService.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace BillingService.Application.Catalog;

public sealed record CreatePartRequest(string Name, string Code, int InitialQuantity, decimal Price);

public sealed record UpdatePartRequest(string Name, string Code, decimal Price);

public sealed record StockQuantityRequest(int Quantity);

public sealed record PartDto(Guid Id, string Name, string Code, int QuantityInStock, decimal Price)
{
    public static PartDto From(Part p) => new(p.Id, p.Name, p.Code, p.QuantityInStock, p.Price);
}

public class PartAppService(IBillingStore store, TimeProvider clock, ILogger<PartAppService> logger)
{
    public async Task<IReadOnlyList<PartDto>> GetAllAsync(CancellationToken cancellationToken) =>
        (await store.ListPartsAsync(cancellationToken))
        .Where(p => !p.Inactive)
        .OrderBy(p => p.Name)
        .Select(PartDto.From)
        .ToList();

    public async Task<PartDto> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        PartDto.From(await LoadAsync(id, cancellationToken));

    public async Task<PartDto> CreateAsync(CreatePartRequest request, CancellationToken cancellationToken)
    {
        var part = Part.Create(request.Name, request.Code, request.InitialQuantity, request.Price, clock.GetUtcNow().UtcDateTime);
        await EnsureCodeIsUniqueAsync(part.Code, null, cancellationToken);

        await store.CommitAsync(new BillingChanges().Save(part), cancellationToken);

        logger.LogInformation("Part {PartId} ({PartCode}) created with {Quantity} unit(s)", part.Id, part.Code, part.QuantityInStock);
        return PartDto.From(part);
    }

    public async Task<PartDto> UpdateAsync(Guid id, UpdatePartRequest request, CancellationToken cancellationToken)
    {
        var part = await LoadAsync(id, cancellationToken);
        part.Update(request.Name, request.Code, request.Price);
        await EnsureCodeIsUniqueAsync(part.Code, id, cancellationToken);

        await store.CommitAsync(new BillingChanges().Save(part), cancellationToken);
        return PartDto.From(part);
    }

    public async Task AddStockAsync(Guid id, int quantity, CancellationToken cancellationToken)
    {
        var part = await LoadAsync(id, cancellationToken);
        part.AddStock(quantity);

        await store.CommitAsync(
            new BillingChanges()
                .Save(part)
                .Publish(EventTypes.PartStockAdded, part.Id, new PartStockAddedPayload(part.Id, part.QuantityInStock)),
            cancellationToken);
    }

    public async Task RemoveStockAsync(Guid id, int quantity, CancellationToken cancellationToken)
    {
        var part = await LoadAsync(id, cancellationToken);
        part.RemoveStock(quantity);

        await store.CommitAsync(new BillingChanges().Save(part), cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var part = await LoadAsync(id, cancellationToken);
        part.SoftDelete();

        await store.CommitAsync(
            new BillingChanges().Save(part).Publish(EventTypes.PartDeleted, part.Id, new PartDeletedPayload(part.Id)),
            cancellationToken);

        logger.LogInformation("Part {PartId} deleted", id);
    }

    private async Task EnsureCodeIsUniqueAsync(string code, Guid? ignoreId, CancellationToken cancellationToken)
    {
        var parts = await store.ListPartsAsync(cancellationToken);
        if (parts.Any(p => !p.Inactive && p.Id != ignoreId && string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException($"A part with code {code} already exists.");
    }

    private async Task<Part> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var part = await store.GetPartAsync(id, cancellationToken);
        return part is { Inactive: false } ? part : throw new NotFoundException("Part not found.");
    }
}

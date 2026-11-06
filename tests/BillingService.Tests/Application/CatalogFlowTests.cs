using BillingService.Application.Abstractions;
using BillingService.Application.Catalog;
using BillingService.Application.Common;
using BillingService.Application.Messaging;
using BillingService.Domain.Exceptions;
using BillingService.Infrastructure.Persistence;
using BillingService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingService.Tests.Application;

public class CatalogFlowTests
{
    private readonly InMemoryBillingStore _store = new();
    private readonly FakeClock _clock = new(TestData.Now);
    private readonly CancellationToken _ct = CancellationToken.None;

    private ServiceCatalogAppService Services => new(_store, _clock, NullLogger<ServiceCatalogAppService>.Instance);
    private PartAppService Parts => new(_store, _clock, NullLogger<PartAppService>.Instance);

    [Fact]
    public async Task CreateService_ShouldReturnDeclaredFields()
    {
        var created = await Services.CreateAsync(new SaveServiceRequest("Oil change", "Synthetic", 120m), _ct);

        Assert.Equal(new ServiceDto(created.Id, "Oil change", "Synthetic", 120m), created);
        Assert.Equal(TestData.Now, (await _store.GetServiceAsync(created.Id, _ct))!.CreatedAt);
    }

    [Fact]
    public async Task CreateService_WithInvalidData_ShouldThrow() =>
        await Assert.ThrowsAsync<DomainException>(() => Services.CreateAsync(new SaveServiceRequest(" ", null, 10m), _ct));

    [Fact]
    public async Task EditGetAndListServices()
    {
        var created = await Services.CreateAsync(new SaveServiceRequest("Oil change", null, 120m), _ct);

        var updated = await Services.UpdateAsync(created.Id, new SaveServiceRequest("Oil change", "Mineral", 100m), _ct);

        Assert.Equal(100m, updated.DefaultPrice);
        Assert.Equal(updated, await Services.GetByIdAsync(created.Id, _ct));
        Assert.Equal(updated, Assert.Single(await Services.GetAllAsync(_ct)));
        await Assert.ThrowsAsync<DomainException>(() => Services.UpdateAsync(created.Id, new SaveServiceRequest("x", null, -1m), _ct));
    }

    [Fact]
    public async Task DeleteService_ShouldSoftDeleteAndPublishServiceDeleted()
    {
        var created = await Services.CreateAsync(new SaveServiceRequest("Oil change", null, 120m), _ct);

        await Services.DeleteAsync(created.Id, _ct);

        Assert.True((await _store.GetServiceAsync(created.Id, _ct))!.Inactive);
        await Assert.ThrowsAsync<NotFoundException>(() => Services.GetByIdAsync(created.Id, _ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Services.DeleteAsync(created.Id, _ct));
        Assert.Empty(await Services.GetAllAsync(_ct));

        var deleted = Assert.Single(await TestData.PendingEventsAsync(_store));
        Assert.Equal(EventTypes.ServiceDeleted, deleted.EventType);
        Assert.Equal(created.Id, deleted.Payload.GetProperty("serviceId").GetGuid());
    }

    [Fact]
    public async Task Services_Missing_ShouldThrowNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Services.GetByIdAsync(Guid.NewGuid(), _ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Services.UpdateAsync(Guid.NewGuid(), new SaveServiceRequest("x", null, 1m), _ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Services.DeleteAsync(Guid.NewGuid(), _ct));
    }

    [Fact]
    public async Task CreatePart_ShouldReturnDeclaredFields()
    {
        var created = await Parts.CreateAsync(new CreatePartRequest("Oil filter", "OF-1", 4, 30m), _ct);

        Assert.Equal(new PartDto(created.Id, "Oil filter", "OF-1", 4, 30m), created);
    }

    [Fact]
    public async Task CreatePart_WithInvalidData_ShouldThrow() =>
        await Assert.ThrowsAsync<DomainException>(() => Parts.CreateAsync(new CreatePartRequest("Filter", "F", -1, 1m), _ct));

    [Fact]
    public async Task EditGetAndListParts()
    {
        var created = await Parts.CreateAsync(new CreatePartRequest("Oil filter", "OF-1", 4, 30m), _ct);

        var updated = await Parts.UpdateAsync(created.Id, new UpdatePartRequest("Oil filter XL", "OF-1", 35m), _ct);

        Assert.Equal(new PartDto(created.Id, "Oil filter XL", "OF-1", 4, 35m), updated);
        Assert.Equal(updated, await Parts.GetByIdAsync(created.Id, _ct));
        Assert.Equal(updated, Assert.Single(await Parts.GetAllAsync(_ct)));
    }

    [Fact]
    public async Task PartCode_ShouldBeUniqueAmongActiveParts()
    {
        var first = await Parts.CreateAsync(new CreatePartRequest("Filter", "DUP-1", 1, 10m), _ct);
        var second = await Parts.CreateAsync(new CreatePartRequest("Disc", "D-1", 1, 10m), _ct);

        await Assert.ThrowsAsync<DomainException>(() => Parts.CreateAsync(new CreatePartRequest("Other", "dup-1", 1, 10m), _ct));
        await Assert.ThrowsAsync<DomainException>(() => Parts.UpdateAsync(second.Id, new UpdatePartRequest("Disc", "DUP-1", 10m), _ct));

        await Parts.DeleteAsync(first.Id, _ct);
        Assert.Equal("DUP-1", (await Parts.CreateAsync(new CreatePartRequest("Filter", "DUP-1", 1, 10m), _ct)).Code);
    }

    [Fact]
    public async Task AddStock_ShouldIncrementAndPublishPartStockAdded()
    {
        var part = await Parts.CreateAsync(new CreatePartRequest("Oil filter", "OF-1", 4, 30m), _ct);

        await Parts.AddStockAsync(part.Id, 3, _ct);

        Assert.Equal(7, (await Parts.GetByIdAsync(part.Id, _ct)).QuantityInStock);
        var added = Assert.Single(await TestData.PendingEventsAsync(_store));
        Assert.Equal(EventTypes.PartStockAdded, added.EventType);
        Assert.Equal(part.Id, added.Payload.GetProperty("partId").GetGuid());
        Assert.Equal(7, added.Payload.GetProperty("availableQuantity").GetInt32());
    }

    [Fact]
    public async Task AddStock_WithInvalidQuantity_ShouldThrowWithoutEvent()
    {
        var part = await Parts.CreateAsync(new CreatePartRequest("Oil filter", "OF-1", 4, 30m), _ct);

        await Assert.ThrowsAsync<DomainException>(() => Parts.AddStockAsync(part.Id, 0, _ct));

        Assert.Empty(await TestData.PendingEventsAsync(_store));
    }

    [Fact]
    public async Task RemoveStock_ShouldDecrementWithoutEvent()
    {
        var part = await Parts.CreateAsync(new CreatePartRequest("Oil filter", "OF-1", 4, 30m), _ct);

        await Parts.RemoveStockAsync(part.Id, 3, _ct);

        Assert.Equal(1, (await Parts.GetByIdAsync(part.Id, _ct)).QuantityInStock);
        await Assert.ThrowsAsync<DomainException>(() => Parts.RemoveStockAsync(part.Id, 2, _ct));
        Assert.Empty(await TestData.PendingEventsAsync(_store));
    }

    [Fact]
    public async Task DeletePart_ShouldSoftDeleteAndPublishPartDeleted()
    {
        var part = await Parts.CreateAsync(new CreatePartRequest("Oil filter", "OF-1", 4, 30m), _ct);

        await Parts.DeleteAsync(part.Id, _ct);

        Assert.True((await _store.GetPartAsync(part.Id, _ct))!.Inactive);
        Assert.Empty(await Parts.GetAllAsync(_ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Parts.AddStockAsync(part.Id, 1, _ct));

        var deleted = Assert.Single(await TestData.PendingEventsAsync(_store));
        Assert.Equal(EventTypes.PartDeleted, deleted.EventType);
        Assert.Equal(part.Id, deleted.Payload.GetProperty("partId").GetGuid());
    }

    [Fact]
    public async Task Parts_Missing_ShouldThrowNotFound()
    {
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<NotFoundException>(() => Parts.GetByIdAsync(id, _ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Parts.UpdateAsync(id, new UpdatePartRequest("x", "y", 1m), _ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Parts.AddStockAsync(id, 1, _ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Parts.RemoveStockAsync(id, 1, _ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Parts.DeleteAsync(id, _ct));
    }

    [Fact]
    public async Task StaleUpdate_ShouldThrowConflict()
    {
        var part = TestData.NewPart();
        await _store.CommitAsync(new BillingChanges().Save(part), _ct);
        var stale = (await _store.GetPartAsync(part.Id, _ct))!;

        await Parts.AddStockAsync(part.Id, 1, _ct);
        stale.AddStock(1);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => _store.CommitAsync(new BillingChanges().Save(stale), _ct));
    }
}

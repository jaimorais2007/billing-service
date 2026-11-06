using BillingService.Application.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace BillingService.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PartsController(PartAppService parts) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PartDto>>> GetAll(CancellationToken ct) =>
        Ok(await parts.GetAllAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PartDto>> GetById(Guid id, CancellationToken ct) =>
        Ok(await parts.GetByIdAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<PartDto>> Create([FromBody] CreatePartRequest request, CancellationToken ct)
    {
        var created = await parts.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PartDto>> Update(Guid id, [FromBody] UpdatePartRequest request, CancellationToken ct) =>
        Ok(await parts.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/add-stock")]
    public async Task<IActionResult> AddStock(Guid id, [FromBody] StockQuantityRequest request, CancellationToken ct)
    {
        await parts.AddStockAsync(id, request.Quantity, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/remove-stock")]
    public async Task<IActionResult> RemoveStock(Guid id, [FromBody] StockQuantityRequest request, CancellationToken ct)
    {
        await parts.RemoveStockAsync(id, request.Quantity, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await parts.DeleteAsync(id, ct);
        return NoContent();
    }
}

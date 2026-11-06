using BillingService.Application.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace BillingService.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ServiceController(ServiceCatalogAppService services) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ServiceDto>>> GetAll(CancellationToken ct) =>
        Ok(await services.GetAllAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ServiceDto>> GetById(Guid id, CancellationToken ct) =>
        Ok(await services.GetByIdAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<ServiceDto>> Create([FromBody] SaveServiceRequest request, CancellationToken ct)
    {
        var created = await services.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ServiceDto>> Update(Guid id, [FromBody] SaveServiceRequest request, CancellationToken ct) =>
        Ok(await services.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await services.DeleteAsync(id, ct);
        return Ok();
    }
}

using BillingService.Application.Payments;
using Microsoft.AspNetCore.Mvc;

namespace BillingService.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController(PaymentAppService payments) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<PaymentCreatedDto>> Create([FromBody] CreatePaymentRequest request, CancellationToken ct)
    {
        var outcome = await payments.CreateAsync(request, ct);
        return outcome.Created ? StatusCode(StatusCodes.Status201Created, outcome.Payment) : Ok(outcome.Payment);
    }
}

using System.Globalization;
using System.Text.Json;
using BillingService.Application.Abstractions;
using BillingService.Application.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BillingService.Api.Controllers;

public sealed record MercadoPagoNotificationData(JsonElement? Id);

public sealed record MercadoPagoNotification(string? Type, MercadoPagoNotificationData? Data);

[ApiController]
// O Mercado Pago não envia JWT: a autenticidade é garantida pela assinatura x-signature.
[AllowAnonymous]
[Route("api/webhooks/mercadopago")]
public class MercadoPagoWebhookController(
    PaymentAppService payments,
    IWebhookSignatureValidator signatureValidator,
    ILogger<MercadoPagoWebhookController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] MercadoPagoNotification? notification,
        CancellationToken ct)
    {
        string? queryDataId = Request.Query["data.id"];

        if (!signatureValidator.IsValid(Request.Headers["x-signature"], Request.Headers["x-request-id"], queryDataId))
        {
            logger.LogWarning("Mercado Pago notification with invalid signature");
            return Unauthorized();
        }

        string? type = Request.Query["type"];
        type ??= notification?.Type;
        var dataId = queryDataId ?? notification?.Data?.Id?.ToString();

        if (type != "payment" || !long.TryParse(dataId, NumberStyles.None, CultureInfo.InvariantCulture, out var transactionId))
        {
            logger.LogInformation("Mercado Pago notification ignored");
            return Ok();
        }

        var result = await payments.HandleGatewayNotificationAsync(transactionId.ToString(CultureInfo.InvariantCulture), ct);
        return result == NotificationResult.TransactionNotFound ? NotFound() : Ok();
    }
}

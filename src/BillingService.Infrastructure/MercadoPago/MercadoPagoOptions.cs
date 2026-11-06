namespace BillingService.Infrastructure.MercadoPago;

public class MercadoPagoOptions
{
    public const string SectionName = "MercadoPago";

    public string BaseUrl { get; set; } = "https://api.mercadopago.com";

    public string AccessToken { get; set; } = string.Empty;

    public string WebhookSecret { get; set; } = string.Empty;

    public string NotificationUrl { get; set; } = string.Empty;

    public bool UseSandbox { get; set; }

    public int CheckoutExpirationMinutes { get; set; } = 60;
}

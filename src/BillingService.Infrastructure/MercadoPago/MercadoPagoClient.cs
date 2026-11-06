using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BillingService.Application.Abstractions;
using BillingService.Application.Common;
using BillingService.Domain.Entities;
using Microsoft.Extensions.Options;

namespace BillingService.Infrastructure.MercadoPago;

public class MercadoPagoClient(HttpClient http, IOptions<MercadoPagoOptions> options) : IPaymentGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MercadoPagoOptions _options = options.Value;

    public TimeSpan CheckoutLifetime => TimeSpan.FromMinutes(_options.CheckoutExpirationMinutes);

    public async Task<ChargeResult> CreateChargeAsync(ChargeRequest request, CancellationToken cancellationToken)
    {
        var reference = request.PaymentId.ToString("D");
        var body = new PreferenceRequest(
            [new PreferenceItem(reference, $"Service order {request.ServiceOrderId:D}", 1, request.TotalAmount, PaymentTransaction.Currency)],
            reference,
            string.IsNullOrWhiteSpace(_options.NotificationUrl) ? null : _options.NotificationUrl,
            Expires: true,
            ExpirationDateTo: DateTime.SpecifyKind(request.ExpiresAt, DateTimeKind.Utc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

        using var message = CreateRequest(HttpMethod.Post, "/checkout/preferences", reference);
        message.Content = JsonContent.Create(body, options: Json);

        var preference = await SendAsync<PreferenceResponse>(message, cancellationToken);
        var url = _options.UseSandbox ? preference.SandboxInitPoint : preference.InitPoint;

        if (string.IsNullOrWhiteSpace(preference.Id) || string.IsNullOrWhiteSpace(url))
            throw new PaymentGatewayException("Mercado Pago did not return a payment link.");

        return new ChargeResult(preference.Id, url);
    }

    public async Task<GatewayPayment?> GetPaymentAsync(string transactionId, CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Get, $"/v1/payments/{Uri.EscapeDataString(transactionId)}");
        using var response = await SendRawAsync(message, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        var payment = await response.Content.ReadFromJsonAsync<PaymentResponse>(Json, cancellationToken);
        return payment is null ? null : ToGatewayPayment(payment);
    }

    public async Task<IReadOnlyList<GatewayPayment>> SearchPaymentsAsync(string externalReference, CancellationToken cancellationToken)
    {
        var path = "/v1/payments/search?sort=date_created&criteria=desc&external_reference=" + Uri.EscapeDataString(externalReference);
        using var message = CreateRequest(HttpMethod.Get, path);

        var search = await SendAsync<SearchResponse>(message, cancellationToken);
        return (search.Results ?? []).Select(ToGatewayPayment).ToList();
    }

    private static GatewayPayment ToGatewayPayment(PaymentResponse payment) => new(
        payment.Id.ToString(CultureInfo.InvariantCulture),
        payment.Status ?? string.Empty,
        payment.ExternalReference,
        payment.TransactionAmount,
        payment.CurrencyId);

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
            throw new PaymentGatewayException("Mercado Pago access token is not configured (MERCADOPAGO_ACCESS_TOKEN).");

        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        if (idempotencyKey is not null)
            message.Headers.Add("X-Idempotency-Key", idempotencyKey);

        return message;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
               ?? throw new PaymentGatewayException("Empty response from Mercado Pago.");
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentGatewayException("Failed to communicate with Mercado Pago.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PaymentGatewayException("Mercado Pago request timed out.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new PaymentGatewayException($"Mercado Pago responded {(int)response.StatusCode}: {detail}");
    }

    private sealed record PreferenceItem(string Id, string Title, int Quantity, decimal UnitPrice, string CurrencyId);

    private sealed record PreferenceRequest(
        IReadOnlyList<PreferenceItem> Items,
        string ExternalReference,
        string? NotificationUrl,
        bool Expires,
        string ExpirationDateTo);

    private sealed record SearchResponse(IReadOnlyList<PaymentResponse>? Results);

    private sealed record PreferenceResponse(string? Id, string? InitPoint, string? SandboxInitPoint);

    private sealed record PaymentResponse(
        long Id,
        string? Status,
        string? ExternalReference,
        decimal TransactionAmount,
        string? CurrencyId);
}

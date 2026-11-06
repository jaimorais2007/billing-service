using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BillingService.Application.Abstractions;
using BillingService.Application.Common;
using BillingService.Infrastructure.MercadoPago;
using Microsoft.Extensions.Options;

namespace BillingService.Tests.Infrastructure;

public class MercadoPagoSignatureValidatorTests
{
    private const string Secret = "test-secret";

    private static MercadoPagoSignatureValidator Validator(string secret = Secret) =>
        new(Options.Create(new MercadoPagoOptions { WebhookSecret = secret }));

    private static string Sign(string manifest, string secret = Secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();

    [Fact]
    public void IsValid_WithCorrectSignature_ShouldAccept()
    {
        var header = $"ts=1704908010,v1={Sign("id:123;request-id:req-1;ts:1704908010;")}";

        Assert.True(Validator().IsValid(header, "req-1", "123"));
    }

    [Fact]
    public void IsValid_WithSpacesInHeader_ShouldAccept()
    {
        var header = $"ts=1704908010, v1={Sign("id:123;request-id:req-1;ts:1704908010;")}";

        Assert.True(Validator().IsValid(header, "req-1", "123"));
    }

    [Fact]
    public void IsValid_WithoutIdOrRequestId_ShouldOmitThemFromManifest()
    {
        Assert.True(Validator().IsValid($"ts=10,v1={Sign("ts:10;")}", null, null));
        Assert.True(Validator().IsValid($"ts=10,v1={Sign("request-id:r;ts:10;")}", "r", null));
    }

    [Theory]
    [InlineData("999", "req-1", "1704908010")]
    [InlineData("123", "req-2", "1704908010")]
    [InlineData("123", "req-1", "1704908011")]
    public void IsValid_WithTamperedData_ShouldReject(string dataId, string requestId, string ts)
    {
        var header = $"ts={ts},v1={Sign("id:123;request-id:req-1;ts:1704908010;")}";

        Assert.False(Validator().IsValid(header, requestId, dataId));
    }

    [Fact]
    public void IsValid_WithAnotherSecret_ShouldReject()
    {
        var header = $"ts=10,v1={Sign("ts:10;", "other")}";

        Assert.False(Validator().IsValid(header, null, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("ts=10")]
    [InlineData("v1=abc")]
    public void IsValid_WithInvalidHeader_ShouldReject(string? header) =>
        Assert.False(Validator().IsValid(header, "r", "1"));

    [Fact]
    public void IsValid_WithoutConfiguredSecret_ShouldReject()
    {
        var header = $"ts=10,v1={Sign("ts:10;", "")}";

        Assert.False(Validator("").IsValid(header, null, null));
    }
}

public class MercadoPagoClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static MercadoPagoClient Client(HttpMessageHandler handler, Action<MercadoPagoOptions>? configure = null)
    {
        var options = new MercadoPagoOptions
        {
            AccessToken = "TEST-token",
            NotificationUrl = "https://api.test/api/webhooks/mercadopago"
        };
        configure?.Invoke(options);
        return new MercadoPagoClient(new HttpClient(handler) { BaseAddress = new Uri("https://mp.test") }, Options.Create(options));
    }

    private static ChargeRequest Charge() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 311.50m, new DateTime(2026, 11, 3, 13, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task CreateCharge_ShouldSendCheckoutExpirationInUtc()
    {
        var handler = new StubHandler(_ => Json("""{"id":"pref-1","init_point":"https://prod"}"""));

        await Client(handler).CreateChargeAsync(Charge(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.True(body.RootElement.GetProperty("expires").GetBoolean());
        Assert.Equal("2026-11-03T13:00:00.000Z", body.RootElement.GetProperty("expiration_date_to").GetString());
        Assert.DoesNotContain(@"+", handler.Body);
    }

    [Fact]
    public void CheckoutLifetime_ShouldComeFromConfiguration()
    {
        var handler = new StubHandler(_ => Json("{}"));

        Assert.Equal(TimeSpan.FromMinutes(30), Client(handler, o => o.CheckoutExpirationMinutes = 30).CheckoutLifetime);
    }

    [Fact]
    public async Task SearchPayments_ShouldFilterByExternalReference()
    {
        var handler = new StubHandler(_ => Json(
            """{"paging":{"total":1},"results":[{"id":5,"status":"approved","external_reference":"ref-1","transaction_amount":10,"currency_id":"BRL"}]}"""));

        var results = await Client(handler).SearchPaymentsAsync("ref-1", CancellationToken.None);

        Assert.Equal(new GatewayPayment("5", "approved", "ref-1", 10m, "BRL"), Assert.Single(results));
        Assert.Equal("/v1/payments/search", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Contains("external_reference=ref-1", handler.Request.RequestUri.Query);
    }

    [Fact]
    public async Task SearchPayments_WithoutResults_ShouldReturnEmpty()
    {
        var handler = new StubHandler(_ => Json("""{"paging":{"total":0}}"""));

        Assert.Empty(await Client(handler).SearchPaymentsAsync("ref-1", CancellationToken.None));
    }

    [Fact]
    public async Task CreateCharge_ShouldSendPreferenceWithTokenAndIdempotencyKey()
    {
        var handler = new StubHandler(_ => Json("""{"id":"pref-1","init_point":"https://prod","sandbox_init_point":"https://sandbox"}"""));
        var request = Charge();

        var result = await Client(handler).CreateChargeAsync(request, CancellationToken.None);

        Assert.Equal(new ChargeResult("pref-1", "https://prod"), result);
        Assert.Equal("/checkout/preferences", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer TEST-token", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal(request.PaymentId.ToString("D"), handler.Request.Headers.GetValues("X-Idempotency-Key").Single());

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(request.PaymentId.ToString("D"), body.RootElement.GetProperty("external_reference").GetString());
        Assert.Equal("https://api.test/api/webhooks/mercadopago", body.RootElement.GetProperty("notification_url").GetString());

        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal($"Service order {request.ServiceOrderId:D}", item.GetProperty("title").GetString());
        Assert.Equal(1, item.GetProperty("quantity").GetInt32());
        Assert.Equal(311.50m, item.GetProperty("unit_price").GetDecimal());
        Assert.Equal("BRL", item.GetProperty("currency_id").GetString());
    }

    [Fact]
    public async Task CreateCharge_WithSandboxEnabled_ShouldUseSandboxInitPoint()
    {
        var handler = new StubHandler(_ => Json("""{"id":"pref-1","init_point":"https://prod","sandbox_init_point":"https://sandbox"}"""));

        var result = await Client(handler, o => { o.UseSandbox = true; o.NotificationUrl = ""; }).CreateChargeAsync(Charge(), CancellationToken.None);

        Assert.Equal("https://sandbox", result.PaymentUrl);
        Assert.DoesNotContain("notification_url", handler.Body);
    }

    [Fact]
    public async Task CreateCharge_WithoutLink_ShouldThrow()
    {
        var handler = new StubHandler(_ => Json("""{"id":"pref-1"}"""));

        await Assert.ThrowsAsync<PaymentGatewayException>(() => Client(handler).CreateChargeAsync(Charge(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateCharge_WithGatewayError_ShouldThrow()
    {
        var handler = new StubHandler(_ => Json("""{"message":"error"}""", HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => Client(handler).CreateChargeAsync(Charge(), CancellationToken.None));
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public async Task WithoutToken_ShouldNotCallGateway()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("should not be called"));

        await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            Client(handler, o => o.AccessToken = "").GetPaymentAsync("1", CancellationToken.None));
    }

    [Fact]
    public async Task GetPayment_ShouldMapResponse_IncludingAmountAsString()
    {
        var handler = new StubHandler(_ => Json(
            """{"id":123,"status":"approved","status_detail":"accredited","external_reference":"ref","transaction_amount":"24.50","currency_id":"BRL"}"""));

        var payment = await Client(handler).GetPaymentAsync("123", CancellationToken.None);

        Assert.Equal("/v1/payments/123", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal(new GatewayPayment("123", "approved", "ref", 24.50m, "BRL"), payment);
    }

    [Fact]
    public async Task GetPayment_NotFound_ShouldReturnNull()
    {
        var handler = new StubHandler(_ => Json("{}", HttpStatusCode.NotFound));

        Assert.Null(await Client(handler).GetPaymentAsync("1", CancellationToken.None));
    }

    [Fact]
    public async Task GetPayment_WithGatewayError_ShouldThrow()
    {
        var handler = new StubHandler(_ => Json("""{"message":"error"}""", HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => Client(handler).GetPaymentAsync("1", CancellationToken.None));
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task NetworkFailure_ShouldBecomePaymentGatewayException()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no network"));

        await Assert.ThrowsAsync<PaymentGatewayException>(() => Client(handler).GetPaymentAsync("1", CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_ShouldBecomePaymentGatewayException()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<PaymentGatewayException>(() => Client(handler).GetPaymentAsync("1", CancellationToken.None));
    }
}

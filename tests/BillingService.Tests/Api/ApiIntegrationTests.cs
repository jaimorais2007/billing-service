using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BillingService.Api.Filters;
using BillingService.Application.Abstractions;
using BillingService.Application.Catalog;
using BillingService.Application.Common;
using BillingService.Application.Payments;
using BillingService.Domain.Enums;
using BillingService.Domain.Exceptions;
using BillingService.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BillingService.Tests.Api;

public class BillingApiFactory : WebApplicationFactory<Program>
{
    public const string WebhookSecret = "api-secret";

    public FakeGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("MercadoPago:WebhookSecret", WebhookSecret);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPaymentGateway>(Gateway);
        });
    }
}

public class ApiIntegrationTests(BillingApiFactory factory) : IClassFixture<BillingApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _client = factory.CreateClient();

    private static string Sign(string dataId, string ts = "10")
    {
        var manifest = $"id:{dataId};request-id:req-1;ts:{ts};";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(BillingApiFactory.WebhookSecret), Encoding.UTF8.GetBytes(manifest));
        return $"ts={ts},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(string query, string? signature, string body = "{}")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/mercadopago" + query);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("x-request-id", "req-1");
        if (signature is not null)
            request.Headers.Add("x-signature", signature);
        return await _client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    private async Task<ServiceDto> CreateServiceAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/Service", new SaveServiceRequest("Alignment", "Front", 90m));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<ServiceDto>(response);
    }

    private async Task<PartDto> CreatePartAsync(int stock = 10)
    {
        var response = await _client.PostAsJsonAsync("/api/Parts", new CreatePartRequest("Brake pad", $"BP-{Guid.NewGuid():N}", stock, 80m));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<PartDto>(response);
    }

    private async Task<Guid> CreatePaymentAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/Payments", new CreatePaymentRequest(250m, Guid.NewGuid(), null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return factory.Gateway.Charges[^1].PaymentId;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static void AssertFields(JsonElement element, params string[] expected) =>
        Assert.Equal(expected.OrderBy(x => x), element.EnumerateObject().Select(p => p.Name).OrderBy(x => x));

    [Fact]
    public async Task CreateService_ShouldReturn201WithDeclaredFieldsOr400()
    {
        var response = await _client.PostAsJsonAsync("/api/Service", new SaveServiceRequest("Alignment", "Front", 90m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertFields(await ReadJsonAsync(response), "id", "name", "description", "defaultPrice");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/Service", new SaveServiceRequest(" ", null, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/Service", new SaveServiceRequest("x", null, -1m))).StatusCode);
    }

    [Fact]
    public async Task EditService_ShouldReturn200Or404Or400()
    {
        var service = await CreateServiceAsync();

        var updated = await _client.PutAsJsonAsync($"/api/Service/{service.Id}", new SaveServiceRequest("Alignment", null, 95m));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(95m, (await ReadAsync<ServiceDto>(updated)).DefaultPrice);

        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PutAsJsonAsync($"/api/Service/{Guid.NewGuid()}", new SaveServiceRequest("x", null, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync($"/api/Service/{service.Id}", new SaveServiceRequest("", null, 1m))).StatusCode);
    }

    [Fact]
    public async Task GetAndListServices_ShouldReturnDeclaredFields()
    {
        var service = await CreateServiceAsync();

        var byId = await _client.GetAsync($"/api/Service/{service.Id}");
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        AssertFields(await ReadJsonAsync(byId), "id", "name", "description", "defaultPrice");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/Service/{Guid.NewGuid()}")).StatusCode);

        var list = await _client.GetFromJsonAsync<List<ServiceDto>>("/api/Service", Json);
        Assert.Contains(service, list!);
    }

    [Fact]
    public async Task DeleteService_ShouldReturn200ThenHideTheService()
    {
        var service = await CreateServiceAsync();

        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync($"/api/Service/{service.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/Service/{service.Id}")).StatusCode);
        Assert.DoesNotContain(service, (await _client.GetFromJsonAsync<List<ServiceDto>>("/api/Service", Json))!);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/Service/{service.Id}")).StatusCode);
    }

    [Fact]
    public async Task CreatePart_ShouldReturn201WithDeclaredFieldsOr400()
    {
        var response = await _client.PostAsJsonAsync("/api/Parts", new CreatePartRequest("Filter", $"F-{Guid.NewGuid():N}", 3, 10m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertFields(await ReadJsonAsync(response), "id", "name", "code", "quantityInStock", "price");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/Parts", new CreatePartRequest("Filter", "C", -1, 10m))).StatusCode);
    }

    [Fact]
    public async Task EditGetAndListParts()
    {
        var part = await CreatePartAsync();

        var updated = await _client.PutAsJsonAsync($"/api/Parts/{part.Id}", new UpdatePartRequest("Brake pad XL", part.Code, 90m));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        AssertFields(await ReadJsonAsync(updated), "id", "name", "code", "quantityInStock", "price");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync($"/api/Parts/{part.Id}", new UpdatePartRequest("", part.Code, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PutAsJsonAsync($"/api/Parts/{Guid.NewGuid()}", new UpdatePartRequest("x", "y", 1m))).StatusCode);

        Assert.Equal("Brake pad XL", (await _client.GetFromJsonAsync<PartDto>($"/api/Parts/{part.Id}", Json))!.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/Parts/{Guid.NewGuid()}")).StatusCode);
        Assert.Contains((await _client.GetFromJsonAsync<List<PartDto>>("/api/Parts", Json))!, p => p.Id == part.Id);
    }

    [Fact]
    public async Task AddAndRemoveStock_ShouldReturn204Or400Or404()
    {
        var part = await CreatePartAsync(stock: 4);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync($"/api/Parts/{part.Id}/add-stock", new StockQuantityRequest(3))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync($"/api/Parts/{part.Id}/remove-stock", new StockQuantityRequest(2))).StatusCode);
        Assert.Equal(5, (await _client.GetFromJsonAsync<PartDto>($"/api/Parts/{part.Id}", Json))!.QuantityInStock);

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/Parts/{part.Id}/add-stock", new StockQuantityRequest(0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/Parts/{part.Id}/remove-stock", new StockQuantityRequest(99))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/Parts/{Guid.NewGuid()}/add-stock", new StockQuantityRequest(1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/Parts/{Guid.NewGuid()}/remove-stock", new StockQuantityRequest(1))).StatusCode);
    }

    [Fact]
    public async Task DeletePart_ShouldReturn204ThenHideThePart()
    {
        var part = await CreatePartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/Parts/{part.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/Parts/{part.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/Parts/{part.Id}")).StatusCode);
    }

    [Fact]
    public async Task CreatePayment_ShouldReturn201WithDeclaredFields()
    {
        var service = await CreateServiceAsync();
        var part = await CreatePartAsync();

        var response = await _client.PostAsJsonAsync("/api/Payments",
            new CreatePaymentRequest(250m, Guid.NewGuid(), [service.Id], [new PaymentPartRequest(part.Id, 2)]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        AssertFields(body, "paymentUrl", "status", "totalAmount");
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(250m, body.GetProperty("totalAmount").GetDecimal());
    }

    [Fact]
    public async Task CreatePayment_WithOpenPayment_ShouldReturn200OrConflict()
    {
        var orderId = Guid.NewGuid();
        var first = await _client.PostAsJsonAsync("/api/Payments", new CreatePaymentRequest(250m, orderId, null, null));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var again = await _client.PostAsJsonAsync("/api/Payments", new CreatePaymentRequest(250m, orderId, null, null));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((await ReadJsonAsync(first)).GetProperty("paymentUrl").GetString(),
            (await ReadJsonAsync(again)).GetProperty("paymentUrl").GetString());

        var different = await _client.PostAsJsonAsync("/api/Payments", new CreatePaymentRequest(99m, orderId, null, null));
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
    }

    [Fact]
    public async Task CreatePayment_WithInvalidItem_ShouldReturn400()
    {
        var response = await _client.PostAsJsonAsync("/api/Payments",
            new CreatePaymentRequest(250m, Guid.NewGuid(), [Guid.NewGuid()], null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid or inactive part or service.", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreatePayment_WithGatewayFailure_ShouldReturn502()
    {
        var failing = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var gateway = new Mock<IPaymentGateway>();
            gateway.Setup(g => g.CreateChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PaymentGatewayException("down"));
            services.AddSingleton(gateway.Object);
        })).CreateClient();

        var response = await failing.PostAsJsonAsync("/api/Payments", new CreatePaymentRequest(250m, Guid.NewGuid(), null, null));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_Approved_ShouldReturn200()
    {
        var paymentId = await CreatePaymentAsync();
        factory.Gateway.Transactions["3001"] = new GatewayPayment("3001", "approved", paymentId.ToString("D"), 250m, "BRL");

        var response = await PostWebhookAsync("?data.id=3001&type=payment", Sign("3001", "1704908010"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_ShouldReadTypeFromBodyWhenMissingFromQuery()
    {
        var paymentId = await CreatePaymentAsync();
        factory.Gateway.Transactions["3002"] = new GatewayPayment("3002", "rejected", paymentId.ToString("D"), 250m, "BRL");

        var response = await PostWebhookAsync("?data.id=3002", Sign("3002"),
            """{"type":"payment","action":"payment.updated","data":{"id":"3002"}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_TransactionNotFound_ShouldReturn404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await PostWebhookAsync("?data.id=404404&type=payment", Sign("404404"))).StatusCode);

    [Fact]
    public async Task Webhook_WithoutOrWithFakeSignature_ShouldReturn401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync("?data.id=1&type=payment", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync("?data.id=1&type=payment", "ts=1,v1=abc")).StatusCode);
    }

    [Theory]
    [InlineData("merchant_order", "1")]
    [InlineData("payment", "../../v1/account")]
    public async Task Webhook_WithOtherTypeOrInvalidId_ShouldReturn200(string type, string dataId) =>
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync($"?data.id={Uri.EscapeDataString(dataId)}&type={type}", Sign(dataId))).StatusCode);
}

public class GlobalExceptionFilterTests
{
    private static ExceptionContext Run(Exception exception)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var context = new ExceptionContext(actionContext, []) { Exception = exception };
        new GlobalExceptionFilter(NullLogger<GlobalExceptionFilter>.Instance).OnException(context);
        return context;
    }

    [Theory]
    [InlineData(typeof(DomainException), StatusCodes.Status400BadRequest)]
    [InlineData(typeof(NotFoundException), StatusCodes.Status404NotFound)]
    public void OnException_ShouldMapStatus(Type type, int status)
    {
        var context = Run((Exception)Activator.CreateInstance(type, "error")!);

        Assert.True(context.ExceptionHandled);
        Assert.Equal(status, ((ObjectResult)context.Result!).StatusCode);
    }

    [Fact]
    public void OnException_Conflict_ShouldReturn409() =>
        Assert.Equal(StatusCodes.Status409Conflict, ((ObjectResult)Run(new ConcurrencyConflictException("x")).Result!).StatusCode);

    [Fact]
    public void OnException_Gateway_ShouldReturn502WithoutExposingDetails()
    {
        var result = (ObjectResult)Run(new PaymentGatewayException("secret token")).Result!;

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public void OnException_Unexpected_ShouldReturn500() =>
        Assert.Equal(StatusCodes.Status500InternalServerError, ((ObjectResult)Run(new InvalidOperationException("x")).Result!).StatusCode);
}

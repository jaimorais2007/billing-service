using System.Net;
using BillingService.Tests.Api;

namespace BillingService.Tests;

public class HealthCheckTests : IClassFixture<BillingApiFactory>
{
    private readonly HttpClient _client;

    public HealthCheckTests(BillingApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ShouldReturnHealthy()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Swagger_ShouldExposeOpenApiDocument()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

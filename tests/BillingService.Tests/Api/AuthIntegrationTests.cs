using System.Net;
using System.Security.Cryptography;
using System.Text;
using BillingService.Tests.Helpers;

namespace BillingService.Tests.Api;

public class AuthIntegrationTests(BillingApiFactory factory) : IClassFixture<BillingApiFactory>
{
    [Theory]
    [InlineData("/api/Parts")]
    [InlineData("/api/Service")]
    public async Task Rota_SemToken_DeveRetornar401(string rota)
    {
        var response = await factory.CreateClient().GetAsync(rota);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("assinatura")]
    [InlineData("emissor")]
    [InlineData("publico")]
    [InlineData("expirado")]
    public async Task Rota_ComTokenInvalido_DeveRetornar401(string defeito)
    {
        var token = defeito switch
        {
            "assinatura" => TestJwt.CreateToken(secret: "outra-chave-que-nao-e-a-do-servico-1234567890"),
            "emissor" => TestJwt.CreateToken(issuer: "emissor-desconhecido"),
            "publico" => TestJwt.CreateToken(audience: "publico-desconhecido"),
            _ => TestJwt.CreateToken(expires: DateTime.UtcNow.AddMinutes(-10))
        };

        var response = await TestJwt.Authorize(factory.CreateClient(), token).GetAsync("/api/Parts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rota_ComTokenValido_DeveRetornar200()
    {
        var response = await TestJwt.Authorize(factory.CreateClient()).GetAsync("/api/Parts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_SemToken_DeveRetornar200()
    {
        var response = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_SemToken_ComAssinaturaValida_NaoExigeJwt()
    {
        // O Mercado Pago não envia JWT: a rota é liberada e protegida pela assinatura x-signature.
        const string dataId = "404404";
        const string ts = "10";
        var manifest = $"id:{dataId};request-id:req-1;ts:{ts};";
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(BillingApiFactory.WebhookSecret), Encoding.UTF8.GetBytes(manifest));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/mercadopago?data.id={dataId}&type=payment");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.Add("x-request-id", "req-1");
        request.Headers.Add("x-signature", $"ts={ts},v1={Convert.ToHexString(hash).ToLowerInvariant()}");

        var response = await factory.CreateClient().SendAsync(request);

        // 404 = passou pela autenticação e chegou ao controller (pagamento inexistente).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

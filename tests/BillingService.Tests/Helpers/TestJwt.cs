using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace BillingService.Tests.Helpers;

/// <summary>Gera tokens no mesmo formato da Lambda de autenticação, para os testes.</summary>
public static class TestJwt
{
    public const string Secret = "chave-de-teste-apenas-para-os-testes-automatizados";
    public const string Issuer = "oficina-api";
    public const string Audience = "oficina-clientes";

    public static string CreateToken(
        string secret = Secret,
        string issuer = Issuer,
        string audience = Audience,
        DateTime? expires = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256);

        var expiresAt = expires ?? DateTime.UtcNow.AddMinutes(30);
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [new Claim("sub", "usuario-de-teste"), new Claim("role", "admin")],
            notBefore: expiresAt.AddHours(-1),
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static HttpClient Authorize(HttpClient client, string? token = null)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? CreateToken());
        return client;
    }
}

using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BillingService.Api.Configuration;

/// <summary>
/// Valida o JWT emitido pela Lambda de autenticação (o mesmo token que o API Gateway exige).
/// Assim o serviço continua protegido mesmo quando é chamado direto, sem passar pelo Gateway.
/// </summary>
public static class JwtAuthenticationConfiguration
{
    public const string DefaultIssuer = "oficina-api";
    public const string DefaultAudience = "oficina-clientes";
    public const string MissingSecretMessage =
        "Jwt:Secret não configurado. Defina a variável de ambiente Jwt__Secret com a mesma chave usada pela Lambda de autenticação.";

    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // A configuração é lida quando as opções são montadas (e não aqui), para valer
        // a configuração final do host, inclusive a injetada nos testes.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration>(ConfigureJwtBearer)
            .Validate(options => options.TokenValidationParameters.IssuerSigningKey is not null, MissingSecretMessage)
            .ValidateOnStart();

        // Toda rota exige token, exceto as marcadas com [AllowAnonymous] / AllowAnonymous().
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    public static void ConfigureJwtBearer(JwtBearerOptions options, IConfiguration configuration)
    {
        var secret = configuration["Jwt:Secret"];

        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuration["Jwt:Issuer"] ?? DefaultIssuer,
            ValidateAudience = true,
            ValidAudience = configuration["Jwt:Audience"] ?? DefaultAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = string.IsNullOrWhiteSpace(secret)
                ? null
                : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret))
        };
    }

    /// <summary>Habilita o botão "Authorize" no Swagger para enviar o token Bearer.</summary>
    public static void AddJwtSecurity(this SwaggerGenOptions options)
    {
        var scheme = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Token JWT obtido em POST /authenticate do API Gateway.",
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
        };

        options.AddSecurityDefinition("Bearer", scheme);
        options.AddSecurityRequirement(new OpenApiSecurityRequirement { { scheme, Array.Empty<string>() } });
    }
}

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.SwaggerGen;
using BillingService.Api.Configuration;
using BillingService.Tests.Helpers;
using Xunit;

namespace BillingService.Tests.Configuration;

public class JwtAuthenticationConfigurationTests
{
    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(BuildConfiguration(values));
        services.AddJwtAuthentication();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ConfigureJwtBearer_ComSegredo_ValidaIssuerAudienceValidadeEAssinatura()
    {
        var options = new JwtBearerOptions();

        JwtAuthenticationConfiguration.ConfigureJwtBearer(options,
            BuildConfiguration(new() { ["Jwt:Secret"] = TestJwt.Secret }));

        var parameters = options.TokenValidationParameters;
        Assert.True(parameters.ValidateIssuer);
        Assert.True(parameters.ValidateAudience);
        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.ValidateIssuerSigningKey);
        Assert.Equal(JwtAuthenticationConfiguration.DefaultIssuer, parameters.ValidIssuer);
        Assert.Equal(JwtAuthenticationConfiguration.DefaultAudience, parameters.ValidAudience);
        Assert.NotNull(parameters.IssuerSigningKey);
        Assert.False(options.MapInboundClaims);
    }

    [Fact]
    public void ConfigureJwtBearer_UsaIssuerEAudienceDaConfiguracao()
    {
        var options = new JwtBearerOptions();

        JwtAuthenticationConfiguration.ConfigureJwtBearer(options, BuildConfiguration(new()
        {
            ["Jwt:Secret"] = TestJwt.Secret,
            ["Jwt:Issuer"] = "outro-emissor",
            ["Jwt:Audience"] = "outro-publico"
        }));

        Assert.Equal("outro-emissor", options.TokenValidationParameters.ValidIssuer);
        Assert.Equal("outro-publico", options.TokenValidationParameters.ValidAudience);
    }

    [Fact]
    public void ConfigureJwtBearer_SemSegredo_NaoDefineChave()
    {
        var options = new JwtBearerOptions();

        JwtAuthenticationConfiguration.ConfigureJwtBearer(options, BuildConfiguration(new()));

        Assert.Null(options.TokenValidationParameters.IssuerSigningKey);
    }

    [Fact]
    public void AddJwtAuthentication_SemSegredo_FalhaNaValidacao()
    {
        using var provider = BuildProvider(new());

        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        var ex = Assert.Throws<OptionsValidationException>(() => monitor.Get(JwtBearerDefaults.AuthenticationScheme));
        Assert.Contains("Jwt:Secret", ex.Message);
    }

    [Fact]
    public void AddJwtAuthentication_ComSegredo_ExigeUsuarioAutenticadoPorPadrao()
    {
        using var provider = BuildProvider(new() { ["Jwt:Secret"] = TestJwt.Secret });

        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var authorization = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        Assert.NotNull(jwt.TokenValidationParameters.IssuerSigningKey);
        Assert.NotNull(authorization.FallbackPolicy);
        Assert.Contains(authorization.FallbackPolicy!.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void AddJwtSecurity_RegistraEsquemaBearerNoSwagger()
    {
        var options = new SwaggerGenOptions();

        options.AddJwtSecurity();

        Assert.True(options.SwaggerGeneratorOptions.SecuritySchemes.ContainsKey("Bearer"));
        Assert.Single(options.SwaggerGeneratorOptions.SecurityRequirements);
    }
}

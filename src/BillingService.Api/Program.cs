using System.Text.Json.Serialization;
using BillingService.Api.Filters;
using BillingService.Application;
using BillingService.Infrastructure;
using BillingService.Infrastructure.Messaging;
using BillingService.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

builder.Services.AddControllers(options =>
{
    options.Filters.Add<GlobalExceptionFilter>();
}).AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.AddJwtSecurity());

// Exige o JWT da Lambda de autenticação em todas as rotas (exceto /health e o webhook)
builder.Services.AddJwtAuthentication();
builder.Services.AddHealthChecks();

builder.Services.AddOTelConfiguration(builder.Configuration);
builder.Logging.AddOTelLogging();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();

await app.RunAsync();

public partial class Program;

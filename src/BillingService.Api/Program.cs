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
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

builder.Services.AddOTelConfiguration(builder.Configuration);
builder.Logging.AddOTelLogging();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health");
app.MapControllers();

await app.RunAsync();

public partial class Program;

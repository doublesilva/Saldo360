using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Saldo360.Api.Health;
using Saldo360.Application;
using Saldo360.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.Run();

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Saldo360.Api.Authentication;
using Saldo360.Api.Health;
using Saldo360.Application;
using Saldo360.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddSaldo360ForwardedHeaders(builder.Configuration)
    .AddSaldo360Authentication(builder.Configuration, builder.Environment);

var app = builder.Build();

// Antes da autenticação: o callback OIDC é montado com o esquema e o host públicos do proxy confiável.
app.UseForwardedHeaders();

app.UseAuthentication();
app.UseAuthorization();

// Público: não exige sessão.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.MapAuthEndpoints();

app.Run();

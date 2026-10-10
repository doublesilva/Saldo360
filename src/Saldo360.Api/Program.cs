using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Text.Json.Serialization;
using Saldo360.Api.Accounts;
using Saldo360.Api.Categories;
using Saldo360.Api.Authentication;
using Saldo360.Api.Families;
using Saldo360.Api.Health;
using Saldo360.Application;
using Saldo360.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddSaldo360ForwardedHeaders(builder.Configuration)
    .AddSaldo360Authentication(builder.Configuration, builder.Environment);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();

// Enums (ex.: papel na família) trafegam pelo nome: "Owner", "Member".
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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
app.MapFamilyEndpoints();
app.MapFamilyInvitationEndpoints();
app.MapFinancialAccountEndpoints();
app.MapFinancialCategoryEndpoints();

app.Run();

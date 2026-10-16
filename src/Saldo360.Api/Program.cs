using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Text.Json.Serialization;
using Saldo360.Api.Accounts;
using Saldo360.Api.Categories;
using Saldo360.Api.Transactions;
using Saldo360.Api.Transfers;
using Saldo360.Api.Authentication;
using Saldo360.Api.Families;
using Saldo360.Api.Planning;
using Saldo360.Api.Health;
using Saldo360.Api.Calendar;
using Saldo360.Api.Budgets;
using Saldo360.Api.CreditCards;
using Saldo360.Api.Recurrences;
using Saldo360.Api.Debts;
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
app.MapFinancialTransactionEndpoints();
app.MapFinancialTransferEndpoints();
app.MapFinancialPlannedTransactionEndpoints();
app.MapFinancialCalendarEndpoints();
app.MapFinancialBudgetEndpoints();
app.MapFinancialCreditCardEndpoints();
app.MapFinancialCreditCardPurchaseEndpoints();
app.MapFinancialCreditCardInvoiceEndpoints();
app.MapFinancialPlannedInvoiceReconciliationEndpoints();
app.MapCreditCardCsvPreviewEndpoints();
app.MapCreditCardPdfPreviewEndpoints();
app.MapFinancialCreditCardImportEndpoints();
app.MapFinancialCreditCardImportedInstallmentEndpoints();
app.MapFinancialRecurrenceEndpoints();
app.MapFinancialPlanningScenarioEndpoints();
app.MapFinancialDebtEndpoints();
app.MapFinancialDebtInstallmentEndpoints();

app.Run();

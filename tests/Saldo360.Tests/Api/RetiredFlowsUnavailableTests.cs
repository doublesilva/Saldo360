using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Saldo360.Application.Ai;
using Saldo360.Application.Classification;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// R00: os fluxos de coleta por e-mail, Inbox/importação documental (CSV/PDF), conferências e conciliações saíram do produto. Estes testes garantem que
/// nenhuma rota, job ou serviço deles continua registrado e que o que precisa permanecer (Pluggy e agente de IA) continua disponível.
/// </summary>
[Trait("Category", "CORE")]
[Trait("Area", "Platform")]
[Trait("Profile", "FAST")]
public sealed class RetiredFlowsUnavailableTests : ApiTestBase
{
    private static readonly string[] RetiredRouteMarkers =
    [
        "/imports", "financial-documents", "financial-email-import", "invoice-reconciliations", "debt-installment-reconciliations",
        "service-accounts", "balance-reconciliation", "statement-analysis", "statement-processing", "pdf-password",
    ];

    // Habilitar a configuração antiga NÃO pode reativar a coleta: não há mais nada que a leia.
    public RetiredFlowsUnavailableTests() : base(settings: new Dictionary<string, string?>
    {
        ["FinancialEmailImport:Enabled"] = "true",
        ["FinancialEmailImport:Sources:0:Name"] = "Synthetic",
        ["FinancialEmailImport:Sources:0:Enabled"] = "true",
        ["FinancialDocuments:StoragePath"] = "synthetic-not-used",
        ["ServiceBillExtraction:Enabled"] = "true",
        ["FinancialDocumentClassification:Enabled"] = "true",
    })
    {
    }

    [Fact]
    public void NoRetiredRouteIsMapped()
    {
        var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty).ToList();
        Assert.NotEmpty(endpoints);
        Assert.All(RetiredRouteMarkers, marker => Assert.DoesNotContain(endpoints, route => route.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        // Os pontos de entrada que permanecem: Pluggy e cartões/faturas/projeção.
        Assert.Contains(endpoints, route => route.Contains("pluggy-integration", StringComparison.Ordinal));
        Assert.Contains(endpoints, route => route.Contains("credit-cards", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("POST", "/api/financial-workspaces/{w}/credit-cards/{c}/imports/preview")]
    [InlineData("POST", "/api/financial-workspaces/{w}/credit-cards/{c}/imports/pdf/preview")]
    [InlineData("POST", "/api/financial-workspaces/{w}/credit-cards/{c}/imports/email/latest")]
    [InlineData("GET", "/api/financial-workspaces/{w}/financial-documents/inbox")]
    [InlineData("POST", "/api/financial-workspaces/{w}/financial-documents/inbox")]
    [InlineData("GET", "/api/financial-email-import/status")]
    [InlineData("POST", "/api/financial-email-import/ItauCards/run")]
    [InlineData("GET", "/api/financial-workspaces/{w}/invoice-reconciliations")]
    [InlineData("GET", "/api/financial-workspaces/{w}/debt-installment-reconciliations")]
    [InlineData("GET", "/api/financial-workspaces/{w}/accounts/balance-reconciliation")]
    [InlineData("GET", "/api/me/service-accounts")]
    public async Task AuthenticatedRequestsToRetiredRoutesAreNotFound(string method, string template)
    {
        var session = await SignInAsync("r00-retired");
        var url = template.Replace("{w}", Guid.NewGuid().ToString()).Replace("{c}", Guid.NewGuid().ToString());
        string? csrf = null;
        if (method == "POST")
        {
            var (csrfCookie, token) = await GetCsrfAsync(session);
            session = $"{session}; {csrfCookie}";
            csrf = token;
        }
        var response = await SendAsync(new HttpMethod(method), url, session, csrf);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void NoCollectionOrDocumentJobIsRegisteredButPluggyAutoSyncIs()
    {
        var jobs = Factory.Services.GetServices<IHostedService>().Select(service => service.GetType().Name).ToList();
        Assert.DoesNotContain(jobs, name => name.Contains("Email", StringComparison.OrdinalIgnoreCase) || name.Contains("Collector", StringComparison.OrdinalIgnoreCase) || name.Contains("Document", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("PluggyAutoSyncHostedService", jobs);
    }

    [Fact]
    public void TheAiAgentStaysAvailableForClassificationThroughASingleProvider()
    {
        using var scope = Factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IAiCompletionProvider>();
        var options = scope.ServiceProvider.GetRequiredService<AiAgentOptions>();
        var model = scope.ServiceProvider.GetRequiredService<IFinancialClassificationModel>();
        Assert.Equal("OpenAiCompletionProvider", provider.GetType().Name);
        Assert.IsType<OpenAiFinancialClassificationModel>(model);
        Assert.False(model.IsAvailable); // Desligada por padrão: nada é enviado sem habilitação e credencial explícitas.
        Assert.False(options.Enabled);
        Assert.Single(Factory.Services.GetServices<IAiCompletionProvider>());
    }

    [Fact]
    public void RetiredModulesAreGoneFromTheApplicationAndInfrastructureAssemblies()
    {
        string[] retired = ["EmailCollector", "GmailFinancialEmail", "DocumentInbox", "StatementDocumentary", "CreditCardPdf", "CreditCardCsv", "ServiceBill", "ServiceAccountBinding", "PlannedInvoiceReconciliation", "DebtInstallmentReconciliation", "ImportedInstallment"];
        var assemblies = new[] { typeof(Saldo360.Application.DependencyInjection).Assembly, typeof(Saldo360.Infrastructure.DependencyInjection).Assembly, typeof(Saldo360.Api.Authentication.CurrentUser).Assembly };
        var leftovers = assemblies.SelectMany(assembly => assembly.GetTypes()).Select(type => type.FullName ?? type.Name)
            // Migrações (histórico) e o mapeamento EF das tabelas dormentes (docs/R00, seção 4) não contam.
            .Where(name => !name.Contains(".Persistence.Migrations.", StringComparison.Ordinal) && !name.Contains(".Persistence.Configurations.", StringComparison.Ordinal))
            .Where(name => retired.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.Empty(leftovers);
    }
}

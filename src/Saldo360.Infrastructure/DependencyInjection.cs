using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Accounts;
using Saldo360.Application.Categories;
using Saldo360.Application.Transactions;
using Saldo360.Application.Transfers;
using Saldo360.Application.Families;
using Saldo360.Application.Planning;
using Saldo360.Application.Debts;
using Saldo360.Application.Goals;
using Saldo360.Infrastructure.Goals;
using Saldo360.Application.Users;
using Saldo360.Infrastructure.Accounts;
using Saldo360.Infrastructure.Categories;
using Saldo360.Infrastructure.Transactions;
using Saldo360.Infrastructure.Transfers;
using Saldo360.Infrastructure.Authentication;
using Saldo360.Infrastructure.Families;
using Saldo360.Infrastructure.Planning;
using Saldo360.Infrastructure.Debts;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Application.Calendar;
using Saldo360.Infrastructure.Calendar;
using Saldo360.Application.Budgets;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.Budgets;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Infrastructure.Recurrences;
using Saldo360.Application.Recurrences;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Saldo360.Infrastructure.Health;

namespace Saldo360.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Saldo360";
    public const string DatabaseHealthCheckName = "postgresql";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // A mensagem cita apenas a chave, nunca o valor.
            throw new InvalidOperationException(
                $"A connection string '{ConnectionStringName}' não foi configurada. " +
                $"Defina 'ConnectionStrings:{ConnectionStringName}' via user-secrets ou variável de ambiente " +
                $"'ConnectionStrings__{ConnectionStringName}'.");
        }

        services.AddDbContext<Saldo360DbContext>(options => Saldo360DbContext.Configure(options, connectionString));

        var readinessTimeout = configuration.GetValue<TimeSpan?>("HealthChecks:ReadinessTimeout")
            ?? TimeSpan.FromSeconds(3);
        if (readinessTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("HealthChecks:ReadinessTimeout deve ser maior que zero.");
        }

        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
            .Add(new HealthCheckRegistration(
                DatabaseHealthCheckName,
                serviceProvider => ActivatorUtilities.CreateInstance<DatabaseReadinessHealthCheck>(serviceProvider),
                HealthStatus.Unhealthy,
                ["ready"],
                readinessTimeout));

        services.AddDataProtection()
            .SetApplicationName("Saldo360")
            .PersistKeysToDbContext<Saldo360DbContext>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITicketStore, DatabaseTicketStore>();

        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IFamilyRepository, FamilyRepository>();
        services.AddScoped<Saldo360.Application.Workspaces.IFinancialWorkspaceRepository, Saldo360.Infrastructure.Workspaces.FinancialWorkspaceRepository>();
        services.AddScoped<Saldo360.Application.Workspaces.IFinancialWorkspaceResetService, Saldo360.Infrastructure.Workspaces.FinancialWorkspaceResetService>();
        services.AddScoped<IFamilyInvitationRepository, FamilyInvitationRepository>();
        services.AddScoped<IFinancialAccountRepository, FinancialAccountRepository>();
        services.AddScoped<IFinancialCategoryRepository, FinancialCategoryRepository>();
        services.AddScoped<IFinancialTransactionRepository, FinancialTransactionRepository>();
        services.AddScoped<IFinancialTransferRepository, FinancialTransferRepository>();
        services.AddScoped<IFinancialPlannedTransactionRepository, FinancialPlannedTransactionRepository>();
        services.AddScoped<IReferenceLookup>(sp => sp.GetRequiredService<IFinancialPlannedTransactionRepository>());
        services.AddScoped<IFinancialCalendarRepository, FinancialCalendarRepository>();
        services.AddScoped<IFinancialCalendarBalanceRepository, FinancialCalendarBalanceRepository>();
        services.AddScoped<IFinancialBudgetRepository, FinancialBudgetRepository>();
        services.AddScoped<Saldo360.Application.Bootstrap.IFinancialBootstrapExtensions, Saldo360.Infrastructure.Bootstrap.FinancialBootstrapExtensions>();
        services.AddScoped<Saldo360.Application.Bootstrap.IBootstrapProvenanceRepository, Saldo360.Infrastructure.Bootstrap.BootstrapProvenanceRepository>();
        services.AddScoped<IFinancialBudgetExecutionRepository, FinancialBudgetExecutionRepository>();
        services.AddScoped<IFinancialCreditCardRepository, FinancialCreditCardRepository>();
        services.AddScoped<ICreditCardPdfContextRepository, FinancialCreditCardRepository>();
        services.AddScoped<ICreditCardStatementResolutionRepository, FinancialCreditCardRepository>();
        services.AddScoped<ICreditCardPdfPreReconciliationRepository, CreditCardPdfPreReconciliationRepository>();
        services.AddScoped<ICreditCardPdfConfirmationRepository, CreditCardPdfConfirmationRepository>();
        services.AddScoped<IFinancialDocumentInboxRepository, FinancialDocumentInboxRepository>();
        services.AddScoped<IStatementCategoryContextRepository, StatementCategoryContextRepository>();
        services.AddScoped<IFinancialEmailCollectorCheckpointRepository, FinancialEmailCollectorCheckpointRepository>();
        services.AddSingleton<IFinancialDocumentStorage, FileSystemFinancialDocumentStorage>();
        var emailImport = configuration.GetSection("FinancialEmailImport").Get<FinancialEmailImportOptions>() ?? new FinancialEmailImportOptions();
        services.AddSingleton(emailImport);
        services.AddSingleton<FinancialEmailSchedule>();
        services.AddSingleton<IFinancialEmailCollectorLock>(_ => new PostgresFinancialEmailCollectorLock(connectionString));
        services.AddHostedService<FinancialEmailCollectorHostedService>();
        // Queries and Gmail identifiers live in request URLs. Do not emit default URL logs.
        services.AddHttpClient("GmailFinancialEmail", client => client.BaseAddress = new Uri("https://gmail.googleapis.com/")).RemoveAllLoggers();
        services.AddHttpClient("GmailFinancialEmailToken", client => client.BaseAddress = new Uri("https://oauth2.googleapis.com/")).RemoveAllLoggers();
        services.AddHttpClient("FinancialLinkedDocument", client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<IFinancialEmailSource, GmailFinancialEmailSource>();
        services.AddSingleton<IFinancialEmailLinkExtractor, FinancialEmailLinkExtractor>();
        services.AddSingleton<IFinancialHostAddressResolver, FinancialHostAddressResolver>();
        services.AddSingleton<IFinancialLinkedDocumentDownloader, FinancialLinkedDocumentDownloader>();
        services.AddScoped<IFinancialCreditCardPurchaseRepository, FinancialCreditCardPurchaseRepository>();
        services.AddScoped<IFinancialCreditCardInvoicePaymentRepository, FinancialCreditCardInvoicePaymentRepository>();
        services.AddScoped<IFinancialCreditCardInvoiceAdjustmentRepository, FinancialCreditCardInvoiceAdjustmentRepository>();
        services.AddScoped<IFinancialPlannedInvoiceReconciliationRepository, FinancialPlannedInvoiceReconciliationRepository>();
        services.AddScoped<IFinancialCreditCardImportRepository, FinancialCreditCardImportRepository>();
        services.AddScoped<IFinancialCreditCardImportedInstallmentRepository, FinancialCreditCardImportedInstallmentRepository>();
        services.AddScoped<IFinancialRecurrenceRepository, FinancialRecurrenceRepository>();
        services.AddScoped<IFinancialRecurrenceMaterializationRepository, FinancialRecurrenceMaterializationRepository>();
        services.AddScoped<IFinancialPlanningScenarioRepository, FinancialPlanningScenarioRepository>();
        services.AddScoped<IFinancialDebtRepository, FinancialDebtRepository>();
        services.AddScoped<IFinancialDebtInstallmentRepository, FinancialDebtInstallmentRepository>();
        services.AddScoped<IFinancialDebtInstallmentReconciliationRepository, FinancialDebtInstallmentReconciliationRepository>();
        services.AddScoped<IFinancialGoalRepository, FinancialGoalRepository>();
        services.AddScoped<IFinancialGoalAllocationRepository, FinancialGoalAllocationRepository>();
        services.AddSingleton<LocalStatementPageReader>();
        services.AddSingleton<ICreditCardPdfTextExtractor, CreditCardPdfTextExtractor>();
        var statementAgent = configuration.GetSection("CreditCardStatementAgent").Get<CreditCardStatementAgentOptions>() ?? new CreditCardStatementAgentOptions();
        services.AddSingleton(statementAgent);
        services.AddHttpClient("CreditCardStatementAgent", client =>
        {
            client.BaseAddress = new Uri(statementAgent.Endpoint);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(statementAgent.TimeoutSeconds, 5, 180));
        }).RemoveAllLoggers();
        services.AddSingleton<ICreditCardStatementAgentProvider, OpenAiCreditCardStatementAgentProvider>();
        services.AddScoped<IStatementDocumentaryPipeline, StatementDocumentaryPipeline>();
        services.AddHttpClient("CreditCardStatementDocumentary", client =>
        {
            client.BaseAddress = new Uri(statementAgent.Endpoint);
            client.Timeout = TimeSpan.FromSeconds(180);
        }).RemoveAllLoggers();
        var documentClassification = configuration.GetSection("FinancialDocumentClassification").Get<FinancialDocumentClassificationOptions>() ?? new FinancialDocumentClassificationOptions();
        services.AddSingleton(documentClassification);
        services.AddSingleton<IFinancialDocumentClassificationAgent, FinancialDocumentClassificationAgent>();
        var serviceBillExtraction = configuration.GetSection("ServiceBillExtraction").Get<ServiceBillExtractionOptions>() ?? new ServiceBillExtractionOptions();
        services.AddSingleton(serviceBillExtraction);
        services.AddSingleton<IServiceBillExtractionAgent, ServiceBillExtractionAgent>();
        var serviceBillReconciliation = configuration.GetSection("ServiceBillReconciliation").Get<ServiceBillReconciliationOptions>() ?? new ServiceBillReconciliationOptions();
        services.AddSingleton(serviceBillReconciliation);
        services.AddScoped<IServiceBillRecurringCandidateRepository, ServiceBillRecurringCandidateRepository>();
        services.AddScoped<IServiceBillConfirmationRepository, ServiceBillConfirmationRepository>();
        services.AddScoped<IServiceAccountBindingRepository, ServiceAccountBindingRepository>();
        services.AddScoped<Saldo360.Application.ServiceAccounts.IServiceAccountRepository, Saldo360.Infrastructure.ServiceAccounts.ServiceAccountRepository>();
        services.AddSingleton<Saldo360.Application.ServiceAccounts.IServiceAccountIdentifierProtection, Saldo360.Infrastructure.ServiceAccounts.ServiceAccountIdentifierProtection>();
        services.AddSingleton<LlmCreditCardStatementExtractorAgent>();
        services.AddSingleton<ParserBackedCreditCardStatementExtractorAgent>();
        services.AddSingleton<IParserBackedCreditCardStatementExtractorAgent>(provider => provider.GetRequiredService<ParserBackedCreditCardStatementExtractorAgent>());
        services.AddSingleton<ICreditCardPdfPasswordProvider>(serviceProvider =>
            new LocalCreditCardPdfPasswordProvider(configuration, serviceProvider.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>()));

        AddPluggyPoc(services, configuration);

        return services;
    }

    /// <summary>POC Pluggy (somente leitura). Credenciais só por user-secrets/variáveis de ambiente (<c>Pluggy__ClientSecret</c>).</summary>
    private static void AddPluggyPoc(IServiceCollection services, IConfiguration configuration)
    {
        var pluggy = configuration.GetSection(Saldo360.Application.Pluggy.PluggyOptions.SectionName).Get<Saldo360.Application.Pluggy.PluggyOptions>() ?? new();
        services.AddSingleton(pluggy);
        // Caminhos e respostas da Pluggy carregam identificadores bancários: sem logs padrão de URL.
        services.AddHttpClient(Saldo360.Infrastructure.Pluggy.PluggyApiKeyProvider.ClientName, client =>
        {
            client.BaseAddress = new Uri(pluggy.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(pluggy.TimeoutSeconds, 5, 120));
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
        services.AddSingleton<Saldo360.Infrastructure.Pluggy.PluggyApiKeyProvider>();
        services.AddSingleton<Saldo360.Application.Pluggy.IPluggyClient, Saldo360.Infrastructure.Pluggy.PluggyClient>();
        services.AddScoped<Saldo360.Application.Pluggy.IPluggyItemLinkRepository, Saldo360.Infrastructure.Pluggy.PluggyItemLinkRepository>();
    }
}

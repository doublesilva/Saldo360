using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Accounts;
using Saldo360.Application.Ai;
using Saldo360.Infrastructure.Ai;
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
        services.AddScoped<IFinancialCreditCardPurchaseRepository, FinancialCreditCardPurchaseRepository>();
        services.AddScoped<Saldo360.Application.CreditCards.Projection.ICardProjectionRepository, CardProjectionRepository>();
        services.AddScoped<Saldo360.Application.Accounts.Reconciliation.IAccountBalanceReconciliationRepository, Saldo360.Infrastructure.Accounts.AccountBalanceReconciliationRepository>();
        services.AddScoped<Saldo360.Application.Dashboard.PlanActual.IPlanActualRepository, Saldo360.Infrastructure.Dashboard.PlanActualRepository>();
        services.AddScoped<Saldo360.Infrastructure.MonthlyPlan.MonthlyPlanRepository>();
        services.AddScoped<Saldo360.Application.MonthlyPlan.IMonthlyPlanRepository>(sp => sp.GetRequiredService<Saldo360.Infrastructure.MonthlyPlan.MonthlyPlanRepository>());
        services.AddScoped<Saldo360.Application.MonthlyPlan.IPlanHistoryWriter>(sp => sp.GetRequiredService<Saldo360.Infrastructure.MonthlyPlan.MonthlyPlanRepository>());
        services.AddScoped<IFinancialCreditCardInvoicePaymentRepository, FinancialCreditCardInvoicePaymentRepository>();
        services.AddScoped<IFinancialCreditCardInvoiceAdjustmentRepository, FinancialCreditCardInvoiceAdjustmentRepository>();
        services.AddScoped<IFinancialRecurrenceRepository, FinancialRecurrenceRepository>();
        services.AddScoped<IFinancialRecurrenceMaterializationRepository, FinancialRecurrenceMaterializationRepository>();
        services.AddScoped<IFinancialPlanningScenarioRepository, FinancialPlanningScenarioRepository>();
        services.AddScoped<IFinancialDebtRepository, FinancialDebtRepository>();
        services.AddScoped<IFinancialDebtInstallmentRepository, FinancialDebtInstallmentRepository>();
        services.AddScoped<IFinancialGoalRepository, FinancialGoalRepository>();
        services.AddScoped<IFinancialGoalAllocationRepository, FinancialGoalAllocationRepository>();
        // Agente de IA (único cliente OpenAI): serve à classificação de lançamentos. A seção continua sendo CreditCardStatementAgent (nome histórico, ver AiAgentOptions).
        var aiAgent = configuration.GetSection(AiAgentOptions.SectionName).Get<AiAgentOptions>() ?? new AiAgentOptions();
        services.AddSingleton(aiAgent);
        services.AddHttpClient(OpenAiCompletionProvider.ClientName, client =>
        {
            client.BaseAddress = new Uri(aiAgent.Endpoint);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(aiAgent.TimeoutSeconds, 5, 180));
        }).RemoveAllLoggers();
        services.AddSingleton<IAiCompletionProvider, OpenAiCompletionProvider>();

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
        services.AddScoped<Saldo360.Application.Pluggy.IPluggyImportRepository, Saldo360.Infrastructure.Pluggy.PluggyImportRepository>();
        // T09.6: sincronização automática (desligada por padrão). Mesmo padrão de execução periódica do coletor de e-mail; sem infraestrutura nova de jobs.
        services.AddSingleton(configuration.GetSection(Saldo360.Application.Pluggy.PluggyAutoSyncOptions.SectionName).Get<Saldo360.Application.Pluggy.PluggyAutoSyncOptions>() ?? new());
        services.AddScoped<Saldo360.Application.Pluggy.IPluggySyncStateRepository, Saldo360.Infrastructure.Pluggy.PluggySyncStateRepository>();
        services.AddHostedService<Saldo360.Infrastructure.Pluggy.PluggyAutoSyncHostedService>();
        services.AddScoped<Saldo360.Application.Pluggy.IPluggyFinancialRepository, Saldo360.Infrastructure.Pluggy.PluggyFinancialRepository>();
        services.AddScoped<Saldo360.Application.Pluggy.IPluggyPostingEngine, Saldo360.Infrastructure.Pluggy.PluggyPostingEngine>();
        services.AddScoped<Saldo360.Application.Pluggy.IPluggyCategorizationRepository, Saldo360.Infrastructure.Pluggy.PluggyCategorizationRepository>();
        // Classificação financeira: o fallback de IA usa o provider de IA registrado acima (mesma credencial e cliente HTTP); só é "disponível" com AiEnabled.
        var classification = configuration.GetSection(Saldo360.Application.Classification.FinancialClassificationOptions.SectionName).Get<Saldo360.Application.Classification.FinancialClassificationOptions>() ?? new();
        services.AddSingleton(classification);
        services.AddSingleton<Saldo360.Application.Classification.IFinancialClassificationModel, Saldo360.Application.Classification.OpenAiFinancialClassificationModel>();
    }
}

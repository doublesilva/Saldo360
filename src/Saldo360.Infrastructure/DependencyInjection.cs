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
        services.AddScoped<IFinancialBudgetExecutionRepository, FinancialBudgetExecutionRepository>();
        services.AddScoped<IFinancialCreditCardRepository, FinancialCreditCardRepository>();
        services.AddScoped<ICreditCardPdfContextRepository, FinancialCreditCardRepository>();
        services.AddScoped<ICreditCardPdfPreReconciliationRepository, CreditCardPdfPreReconciliationRepository>();
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
        services.AddSingleton<ICreditCardPdfTextExtractor, CreditCardPdfTextExtractor>();
        services.AddSingleton<ICreditCardPdfPasswordProvider>(serviceProvider =>
            new LocalCreditCardPdfPasswordProvider(configuration, serviceProvider.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>()));

        return services;
    }
}

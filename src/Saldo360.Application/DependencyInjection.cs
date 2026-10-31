using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Accounts;
using Saldo360.Application.Categories;
using Saldo360.Application.Transactions;
using Saldo360.Application.Transfers;
using Saldo360.Application.Families;
using Saldo360.Application.Planning;
using Saldo360.Application.Calendar;
using Saldo360.Application.Budgets;
using Saldo360.Application.CreditCards;
using Saldo360.Application.Recurrences;
using Saldo360.Application.Debts;
using Saldo360.Application.Goals;
using Saldo360.Application.Dashboard;

namespace Saldo360.Application;

public static class DependencyInjection
{
    /// <summary>Registra os serviços de aplicação (casos de uso).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<FamilyService>();
        services.AddScoped<Saldo360.Application.Pluggy.PluggyPocService>();
        services.AddScoped<Saldo360.Application.Pluggy.PluggyTransactionSyncService>();
        services.AddScoped<Saldo360.Application.Pluggy.PluggyAutoSyncRunner>();
        services.AddScoped<Saldo360.Application.Pluggy.PluggySyncMonitorService>();
        services.AddScoped<Saldo360.Application.Pluggy.PluggyAutomationService>();
        services.AddScoped<Saldo360.Application.Classification.ClassificationReviewService>();
        services.AddScoped<Saldo360.Application.Pluggy.PluggyCategorizationJob>();
        services.AddScoped<Saldo360.Application.Pluggy.PluggyAccountLinkService>();
        services.AddScoped<Saldo360.Application.Pluggy.PluggyReviewService>();
        services.AddScoped<Saldo360.Application.Classification.IFinancialClassificationService, Saldo360.Application.Classification.FinancialClassificationService>();
        services.AddScoped<Saldo360.Application.Pluggy.IPluggyCategorizationService, Saldo360.Application.Pluggy.PluggyCategorizationService>();
        services.AddScoped<FamilyInvitationService>();
        services.AddScoped<FinancialAccountService>();
        services.AddSingleton<IDefaultCategoryCatalog>(StandardDefaultCategoryCatalog.Instance);
        services.AddScoped<FinancialCategoryService>();
        services.AddScoped<FinancialTransactionService>();
        services.AddScoped<FinancialTransferService>();
        services.AddScoped<FinancialPlannedTransactionService>();
        services.AddScoped<FinancialCalendarService>();
        services.AddScoped<FinancialCalendarBalanceService>();
        services.AddScoped<FinancialCalendarMonthlyProjectionService>();
        services.AddScoped<FinancialBudgetService>();
        services.AddScoped<FinancialBudgetExecutionService>();
        services.AddScoped<FinancialCreditCardService>();
        services.AddScoped<Saldo360.Application.CreditCards.Projection.CardProjectionService>();
        services.AddScoped<Saldo360.Application.Accounts.Reconciliation.AccountBalanceReconciliationService>();
        services.AddScoped<Saldo360.Application.Dashboard.PlanActual.PlanActualService>();
        services.AddScoped<Saldo360.Application.Dashboard.Overview.DashboardOverviewService>();
        services.AddScoped<Saldo360.Application.MonthlyPlan.MonthlyPlanService>();
        services.AddScoped<FinancialCreditCardPurchaseService>();
        services.AddScoped<FinancialCreditCardInvoicePaymentService>();
        services.AddScoped<FinancialCreditCardInvoiceAdjustmentService>();
        services.AddScoped<FinancialRecurrenceService>();
        services.AddScoped<FinancialPlanningScenarioService>();
        services.AddScoped<FinancialPlanningScenarioProjectionService>();
        services.AddScoped<FinancialDebtService>();
        services.AddScoped<FinancialDebtInstallmentService>();
        services.AddScoped<FinancialGoalService>();
        services.AddScoped<FinancialGoalAllocationService>();
        return services;
    }
}

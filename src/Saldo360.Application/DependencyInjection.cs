using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Accounts;
using Saldo360.Application.Categories;
using Saldo360.Application.Transactions;
using Saldo360.Application.Transfers;
using Saldo360.Application.Families;
using Saldo360.Application.Planning;
using Saldo360.Application.Calendar;

namespace Saldo360.Application;

public static class DependencyInjection
{
    /// <summary>Registra os serviços de aplicação (casos de uso).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<FamilyService>();
        services.AddScoped<FamilyInvitationService>();
        services.AddScoped<FinancialAccountService>();
        services.AddScoped<FinancialCategoryService>();
        services.AddScoped<FinancialTransactionService>();
        services.AddScoped<FinancialTransferService>();
        services.AddScoped<FinancialPlannedTransactionService>();
        services.AddScoped<FinancialCalendarService>();
        services.AddScoped<FinancialCalendarBalanceService>();
        services.AddScoped<FinancialCalendarMonthlyProjectionService>();
        return services;
    }
}

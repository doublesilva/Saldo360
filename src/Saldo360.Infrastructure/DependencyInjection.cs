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
using Saldo360.Application.Users;
using Saldo360.Infrastructure.Accounts;
using Saldo360.Infrastructure.Categories;
using Saldo360.Infrastructure.Transactions;
using Saldo360.Infrastructure.Transfers;
using Saldo360.Infrastructure.Authentication;
using Saldo360.Infrastructure.Families;
using Saldo360.Infrastructure.Planning;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;

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

        services.AddHealthChecks()
            .AddDbContextCheck<Saldo360DbContext>(DatabaseHealthCheckName);

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

        return services;
    }
}

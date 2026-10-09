using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Infrastructure.Authentication;
using Saldo360.Infrastructure.Persistence;

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

        services.AddDbContext<Saldo360DbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.AddHealthChecks()
            .AddDbContextCheck<Saldo360DbContext>(DatabaseHealthCheckName);

        services.AddDataProtection()
            .SetApplicationName("Saldo360")
            .PersistKeysToDbContext<Saldo360DbContext>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITicketStore, DatabaseTicketStore>();

        return services;
    }
}

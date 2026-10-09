using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddDbContext<Saldo360DbContext>(options => options.UseNpgsql(connectionString));

        services.AddHealthChecks()
            .AddDbContextCheck<Saldo360DbContext>(DatabaseHealthCheckName);

        return services;
    }
}

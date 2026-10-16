using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Health;

public sealed class DatabaseReadinessHealthCheck(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseReadinessHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<Saldo360DbContext>();
            if (await database.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Healthy();
            }

            LogUnavailable("ConnectionRejected");
            return HealthCheckResult.Unhealthy("Database dependency unavailable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogUnavailable(exception.GetType().FullName ?? exception.GetType().Name);
            return HealthCheckResult.Unhealthy("Database dependency unavailable.");
        }
    }

    private void LogUnavailable(string errorType) => logger.LogWarning(
        "Dependency health check failed. Dependency: {Dependency}; Result: {DependencyResult}; ErrorType: {ErrorType}",
        "PostgreSQL",
        "Unhealthy",
        errorType);
}

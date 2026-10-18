using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialEmailCollectorHostedService(
    IServiceScopeFactory scopes, FinancialEmailImportOptions options, FinancialEmailSchedule schedule,
    TimeProvider clock, ILogger<FinancialEmailCollectorHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = schedule.Next(clock.GetUtcNow());
            var delay = next - clock.GetUtcNow();
            if (delay <= TimeSpan.Zero) continue;
            try { await Task.Delay(delay, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            if (stoppingToken.IsCancellationRequested) break;
            foreach (var profile in options.Sources.Where(item => item.Enabled))
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<FinancialEmailCollectorRunner>().RunAsync(profile.Name, null, false, stoppingToken);
                    logger.LogInformation("Scheduled financial email collector profile {Profile} finished with {Status}.", profile.Name, result?.Status ?? "Disabled");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogWarning(exception, "Scheduled financial email collector profile {Profile} failed.", profile.Name); }
            }
        }
    }
}

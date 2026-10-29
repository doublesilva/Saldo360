using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Saldo360.Application.Pluggy;

namespace Saldo360.Infrastructure.Pluggy;

/// <summary>
/// Agendador da sincronização automática (T09.6): mesmo padrão do coletor de e-mail, sem infraestrutura nova de jobs. A cada passada procura as conexões vencidas,
/// dentro da janela configurada, e delega ao <see cref="PluggyAutoSyncRunner"/>. Só registra contagens: nunca dados bancários, nomes, valores nem identificadores do Item.
/// </summary>
internal sealed class PluggyAutoSyncHostedService(IServiceScopeFactory scopes, PluggyAutoSyncOptions options, TimeProvider clock, ILogger<PluggyAutoSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.StartupDelaySeconds)), clock, stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var summary = await scope.ServiceProvider.GetRequiredService<PluggyAutoSyncRunner>().RunDueAsync(stoppingToken);
                if (summary.Considered > 0)
                    logger.LogInformation("Pluggy auto-sync: {Considered} connection(s) due, {Synced} synced, {NothingNew} with nothing new, {Failed} failed, {Expired} with expired consent, {Busy} busy.",
                        summary.Considered, summary.Synced, summary.NothingNew, summary.Failed, summary.ConsentExpired, summary.Busy);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Pluggy auto-sync pass failed ({ErrorType}).", exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(options.PollIntervalMinutes, 1, 24 * 60)), clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

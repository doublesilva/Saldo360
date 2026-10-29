using Saldo360.Application.Workspaces;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

public sealed record PluggyAutoSyncSettingsView(bool Enabled, int IntervalHours, int WindowStartHour, int WindowEndHour, int MaxConnectionsPerRun, int PollIntervalMinutes);

/// <summary>
/// Estado de sincronização de uma conexão. <c>Status</c>: NeverSynced, Running, Completed, CompletedWithErrors, Failed, ConsentExpired ou SourceNotUpdated
/// (a instituição ainda não trouxe dados novos). Nada aqui carrega token, credencial, dado bancário ou mensagem da instituição.
/// </summary>
public sealed record PluggySyncConnectionView(
    Guid ConnectionId, string? Label, string? Institution, int IntegratedAccounts, string Status, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextAttemptAt, string? LastTrigger, int? LastDurationSeconds, int LastCreated, int LastUpdated, string? LastError, int ConsecutiveFailures,
    DateTimeOffset? ConsentExpiresAt, bool ConsentExpiringSoon, DateTimeOffset? SourceUpdatedAt, IReadOnlyList<PluggySyncRunView> RecentRuns);

public sealed record PluggySyncMonitorView(DateTimeOffset Now, PluggyAutoSyncSettingsView Auto, IReadOnlyList<PluggySyncConnectionView> Connections);

/// <summary>Leitura do monitoramento da sincronização (banco apenas: não consulta a Pluggy).</summary>
public sealed class PluggySyncMonitorService(IFinancialWorkspaceRepository workspaces, IPluggyItemLinkRepository links, IPluggySyncStateRepository states,
    IPluggyImportRepository imports, PluggyOptions options, PluggyAutoSyncOptions auto, TimeProvider time)
{
    public const int RecentRuns = 5;

    public async Task<PluggyResult<PluggySyncMonitorView>> GetAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        if (!options.Enabled || !options.ImportEnabled || await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return PluggyResult<PluggySyncMonitorView>.NotFound();
        var now = time.GetUtcNow();
        var connections = await links.ListAsync(workspaceId, ct);
        var stateByConnection = (await states.ListAsync(workspaceId, ct)).ToDictionary(s => s.ConnectionId);
        var accounts = await states.AccountsByConnectionAsync(workspaceId, ct);
        var views = new List<PluggySyncConnectionView>();
        foreach (var link in connections)
        {
            stateByConnection.TryGetValue(link.Id, out var state);
            var runs = (await imports.ListRunsAsync(workspaceId, link.Id, RecentRuns, ct)).Select(PluggyTransactionSyncService.ToView).ToList();
            var last = runs.FirstOrDefault();
            var running = last is { FinishedAt: null };
            var status = running ? "Running" : state is null || state.LastOutcome == PluggySyncOutcome.Never ? (last is null ? "NeverSynced" : last.Status) : state.LastOutcome.ToString();
            // Próxima execução: só existe com a automática ligada; fora da janela, vale a abertura da próxima janela.
            DateTimeOffset? next = !auto.Enabled ? null : PluggyAutoSyncPolicy.NextWindowOpen(state?.NextAttemptAt is { } due && due > now ? due : now, auto);
            var lastRun = last is { FinishedAt: not null } ? last : runs.FirstOrDefault(r => r.FinishedAt is not null);
            accounts.TryGetValue(link.Id, out var info);
            views.Add(new(link.Id, link.Label, info?.Institution, info?.Accounts ?? 0, status, state?.LastAttemptAt ?? last?.StartedAt, state?.LastSuccessAt, next, state?.LastTrigger?.ToString() ?? last?.Trigger,
                lastRun is { FinishedAt: { } finished } ? (int)Math.Max(0, (finished - lastRun.StartedAt).TotalSeconds) : null, lastRun?.Created ?? 0, lastRun?.Updated ?? 0,
                state?.LastError ?? lastRun?.ErrorSummary, state?.ConsecutiveFailures ?? 0, state?.ConsentExpiresAt,
                PluggyAutoSyncPolicy.ConsentExpiringSoon(state?.ConsentExpiresAt, now, auto), state?.SourceUpdatedAt, runs));
        }
        return PluggyResult<PluggySyncMonitorView>.Ok(new(now, new(auto.Enabled, auto.IntervalHours, auto.WindowStartHour, auto.WindowEndHour, auto.MaxConnectionsPerRun, auto.PollIntervalMinutes), views));
    }
}

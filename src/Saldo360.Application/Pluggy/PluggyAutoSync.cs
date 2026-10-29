using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

/// <summary>
/// Sincronização automática das conexões Pluggy (T09.6). Desligada por padrão. Reaproveita o mesmo fluxo da sincronização manual: nada de infraestrutura
/// nova de jobs. Meu Pluggy atualiza o Item no ritmo da instituição (em geral uma vez por dia): por isso o intervalo padrão é de 24 horas e uma conexão cujo Item
/// ainda não trouxe dados novos não é consultada de novo (apenas reverificada em intervalos curtos, sem gravar nada).
/// </summary>
public sealed class PluggyAutoSyncOptions
{
    public const string SectionName = "Pluggy:AutoSync";
    public bool Enabled { get; set; }
    /// <summary>Tempo entre sincronizações bem-sucedidas de uma conexão.</summary>
    public int IntervalHours { get; set; } = 24;
    /// <summary>Janela de execução em horário de Brasília: [início, fim). 0 a 24 = sem restrição; início maior que fim atravessa a meia-noite. A sincronização manual nunca depende da janela.</summary>
    public int WindowStartHour { get; set; }
    public int WindowEndHour { get; set; } = 24;
    /// <summary>De quanto em quanto tempo o agendador procura conexões vencidas.</summary>
    public int PollIntervalMinutes { get; set; } = 15;
    public int StartupDelaySeconds { get; set; } = 60;
    public int MaxConnectionsPerRun { get; set; } = 5;
    public int ConnectionDelaySeconds { get; set; } = 5;
    /// <summary>Falhas seguidas antes de desistir até o próximo intervalo regular. Cada nova tentativa espera o dobro da anterior.</summary>
    public int MaxConsecutiveAttempts { get; set; } = 3;
    public int RetryDelayMinutes { get; set; } = 30;
    public int MaxRetryDelayMinutes { get; set; } = 360;
    /// <summary>Reverificação, sem gravar nada, quando o Item ainda não tem dados novos.</summary>
    public int SourceRecheckMinutes { get; set; } = 120;
    /// <summary>Reverificação do consentimento vencido (o usuário pode tê-lo renovado).</summary>
    public int ConsentRecheckHours { get; set; } = 6;
    /// <summary>Antecedência do aviso de consentimento perto de vencer.</summary>
    public int ConsentWarningDays { get; set; } = 7;
    public int InProgressRetryMinutes { get; set; } = 5;
    /// <summary>Reserva da conexão enquanto uma instância a processa (protege contra duas instâncias).</summary>
    public int ClaimMinutes { get; set; } = 20;
}

/// <summary>Regras puras de quando sincronizar e quando tentar de novo.</summary>
public static class PluggyAutoSyncPolicy
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(Saldo360.Domain.Common.BusinessCalendar.TimeZoneId);

    public static bool IsInWindow(DateTimeOffset now, PluggyAutoSyncOptions options)
    {
        var start = Math.Clamp(options.WindowStartHour, 0, 24);
        var end = Math.Clamp(options.WindowEndHour, 0, 24);
        if (start == 0 && end == 24) return true;
        var hour = TimeZoneInfo.ConvertTime(now, Zone).Hour;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    /// <summary>Quando a janela abre: agora, se já está aberta; senão o próximo início.</summary>
    public static DateTimeOffset NextWindowOpen(DateTimeOffset now, PluggyAutoSyncOptions options)
    {
        if (IsInWindow(now, options)) return now;
        var local = TimeZoneInfo.ConvertTime(now, Zone);
        var candidate = new DateTime(local.Year, local.Month, local.Day, Math.Clamp(options.WindowStartHour, 0, 23), 0, 0);
        if (candidate <= local.DateTime) candidate = candidate.AddDays(1);
        return new DateTimeOffset(candidate, Zone.GetUtcOffset(candidate));
    }

    public static DateTimeOffset AfterSuccess(DateTimeOffset now, PluggyAutoSyncOptions options) => now.AddHours(Math.Max(1, options.IntervalHours));

    /// <summary>Após uma falha: espera crescente (dobra a cada tentativa, com teto); esgotadas as tentativas, só no próximo intervalo regular.</summary>
    public static DateTimeOffset AfterFailure(DateTimeOffset now, int consecutiveFailures, PluggyAutoSyncOptions options)
    {
        if (consecutiveFailures >= Math.Max(1, options.MaxConsecutiveAttempts)) return AfterSuccess(now, options);
        var minutes = Math.Max(1, options.RetryDelayMinutes) * Math.Pow(2, Math.Max(0, consecutiveFailures - 1));
        return now.AddMinutes(Math.Min(minutes, Math.Max(1, options.MaxRetryDelayMinutes)));
    }

    public static DateTimeOffset AfterSourceNotUpdated(DateTimeOffset now, PluggyAutoSyncOptions options) => now.AddMinutes(Math.Max(1, options.SourceRecheckMinutes));

    public static DateTimeOffset AfterConsentExpired(DateTimeOffset now, PluggyAutoSyncOptions options) => now.AddHours(Math.Max(1, options.ConsentRecheckHours));

    public static DateTimeOffset AfterBusy(DateTimeOffset now, PluggyAutoSyncOptions options) => now.AddMinutes(Math.Max(1, options.InProgressRetryMinutes));

    public static bool ConsentExpiringSoon(DateTimeOffset? consentExpiresAt, DateTimeOffset now, PluggyAutoSyncOptions options) =>
        consentExpiresAt is { } expires && expires > now && expires <= now.AddDays(Math.Max(1, options.ConsentWarningDays));

    /// <summary>
    /// A origem tem novidade? Só é considerada sem novidade quando a última sincronização foi completa e o <c>lastUpdatedAt</c> do Item não avançou.
    /// Sem informação da origem, sincroniza (na dúvida, não perde dado).
    /// </summary>
    public static bool SourceHasNothingNew(PluggySyncState? state, DateTimeOffset? itemUpdatedAt) =>
        state is { LastOutcome: PluggySyncOutcome.Completed, SourceUpdatedAt: { } known } && itemUpdatedAt is { } current && current <= known;
}

public sealed record PluggySyncCandidate(PluggyItemLink Link, PluggySyncState? State);

/// <summary>Estado de monitoramento e agendamento por conexão. Somente a infraestrutura conhece o armazenamento.</summary>
public interface IPluggySyncStateRepository
{
    Task<PluggySyncState?> FindAsync(Guid workspaceId, Guid connectionId, CancellationToken ct);
    Task<IReadOnlyList<PluggySyncState>> ListAsync(Guid workspaceId, CancellationToken ct);
    /// <summary>Conexões com conta ou cartão integrado cuja próxima tentativa venceu e que não estão reservadas, da mais atrasada à mais recente.</summary>
    Task<IReadOnlyList<PluggySyncCandidate>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct);
    /// <summary>Reserva a conexão de forma atômica; falso se outra instância já a reservou ou se ela deixou de estar vencida.</summary>
    Task<bool> TryClaimAsync(Guid workspaceId, Guid connectionId, DateTimeOffset now, DateTimeOffset until, CancellationToken ct);
    /// <summary>Cria o estado, se não existir, e aplica a mudança.</summary>
    Task SaveAsync(Guid workspaceId, Guid connectionId, Action<PluggySyncState> change, DateTimeOffset now, CancellationToken ct);
    /// <summary>Encerra como falha as execuções "em andamento" mais antigas que o limite (processo que morreu no meio).</summary>
    Task<int> RecoverAbandonedAsync(DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, PluggyConnectionAccounts>> AccountsByConnectionAsync(Guid workspaceId, CancellationToken ct);
}

public sealed record PluggyConnectionAccounts(int Accounts, string? Institution);

public sealed record PluggyAutoSyncSummary(bool Enabled, bool InWindow, int Considered, int Synced, int NothingNew, int Failed, int ConsentExpired, int Busy);

/// <summary>
/// Uma passada do agendador: encontra as conexões vencidas e sincroniza cada uma, isoladamente (a falha de uma não impede as outras). Usa o mesmo caminho da
/// sincronização manual, então a unicidade de execução em andamento vale para os dois. Nunca chama a OpenAI: a categorização por IA continua dependendo da
/// confirmação explícita do usuário.
/// </summary>
public sealed class PluggyAutoSyncRunner(PluggyOptions pluggy, PluggyAutoSyncOptions options, IPluggySyncStateRepository states,
    PluggyTransactionSyncService sync, IPluggyClient client, IPluggyFinancialRepository financial, TimeProvider time)
{
    public async Task<PluggyAutoSyncSummary> RunDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!options.Enabled || !pluggy.Enabled || !pluggy.ImportEnabled || !pluggy.IsConfigured) return new(false, false, 0, 0, 0, 0, 0, 0);
        // A recuperação não depende da janela: execução abandonada não pode ficar "em andamento" para sempre.
        await states.RecoverAbandonedAsync(now, TimeSpan.FromMinutes(Math.Max(1, pluggy.SyncStaleMinutes)), ct);
        if (!PluggyAutoSyncPolicy.IsInWindow(now, options)) return new(true, false, 0, 0, 0, 0, 0, 0);

        int synced = 0, nothingNew = 0, failed = 0, expired = 0, busy = 0;
        var due = await states.ListDueAsync(now, Math.Clamp(options.MaxConnectionsPerRun, 1, 50), ct);
        foreach (var candidate in due)
        {
            if (ct.IsCancellationRequested) break;
            var link = candidate.Link;
            if (!await states.TryClaimAsync(link.WorkspaceId, link.Id, time.GetUtcNow(), time.GetUtcNow().AddMinutes(Math.Max(1, options.ClaimMinutes)), ct)) continue;
            try
            {
                switch (await ProcessAsync(link, candidate.State, ct))
                {
                    case PluggySyncOutcome.Completed or PluggySyncOutcome.CompletedWithErrors: synced++; break;
                    case PluggySyncOutcome.SourceNotUpdated: nothingNew++; break;
                    case PluggySyncOutcome.ConsentExpired: expired++; break;
                    case null: busy++; break;
                    default: failed++; break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Falha inesperada numa conexão não derruba as outras: fica registrada (sem detalhe) e a conexão volta a ser tentada com espera.
                failed++;
                var at = time.GetUtcNow();
                await states.SaveAsync(link.WorkspaceId, link.Id, s => s.RecordAttempt(at, PluggySyncOutcome.Failed, PluggySyncTrigger.Scheduled, null, "UnexpectedError", true,
                    PluggyAutoSyncPolicy.AfterFailure(at, s.ConsecutiveFailures + 1, options), null), at, CancellationToken.None);
            }
            if (options.ConnectionDelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(options.ConnectionDelaySeconds), time, ct);
        }
        return new(true, true, due.Count, synced, nothingNew, failed, expired, busy);
    }

    /// <returns>O resultado da tentativa; nulo quando a conexão estava ocupada com outra sincronização.</returns>
    private async Task<PluggySyncOutcome?> ProcessAsync(Domain.Pluggy.PluggyItemLink link, PluggySyncState? state, CancellationToken ct)
    {
        PluggyItem? item = null;
        try { item = await client.GetItemAsync(link.PluggyItemId, ct); }
        catch (PluggyException) { /* a sincronização abaixo registra a falha na própria execução */ }

        if (item is not null)
        {
            var at = time.GetUtcNow();
            await states.SaveAsync(link.WorkspaceId, link.Id, s => s.ObserveItem(item.Status, item.ConsentExpiresAt, at), at, ct);
            var consentExpired = PluggyPocService.StateOf(item, at) == PluggyConnectionState.ConsentExpired;
            // Consentimento que já constava vencido: reverifica sem criar execução nem consultar dados; o usuário precisa renovar.
            if (consentExpired && state is { LastOutcome: PluggySyncOutcome.ConsentExpired })
            {
                await financial.SetConnectionStatusAsync(link.WorkspaceId, link.Id, Domain.Pluggy.PluggyAccountLinkStatus.ConsentExpired, ct);
                await states.SaveAsync(link.WorkspaceId, link.Id, s => s.RecordAttempt(at, PluggySyncOutcome.ConsentExpired, PluggySyncTrigger.Scheduled, null, "ConsentExpired", false,
                    PluggyAutoSyncPolicy.AfterConsentExpired(at, options), null), at, ct);
                return PluggySyncOutcome.ConsentExpired;
            }
            // O Item ainda está sendo atualizado, ou nada novo desde a última sincronização completa: não vale consultar de novo.
            if (!consentExpired && (string.Equals(item.ExecutionStatus, "CREATING", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Status, "UPDATING", StringComparison.OrdinalIgnoreCase)
                || PluggyAutoSyncPolicy.SourceHasNothingNew(state, item.LastUpdatedAt)))
            {
                await states.SaveAsync(link.WorkspaceId, link.Id, s => s.RecordAttempt(at, PluggySyncOutcome.SourceNotUpdated, PluggySyncTrigger.Scheduled, null, null, false,
                    PluggyAutoSyncPolicy.AfterSourceNotUpdated(at, options), null), at, ct);
                return PluggySyncOutcome.SourceNotUpdated;
            }
        }

        var result = await sync.RunConnectionAsync(link, link.UserId, PluggySyncTrigger.Scheduled, item, ct);
        if (result.Access == PluggyAccess.Conflict)
        {
            var at = time.GetUtcNow();
            await states.SaveAsync(link.WorkspaceId, link.Id, s => s.Reschedule(PluggyAutoSyncPolicy.AfterBusy(at, options), at), at, ct);
            return null;
        }
        return await OutcomeOfAsync(link, ct);
    }

    private async Task<PluggySyncOutcome> OutcomeOfAsync(Domain.Pluggy.PluggyItemLink link, CancellationToken ct) =>
        (await states.FindAsync(link.WorkspaceId, link.Id, ct))?.LastOutcome ?? PluggySyncOutcome.Failed;
}

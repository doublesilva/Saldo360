using Saldo360.Application.Classification;
using Saldo360.Application.Ai;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

// R04 — Central de Automação. Nada aqui cria motor, agente ou agendador novos: a central lê e comanda o que já existe (a sincronização manual/agendada, o
// agendador de segundo plano e o classificador com o agente de IA), guarda a preferência do usuário e registra o histórico das execuções.

public sealed record PluggyAiView(bool Enabled, bool Available, string? Model, string? Reason);

/// <summary>O que o ambiente (servidor) permite. O usuário não liga nem desliga o agendador do servidor: só pausa o do seu orçamento.</summary>
public sealed record PluggyServerView(bool IntegrationAvailable, bool ImportEnabled, bool SchedulerEnabled, int DefaultIntervalHours, int WindowStartHour, int WindowEndHour,
    string TimeZone, IReadOnlyList<int> IntervalOptions, PluggyAiView Ai);

public sealed record PluggyAutomationSettingsDto(bool SyncPaused, int SyncIntervalHours, bool SyncIntervalIsDefault, bool CategorizeAfterSync, DateTimeOffset? CategorizeConsentAt);

public sealed record AutomationSyncCounts(int AccountsTotal, int AccountsSynced, int Seen, int Created, int Updated, int Unchanged, int Missing, int Errors, string? ErrorSummary);

public sealed record AutomationCategorizationCounts(int Considered, int ClassifiedLocal, int ClassifiedAi, int Pending, int SkippedChanged, int AiItemsSent, int AiCalls, bool UsedAi, string? Model, string? AiStoppedBecause);

/// <summary>
/// Uma execução de uma automação. <c>Kind</c>: Sync ou Categorization. <c>Trigger</c>: Manual, Scheduled (só sincronização) ou AfterSync (só categorização).
/// <c>State</c>: Running, Completed, CompletedWithPending ou Failed. As contagens são por operação e nunca se somam entre etapas. Só códigos curtos de falha.
/// </summary>
public sealed record AutomationRunView(Guid Id, string Kind, string Trigger, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, int? DurationSeconds,
    Guid? ConnectionId, string? Scope, Guid? RelatedRunId, string? FailureCode, bool Interrupted, AutomationSyncCounts? Sync, AutomationCategorizationCounts? Categorization);

public sealed record PluggySyncAutomationView(string State, int Connections, int ConnectionsNeedingAttention, bool Running, DateTimeOffset? NextRunAt, AutomationRunView? LastRun);

public sealed record PluggyCategorizationAutomationView(string State, int Eligible, int NeedsReview, int NotEvaluated, bool Running, AutomationRunView? LastRun);

public sealed record PluggyAutomationOverview(DateTimeOffset Now, PluggyServerView Server, PluggyAutomationSettingsDto Settings, PluggySyncAutomationView Sync, PluggyCategorizationAutomationView Categorization);

public sealed record PluggyAutomationUpdate(bool? SyncPaused, int? SyncIntervalHours, bool? CategorizeAfterSync, bool? ConfirmExternal);

public sealed record AutomationRunFilter(string? Kind, string? State, DateOnly? From, DateOnly? To, int Page, int PageSize);

public sealed record AutomationRunPage(IReadOnlyList<AutomationRunView> Items, int Total, int Page, int PageSize);

/// <summary>Linha crua do histórico (sincronização ou categorização), como o armazenamento a conhece.</summary>
public sealed record AutomationRunRow(Guid Id, string Kind, string Trigger, string RawState, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, Guid? ConnectionId, Guid? RelatedRunId,
    string? Code, AutomationSyncCounts? Sync, AutomationCategorizationCounts? Categorization);

public interface IPluggyAutomationRepository
{
    Task<PluggyAutomationSettings?> FindSettingsAsync(Guid workspaceId, CancellationToken ct);
    /// <summary>Cria a linha de preferências, se não existir, e aplica a mudança.</summary>
    Task<PluggyAutomationSettings> SaveSettingsAsync(Guid workspaceId, Guid userId, Action<PluggyAutomationSettings> change, DateTimeOffset now, CancellationToken ct);
    /// <summary>Inicia uma execução de categorização; nulo se já existe uma em andamento (a abandonada, mais antiga que o limite, é encerrada como falha antes).</summary>
    Task<PluggyCategorizationRun?> TryStartCategorizationAsync(Guid workspaceId, Guid userId, PluggyCategorizationTrigger trigger, Guid? syncRunId, Guid? connectionId, bool usedAi,
        DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct);
    Task SaveCategorizationAsync(PluggyCategorizationRun run, CancellationToken ct);
    /// <summary>Encerra como falha as categorizações "em andamento" mais antigas que o limite (processo que morreu no meio).</summary>
    Task<int> RecoverAbandonedCategorizationsAsync(DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct);
    /// <summary>Histórico paginado (mais recente primeiro). <paramref name="staleBefore"/>: execuções "em andamento" iniciadas antes disso constam como interrompidas (falha). Somente leitura.</summary>
    Task<(IReadOnlyList<AutomationRunRow> Items, int Total)> ListRunsAsync(Guid workspaceId, AutomationRunFilter filter, DateTimeOffset staleBefore, CancellationToken ct);
    Task<AutomationRunRow?> FindRunAsync(Guid workspaceId, string kind, Guid id, CancellationToken ct);
    Task<AutomationRunRow?> LastRunAsync(Guid workspaceId, string kind, CancellationToken ct);
    Task<bool> AnyRunningAsync(Guid workspaceId, string kind, DateTimeOffset staleBefore, CancellationToken ct);
}

public enum PluggyAutomationAccess { Allowed, NotFound, Invalid }

public sealed record PluggyAutomationResult<T>(PluggyAutomationAccess Access, T? Value, IDictionary<string, string[]>? Errors = null)
{
    public static PluggyAutomationResult<T> Ok(T value) => new(PluggyAutomationAccess.Allowed, value);
    public static PluggyAutomationResult<T> NotFound { get; } = new(PluggyAutomationAccess.NotFound, default);
    public static PluggyAutomationResult<T> Invalid(string field, string message) => new(PluggyAutomationAccess.Invalid, default, new Dictionary<string, string[]> { [field] = [message] });
}

public static class AutomationKinds
{
    public const string Sync = "Sync";
    public const string Categorization = "Categorization";
    public static readonly string[] States = ["Running", "Completed", "CompletedWithPending", "Failed"];

    public static string StateOfSync(PluggySyncStatus status) => status switch
    {
        PluggySyncStatus.Running => "Running",
        PluggySyncStatus.Completed => "Completed",
        PluggySyncStatus.CompletedWithErrors => "CompletedWithPending",
        _ => "Failed",
    };
}

/// <summary>
/// Leitura e comandos da central de automação. A leitura nunca dispara execução e não consulta a Pluggy nem a IA: só o banco e a configuração. Os comandos
/// (sincronizar agora, categorizar) são os de sempre, expostos pelas rotas existentes; aqui ficam as preferências e a composição do estado.
/// </summary>
public sealed class PluggyAutomationService(IFinancialWorkspaceRepository workspaces, IPluggyAutomationRepository automation, IPluggyItemLinkRepository links, IPluggySyncStateRepository states,
    IPluggyCategorizationRepository categorizationRepository, IFinancialClassificationModel model, FinancialClassificationOptions classification, AiAgentOptions agent,
    PluggyOptions pluggy, PluggyAutoSyncOptions auto, TimeProvider time)
{
    public const int MaxPageSize = 100;

    private bool IntegrationAvailable => pluggy.Enabled && pluggy.ImportEnabled && pluggy.IsConfigured;

    private async Task<bool> CanAccessAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        pluggy.Enabled && pluggy.ImportEnabled && await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is not null;

    public PluggyAiView AiView() => new(agent.Enabled && classification.AiEnabled, model.IsAvailable, model.IsAvailable ? model.ModelName : null,
        model.IsAvailable ? null : !agent.Enabled ? "AiNotConfigured" : !classification.AiEnabled ? "AiDisabled" : "AiNotConfigured");

    public async Task<PluggyAutomationResult<PluggyAutomationOverview>> GetOverviewAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyAutomationResult<PluggyAutomationOverview>.NotFound;
        var now = time.GetUtcNow();
        var stale = TimeSpan.FromMinutes(Math.Max(1, pluggy.SyncStaleMinutes));
        var settings = await automation.FindSettingsAsync(workspaceId, ct);
        var ai = AiView();
        var server = new PluggyServerView(IntegrationAvailable, pluggy.ImportEnabled, auto.Enabled && IntegrationAvailable, auto.IntervalHours, auto.WindowStartHour, auto.WindowEndHour,
            Saldo360.Domain.Common.BusinessCalendar.TimeZoneId, PluggyAutomationSettings.IntervalOptionsHours, ai);
        var dto = new PluggyAutomationSettingsDto(settings?.SyncPaused ?? false, settings?.SyncIntervalHours ?? Math.Max(1, auto.IntervalHours), settings?.SyncIntervalHours is null,
            settings?.CategorizeAfterSync ?? false, settings?.CategorizeConsentAt);

        // Sincronização: o estado vem das conexões e das execuções registradas; "próxima" só existe quando o agendador real a calcula (ligado, não pausado).
        var connections = await links.ListAsync(workspaceId, ct);
        var accounts = await states.AccountsByConnectionAsync(workspaceId, ct);
        var stateRows = await states.ListAsync(workspaceId, ct);
        var integrated = connections.Where(c => accounts.TryGetValue(c.Id, out var info) && info.Accounts > 0).ToList();
        var attention = stateRows.Count(s => integrated.Any(c => c.Id == s.ConnectionId) && s.LastOutcome is PluggySyncOutcome.Failed or PluggySyncOutcome.CompletedWithErrors or PluggySyncOutcome.ConsentExpired);
        var syncRunning = await automation.AnyRunningAsync(workspaceId, AutomationKinds.Sync, now - stale, ct);
        var schedulerOn = server.SchedulerEnabled && !dto.SyncPaused;
        DateTimeOffset? next = null;
        if (schedulerOn && integrated.Count > 0)
        {
            var due = integrated.Select(c => stateRows.FirstOrDefault(s => s.ConnectionId == c.Id)?.NextAttemptAt).Select(d => d is { } at && at > now ? at : now).Min();
            next = PluggyAutoSyncPolicy.NextWindowOpen(due, auto);
        }
        var lastSync = await automation.LastRunAsync(workspaceId, AutomationKinds.Sync, ct);
        var syncState = !IntegrationAvailable ? "Unavailable" : integrated.Count == 0 ? "NoConnections" : syncRunning ? "Running" : !server.SchedulerEnabled ? "SchedulerOff" : dto.SyncPaused ? "Paused" : "Scheduled";
        var syncView = new PluggySyncAutomationView(syncState, integrated.Count, attention, syncRunning, next, lastSync is null ? null : await ToViewAsync(workspaceId, lastSync, now, stale, ct));

        // Categorização: elegíveis = os que a execução comum processaria (sem escolha manual e sem categoria automática). Quantidade do orçamento inteiro.
        var candidates = await categorizationRepository.ListCandidatesAsync(workspaceId, ct);
        var eligible = candidates.Count(c => !c.IsManual && !c.HasAutomaticCategory);
        var counts = await categorizationRepository.CountAsync(workspaceId, ct);
        var catRunning = await automation.AnyRunningAsync(workspaceId, AutomationKinds.Categorization, now - stale, ct);
        var lastCat = await automation.LastRunAsync(workspaceId, AutomationKinds.Categorization, ct);
        var catState = catRunning ? "Running" : !ai.Available ? "AiUnavailable" : eligible == 0 ? "NothingToDo" : "Ready";
        var catView = new PluggyCategorizationAutomationView(catState, eligible, counts.NeedsReview, counts.NotEvaluated, catRunning, lastCat is null ? null : await ToViewAsync(workspaceId, lastCat, now, stale, ct));
        return PluggyAutomationResult<PluggyAutomationOverview>.Ok(new(now, server, dto, syncView, catView));
    }

    public async Task<PluggyAutomationResult<PluggyAutomationSettingsDto>> UpdateAsync(Guid workspaceId, Guid userId, PluggyAutomationUpdate update, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyAutomationResult<PluggyAutomationSettingsDto>.NotFound;
        if (update.SyncPaused is null && update.SyncIntervalHours is null && update.CategorizeAfterSync is null)
            return PluggyAutomationResult<PluggyAutomationSettingsDto>.Invalid("settings", "Informe ao menos uma preferência.");
        if (update.SyncIntervalHours is { } hours && !PluggyAutomationSettings.IntervalOptionsHours.Contains(hours))
            return PluggyAutomationResult<PluggyAutomationSettingsDto>.Invalid("syncIntervalHours", $"Escolha um intervalo de {string.Join(", ", PluggyAutomationSettings.IntervalOptionsHours)} horas.");
        var current = await automation.FindSettingsAsync(workspaceId, ct);
        // O envio de descrições saneadas ao provedor de IA a cada sincronização só é ligado com o consentimento explícito (que fica registrado).
        if (update.CategorizeAfterSync == true && update.ConfirmExternal != true && current?.CategorizeConsentAt is null)
            return PluggyAutomationResult<PluggyAutomationSettingsDto>.Invalid("confirmExternal", "Confirme explicitamente o envio de descrições saneadas ao provedor de IA para categorizar após cada sincronização.");

        var now = time.GetUtcNow();
        var saved = await automation.SaveSettingsAsync(workspaceId, userId, s =>
        {
            if (update.SyncPaused is { } paused) s.SetSyncPaused(paused, userId, now);
            if (update.SyncIntervalHours is { } value) s.SetSyncInterval(value, userId, now);
            if (update.CategorizeAfterSync is { } after) s.SetCategorizeAfterSync(after, update.ConfirmExternal == true, userId, now);
        }, now, ct);
        return PluggyAutomationResult<PluggyAutomationSettingsDto>.Ok(new(saved.SyncPaused, saved.SyncIntervalHours ?? Math.Max(1, auto.IntervalHours), saved.SyncIntervalHours is null,
            saved.CategorizeAfterSync, saved.CategorizeConsentAt));
    }

    public async Task<PluggyAutomationResult<AutomationRunPage>> ListRunsAsync(Guid workspaceId, Guid userId, AutomationRunFilter filter, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyAutomationResult<AutomationRunPage>.NotFound;
        if (filter.Kind is not null && filter.Kind is not (AutomationKinds.Sync or AutomationKinds.Categorization)) return PluggyAutomationResult<AutomationRunPage>.Invalid("kind", "Automação inválida. Use Sync ou Categorization.");
        if (filter.State is not null && !AutomationKinds.States.Contains(filter.State)) return PluggyAutomationResult<AutomationRunPage>.Invalid("state", "Resultado inválido.");
        if (filter.From is { } from && filter.To is { } to && from > to) return PluggyAutomationResult<AutomationRunPage>.Invalid("from", "O início do período não pode ser posterior ao fim.");
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, MaxPageSize);
        var now = time.GetUtcNow();
        var stale = TimeSpan.FromMinutes(Math.Max(1, pluggy.SyncStaleMinutes));
        var (rows, total) = await automation.ListRunsAsync(workspaceId, filter with { Page = page, PageSize = size }, now - stale, ct);
        var labels = await ScopeLabelsAsync(workspaceId, ct);
        return PluggyAutomationResult<AutomationRunPage>.Ok(new(rows.Select(r => ToView(r, now, stale, labels)).ToList(), total, page, size));
    }

    public async Task<PluggyAutomationResult<AutomationRunView>> GetRunAsync(Guid workspaceId, Guid userId, string kind, Guid id, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct) || kind is not (AutomationKinds.Sync or AutomationKinds.Categorization)) return PluggyAutomationResult<AutomationRunView>.NotFound;
        if (await automation.FindRunAsync(workspaceId, kind, id, ct) is not { } row) return PluggyAutomationResult<AutomationRunView>.NotFound;
        return PluggyAutomationResult<AutomationRunView>.Ok(await ToViewAsync(workspaceId, row, time.GetUtcNow(), TimeSpan.FromMinutes(Math.Max(1, pluggy.SyncStaleMinutes)), ct));
    }

    private async Task<AutomationRunView> ToViewAsync(Guid workspaceId, AutomationRunRow row, DateTimeOffset now, TimeSpan stale, CancellationToken ct) =>
        ToView(row, now, stale, row.ConnectionId is null ? new Dictionary<Guid, string>() : await ScopeLabelsAsync(workspaceId, ct));

    private async Task<IReadOnlyDictionary<Guid, string>> ScopeLabelsAsync(Guid workspaceId, CancellationToken ct)
    {
        var connections = await links.ListAsync(workspaceId, ct);
        var accounts = await states.AccountsByConnectionAsync(workspaceId, ct);
        return connections.ToDictionary(c => c.Id, c => c.Label ?? (accounts.TryGetValue(c.Id, out var info) ? info.Institution : null) ?? "Conexão");
    }

    /// <summary>Uma execução "em andamento" mais antiga que o limite consta como falha interrompida: nunca fica "em andamento" para sempre.</summary>
    public static AutomationRunView ToView(AutomationRunRow row, DateTimeOffset now, TimeSpan stale, IReadOnlyDictionary<Guid, string> labels)
    {
        var interrupted = row.RawState == "Running" && row.StartedAt < now - stale;
        var state = interrupted ? "Failed" : row.RawState;
        var finished = interrupted ? null : row.FinishedAt;
        var duration = finished is { } end ? (int?)Math.Max(0, (int)(end - row.StartedAt).TotalSeconds) : null;
        var scope = row.ConnectionId is { } id && labels.TryGetValue(id, out var label) ? label : row.Kind == AutomationKinds.Categorization && row.ConnectionId is null ? "Todo o orçamento" : null;
        return new(row.Id, row.Kind, row.Trigger, state, row.StartedAt, finished, duration, row.ConnectionId, scope, row.RelatedRunId, interrupted ? "Interrupted" : row.Code, interrupted, row.Sync, row.Categorization);
    }
}

/// <summary>Resultado de uma tentativa de categorização com histórico.</summary>
public enum PluggyCategorizationJobOutcome { Ok, ConfirmationRequired, AiUnavailable, Busy, Failed }

public sealed record PluggyCategorizationJobResult(PluggyCategorizationJobOutcome Outcome, PluggyCategorizationResult? Result, Guid? RunId, string? FailureCode);

/// <summary>
/// Executa a categorização do jeito de sempre (<see cref="IPluggyCategorizationService"/>: manual → regras → categoria da Pluggy → IA) registrando a execução no
/// histórico, com uma só em andamento por orçamento. É o mesmo caminho do botão manual e da categorização após a sincronização. Falha aqui nunca desfaz uma
/// sincronização concluída nem aparece como falha da conexão.
/// </summary>
public sealed class PluggyCategorizationJob(IPluggyAutomationRepository automation, IPluggyCategorizationService categorization, PluggyOptions pluggy, TimeProvider time)
{
    public async Task<PluggyCategorizationJobResult> RunAsync(Guid workspaceId, Guid userId, PluggyCategorizationTrigger trigger, PluggyCategorizationRequest request, Guid? syncRunId, Guid? connectionId, CancellationToken ct)
    {
        if (request.UseAi && !request.ConfirmExternal) return new(PluggyCategorizationJobOutcome.ConfirmationRequired, null, null, null);
        var started = time.GetUtcNow();
        var run = await automation.TryStartCategorizationAsync(workspaceId, userId, trigger, syncRunId, connectionId, request.UseAi, started, TimeSpan.FromMinutes(Math.Max(1, pluggy.SyncStaleMinutes)), ct);
        if (run is null) return new(PluggyCategorizationJobOutcome.Busy, null, null, null);
        try
        {
            var outcome = await categorization.RunAsync(workspaceId, userId, request, started, ct);
            if (outcome.Outcome != PluggyCategorizationOutcome.Ok || outcome.Result is not { } result)
            {
                run.Fail(time.GetUtcNow(), outcome.Outcome == PluggyCategorizationOutcome.AiUnavailable ? "AiUnavailable" : "NotCompleted");
                await automation.SaveCategorizationAsync(run, CancellationToken.None);
                return new(PluggyCategorizationJobOutcome.Failed, null, run.Id, run.FailureCode);
            }
            run.Complete(time.GetUtcNow(), result.Considered, result.Pluggy + result.Rule, result.Ai, result.Unresolved, result.SkippedChanged, result.AiItemsSent, result.AiCalls,
                result.AiStoppedBecause, result.AiFailure, result.Model);
            await automation.SaveCategorizationAsync(run, CancellationToken.None);
            return new(PluggyCategorizationJobOutcome.Ok, result, run.Id, null);
        }
        catch (OperationCanceledException)
        {
            run.Fail(time.GetUtcNow(), "Cancelled");
            await automation.SaveCategorizationAsync(run, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            run.Fail(time.GetUtcNow(), "UnexpectedError");
            await automation.SaveCategorizationAsync(run, CancellationToken.None);
            return new(PluggyCategorizationJobOutcome.Failed, null, run.Id, run.FailureCode);
        }
    }
}

namespace Saldo360.Application.Pluggy;

// Saúde da automação bancária: UMA regra, usada pelo monitor de sincronização (Central de Automação) e pelo Plano × Realizado (Visão Geral).
// Só lê o que já está no banco (estado e histórico da conexão); nunca consulta a Pluggy nem dispara sincronização.

public static class SyncHealthStates
{
    public const string UpToDate = "UpToDate";
    public const string Delayed = "Delayed";
    public const string Blocked = "Blocked";
    public const string NeedsAttention = "NeedsAttention";
    public const string AwaitingFirstImport = "AwaitingFirstImport";
    public const string AutomationDisabled = "AutomationDisabled";

    /// <summary>
    /// Precedência entre conexões (da mais grave à menos): bloqueada (só o usuário resolve) → requer atenção (falha da última execução) → atrasada (prazo vencido com cadência)
    /// → aguardando primeira importação → automação desativada → em dia. O agregado nunca é "em dia" se uma conexão do recorte não estiver.
    /// </summary>
    public static readonly string[] Precedence = [Blocked, NeedsAttention, Delayed, AwaitingFirstImport, AutomationDisabled, UpToDate];
}

/// <summary>Motivos conhecidos (códigos curtos; o texto é da tela): ConsentExpired, RunFailed, RunCompletedWithErrors, ImportOverdue, NoImportYet, AutomationPaused, AutomationOff.</summary>
public sealed record SyncHealthDto(string State, string? Reason, DateTimeOffset? LastSuccessAt, DateTimeOffset? OverdueSince);

/// <summary>Cadência de importação: <c>Active</c> só com o agendador do servidor ligado e a automação do orçamento não pausada.</summary>
public sealed record SyncCadence(bool Active, bool Paused, int IntervalHours);

/// <summary>Resumo de várias conexões: pior estado, quantas estão nele, quantas estão em dia e quais pedem atenção.</summary>
public sealed record SyncHealthSummaryDto(string State, int Connections, int InState, int UpToDate, IReadOnlyList<Guid> AffectedConnectionIds);

public static class SyncHealth
{
    /// <summary>Prazo de uma importação: uma vez e meia o intervalo configurado.</summary>
    public static TimeSpan OverdueAfter(int intervalHours) => TimeSpan.FromHours(Math.Max(1, intervalHours) * 1.5);

    /// <summary>
    /// Saúde de uma conexão. "Atrasada" exige cadência ativa E prazo vencido desde a última verificação bem-sucedida: a importação completa, ou, quando a instituição só não trouxe
    /// novidade (<c>SourceNotUpdated</c>), a última tentativa — a conexão foi consultada no prazo e não há o que importar.
    /// </summary>
    public static SyncHealthDto Evaluate(string status, DateTimeOffset? lastSuccessAt, DateTimeOffset? lastAttemptAt, SyncCadence cadence, DateTimeOffset now)
    {
        if (status == "ConsentExpired") return new(SyncHealthStates.Blocked, "ConsentExpired", lastSuccessAt, null);
        if (status == "Failed") return new(SyncHealthStates.NeedsAttention, "RunFailed", lastSuccessAt, null);
        if (status == "CompletedWithErrors") return new(SyncHealthStates.NeedsAttention, "RunCompletedWithErrors", lastSuccessAt, null);
        if (lastSuccessAt is null) return new(SyncHealthStates.AwaitingFirstImport, "NoImportYet", null, null);
        if (!cadence.Active) return new(SyncHealthStates.AutomationDisabled, cadence.Paused ? "AutomationPaused" : "AutomationOff", lastSuccessAt, null);
        var checkedAt = status == "SourceNotUpdated" && lastAttemptAt is { } attempt && attempt > lastSuccessAt ? attempt : lastSuccessAt.Value;
        var due = checkedAt + OverdueAfter(cadence.IntervalHours);
        return now > due ? new(SyncHealthStates.Delayed, "ImportOverdue", lastSuccessAt, due) : new(SyncHealthStates.UpToDate, null, lastSuccessAt, null);
    }

    public static SyncHealthSummaryDto? Summarize(IReadOnlyList<(Guid ConnectionId, SyncHealthDto Health)> items)
    {
        if (items.Count == 0) return null;
        var worst = SyncHealthStates.Precedence.First(state => items.Any(i => i.Health.State == state));
        var affected = items.Where(i => i.Health.State == worst && worst != SyncHealthStates.UpToDate).Select(i => i.ConnectionId).ToList();
        return new(worst, items.Count, items.Count(i => i.Health.State == worst), items.Count(i => i.Health.State == SyncHealthStates.UpToDate), affected);
    }
}

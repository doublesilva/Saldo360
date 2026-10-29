using Saldo360.Domain.Common;

namespace Saldo360.Domain.Pluggy;

/// <summary>Quem disparou a execução: o usuário (botão "Sincronizar agora") ou o agendador de segundo plano (T09.6).</summary>
public enum PluggySyncTrigger { Manual, Scheduled }

/// <summary>
/// Resultado da última tentativa de uma conexão. <c>SourceNotUpdated</c> = a instituição ainda não trouxe dados novos desde a última sincronização
/// completa (nada foi consultado nem gravado); <c>ConsentExpired</c> = o consentimento venceu e só o usuário renova.
/// </summary>
public enum PluggySyncOutcome { Never, Completed, CompletedWithErrors, Failed, ConsentExpired, SourceNotUpdated }

/// <summary>
/// Estado de monitoramento e de agendamento de uma conexão Pluggy (uma linha por conexão): última tentativa e última sincronização bem-sucedida, resultado,
/// falhas seguidas, próxima tentativa e o que se sabe do consentimento. Não guarda dado bancário nem mensagem da instituição: só códigos curtos.
/// <see cref="ClaimedUntil"/> é a reserva temporária que impede dois processos de tratarem a mesma conexão ao mesmo tempo.
/// </summary>
public sealed class PluggySyncState : Entity
{
    public const int ErrorMaxLength = 300;
    public const int StatusMaxLength = 24;
    private PluggySyncState() { }

    public Guid WorkspaceId { get; private init; }
    public Guid ConnectionId { get; private init; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public PluggySyncOutcome LastOutcome { get; private set; }
    public PluggySyncTrigger? LastTrigger { get; private set; }
    public Guid? LastRunId { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public DateTimeOffset? ClaimedUntil { get; private set; }
    /// <summary>Último <c>lastUpdatedAt</c> do Item que uma sincronização completa já trouxe: a origem só tem novidade se este valor avançar.</summary>
    public DateTimeOffset? SourceUpdatedAt { get; private set; }
    public string? SourceStatus { get; private set; }
    public DateTimeOffset? ConsentExpiresAt { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static PluggySyncState Create(Guid workspaceId, Guid connectionId, DateTimeOffset now) =>
        new() { WorkspaceId = workspaceId, ConnectionId = connectionId, UpdatedAt = now };

    public void Claim(DateTimeOffset until, DateTimeOffset now) { ClaimedUntil = until; UpdatedAt = now; }

    public void Release(DateTimeOffset now) { ClaimedUntil = null; UpdatedAt = now; }

    /// <summary>Guarda o que a consulta ao Item informou sobre o consentimento e a situação (sem nenhum dado financeiro).</summary>
    public void ObserveItem(string? status, DateTimeOffset? consentExpiresAt, DateTimeOffset now)
    {
        SourceStatus = status is { Length: > StatusMaxLength } ? status[..StatusMaxLength] : status;
        ConsentExpiresAt = consentExpiresAt;
        UpdatedAt = now;
    }

    /// <summary>
    /// Registra o fim de uma tentativa. Falhas somam e o sucesso zera; <paramref name="next"/> já vem calculado pela política de agendamento.
    /// <paramref name="sourceUpdatedAt"/> só é guardado numa sincronização completa: é o que a origem já entregou.
    /// </summary>
    public void RecordAttempt(DateTimeOffset now, PluggySyncOutcome outcome, PluggySyncTrigger trigger, Guid? runId, string? error, bool failure, DateTimeOffset? next, DateTimeOffset? sourceUpdatedAt)
    {
        LastAttemptAt = now;
        LastOutcome = outcome;
        LastTrigger = trigger;
        LastRunId = runId ?? LastRunId;
        if (outcome == PluggySyncOutcome.Completed) { LastSuccessAt = now; SourceUpdatedAt = sourceUpdatedAt ?? SourceUpdatedAt; }
        ConsecutiveFailures = failure ? ConsecutiveFailures + 1 : outcome is PluggySyncOutcome.Completed ? 0 : ConsecutiveFailures;
        LastError = string.IsNullOrWhiteSpace(error) ? null : error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        NextAttemptAt = next;
        ClaimedUntil = null;
        UpdatedAt = now;
    }

    /// <summary>Reagenda sem registrar tentativa (a conexão estava ocupada, por exemplo).</summary>
    public void Reschedule(DateTimeOffset next, DateTimeOffset now) { NextAttemptAt = next; ClaimedUntil = null; UpdatedAt = now; }
}

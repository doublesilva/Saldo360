using Saldo360.Domain.Common;

namespace Saldo360.Domain.Pluggy;

/// <summary>
/// Preferências de automação de um orçamento (R04), uma linha por orçamento. Não cria nada sozinha: sem linha, vale o padrão (sincronização no ritmo do
/// servidor e categorização após a sincronização desligada). Quem liga ou desliga o agendador no servidor é a configuração (<c>Pluggy:AutoSync</c>); aqui o
/// usuário só pode <b>pausar</b> as execuções agendadas do seu orçamento e escolher o intervalo entre elas.
/// </summary>
public sealed class PluggyAutomationSettings : Entity
{
    /// <summary>Intervalos válidos, em horas, entre sincronizações agendadas (a instituição costuma atualizar o Item uma vez por dia).</summary>
    public static readonly IReadOnlyList<int> IntervalOptionsHours = [6, 12, 24];

    private PluggyAutomationSettings() { }

    public Guid WorkspaceId { get; private init; }
    /// <summary>Pausa as execuções agendadas. Não interrompe uma execução já iniciada e não impede "Sincronizar agora".</summary>
    public bool SyncPaused { get; private set; }
    /// <summary>Intervalo escolhido entre as sincronizações agendadas; nulo = o padrão do servidor.</summary>
    public int? SyncIntervalHours { get; private set; }
    /// <summary>Categorizar com a IA, só o que for elegível, ao fim de cada sincronização bem-sucedida. Exige o consentimento registrado em <see cref="CategorizeConsentAt"/>.</summary>
    public bool CategorizeAfterSync { get; private set; }
    public DateTimeOffset? CategorizeConsentAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedByUserId { get; private set; }

    public static PluggyAutomationSettings Create(Guid workspaceId, Guid userId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Orçamento ou usuário inválido.");
        return new() { WorkspaceId = workspaceId, UpdatedAt = now, UpdatedByUserId = userId };
    }

    public void SetSyncPaused(bool paused, Guid userId, DateTimeOffset now) { SyncPaused = paused; Touch(userId, now); }

    public void SetSyncInterval(int? hours, Guid userId, DateTimeOffset now)
    {
        if (hours is { } value && !IntervalOptionsHours.Contains(value)) throw new ArgumentOutOfRangeException(nameof(hours), "Intervalo não suportado.");
        SyncIntervalHours = hours;
        Touch(userId, now);
    }

    /// <summary>Ligar exige o consentimento explícito (envio de descrições saneadas ao provedor de IA); desligar apaga o consentimento.</summary>
    public void SetCategorizeAfterSync(bool enabled, bool consent, Guid userId, DateTimeOffset now)
    {
        if (enabled && !consent && CategorizeConsentAt is null) throw new InvalidOperationException("Consentimento obrigatório.");
        CategorizeAfterSync = enabled;
        CategorizeConsentAt = enabled ? CategorizeConsentAt ?? now : null;
        Touch(userId, now);
    }

    private void Touch(Guid userId, DateTimeOffset now) { UpdatedAt = now; UpdatedByUserId = userId; }
}

public enum PluggyCategorizationTrigger { Manual, AfterSync }

/// <summary>
/// <c>CompletedWithPending</c> = terminou, mas sobraram lançamentos sem categoria segura ou a IA parou antes de processar tudo (limite da execução ou falha do
/// provedor). Uma falha da categorização nunca é uma falha da conexão bancária: a sincronização já está concluída e salva.
/// </summary>
public enum PluggyCategorizationRunState { Running, Completed, CompletedWithPending, Failed }

/// <summary>
/// Uma execução da categorização dos lançamentos importados (R04), manual ou ao fim de uma sincronização. No máximo uma "em andamento" por orçamento (índice
/// único parcial), então clique repetido ou execução concorrente nunca processa o mesmo conjunto duas vezes. Guarda contagens e códigos curtos, nunca
/// descrição, valor ou resposta do provedor.
/// </summary>
public sealed class PluggyCategorizationRun : Entity
{
    public const int CodeMaxLength = 64;
    private PluggyCategorizationRun() { }

    public Guid WorkspaceId { get; private init; }
    public Guid UserId { get; private init; }
    public PluggyCategorizationTrigger Trigger { get; private init; }
    /// <summary>Sincronização que originou esta execução (somente <see cref="PluggyCategorizationTrigger.AfterSync"/>).</summary>
    public Guid? SyncRunId { get; private init; }
    public Guid? ConnectionId { get; private init; }
    public DateTimeOffset StartedAt { get; private init; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public PluggyCategorizationRunState State { get; private set; }
    public bool UsedAi { get; private set; }
    /// <summary>Lançamentos elegíveis considerados nesta execução.</summary>
    public int Considered { get; private set; }
    /// <summary>Classificados sem IA (categoria da Pluggy ou regra do domínio).</summary>
    public int ClassifiedLocal { get; private set; }
    public int ClassifiedAi { get; private set; }
    /// <summary>Sem categoria segura: continuam pendentes para classificação manual.</summary>
    public int Pending { get; private set; }
    /// <summary>Alterados pelo usuário durante a execução: preservados.</summary>
    public int SkippedChanged { get; private set; }
    public int AiItemsSent { get; private set; }
    public int AiCalls { get; private set; }
    /// <summary>Por que a IA parou antes de processar tudo (limite de itens, de tokens, de custo ou falha do provedor).</summary>
    public string? AiStoppedBecause { get; private set; }
    public string? FailureCode { get; private set; }
    public string? Model { get; private set; }

    public static PluggyCategorizationRun Start(Guid workspaceId, Guid userId, PluggyCategorizationTrigger trigger, Guid? syncRunId, Guid? connectionId, bool usedAi, DateTimeOffset now) =>
        new() { WorkspaceId = workspaceId, UserId = userId, Trigger = trigger, SyncRunId = syncRunId, ConnectionId = connectionId, UsedAi = usedAi, StartedAt = now, State = PluggyCategorizationRunState.Running };

    public void Complete(DateTimeOffset now, int considered, int classifiedLocal, int classifiedAi, int pending, int skippedChanged, int aiItemsSent, int aiCalls, string? aiStoppedBecause, string? aiFailure, string? model)
    {
        FinishedAt = now;
        Considered = considered; ClassifiedLocal = classifiedLocal; ClassifiedAi = classifiedAi; Pending = pending; SkippedChanged = skippedChanged;
        AiItemsSent = aiItemsSent; AiCalls = aiCalls; Model = Trim(model);
        AiStoppedBecause = Trim(aiFailure ?? aiStoppedBecause);
        State = pending > 0 || AiStoppedBecause is not null ? PluggyCategorizationRunState.CompletedWithPending : PluggyCategorizationRunState.Completed;
    }

    public void Fail(DateTimeOffset now, string code) { FinishedAt = now; FailureCode = Trim(code); State = PluggyCategorizationRunState.Failed; }

    private static string? Trim(string? value) => value is { Length: > CodeMaxLength } ? value[..CodeMaxLength] : value;
}

using Saldo360.Domain.Common;

namespace Saldo360.Domain.Planning;

public enum AmountConfirmationEventKind
{
    /// <summary>Primeiro valor confirmado da competência.</summary>
    Confirmed,
    /// <summary>Valor (ou origem) de uma confirmação existente corrigido.</summary>
    Updated,
    /// <summary>Confirmação automática desfeita porque a fonte foi definitivamente invalidada.</summary>
    Reverted,
    /// <summary>A obrigação continua a mesma; a referência bancária foi trocada por outra transação.</summary>
    SourceReplaced,
    /// <summary>Débito bancário com candidatos, mas sem par seguro: nada foi associado; guarda as evidências para evoluir as regras.</summary>
    Ambiguous,
}

/// <summary>
/// Trilha de auditoria (somente acréscimo) das atualizações de valor confirmado e dos diagnósticos de ambiguidade (PROJ-05).
/// Nunca é editada: cada linha registra o que mudou, a origem e a transação bancária de referência.
/// </summary>
public sealed class FinancialAmountConfirmationEvent : Entity
{
    public const int ReasonMaxLength = 64;
    private FinancialAmountConfirmationEvent() { Reason = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public Guid? RecurrenceId { get; private init; }
    public DateOnly? Competence { get; private init; }
    public Guid? PlannedTransactionId { get; private init; }
    public AmountConfirmationEventKind Kind { get; private init; }
    public AmountConfirmationSource? Source { get; private init; }
    public decimal? PreviousAmount { get; private init; }
    public decimal? NewAmount { get; private init; }
    /// <summary>Transação bancária de referência (a atual; em <c>Ambiguous</c>, o débito sem par).</summary>
    public Guid? SourceTransactionId { get; private init; }
    public string Reason { get; private init; }
    /// <summary>JSON com candidatos, evidências e impacto (diagnóstico); nulo nos demais.</summary>
    public string? Details { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }

    public static FinancialAmountConfirmationEvent Create(Guid workspaceId, Guid? recurrenceId, DateOnly? competence, Guid? plannedTransactionId, AmountConfirmationEventKind kind,
        AmountConfirmationSource? source, decimal? previousAmount, decimal? newAmount, Guid? sourceTransactionId, string reason, string? details, DateTimeOffset now) =>
        new()
        {
            WorkspaceId = workspaceId, RecurrenceId = recurrenceId, Competence = competence is { } c ? new DateOnly(c.Year, c.Month, 1) : null, PlannedTransactionId = plannedTransactionId,
            Kind = kind, Source = source, PreviousAmount = previousAmount, NewAmount = newAmount, SourceTransactionId = sourceTransactionId,
            Reason = reason.Length > ReasonMaxLength ? reason[..ReasonMaxLength] : reason, Details = details, CreatedAt = now,
        };
}

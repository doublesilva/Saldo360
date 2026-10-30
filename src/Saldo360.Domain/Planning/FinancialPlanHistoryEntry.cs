using Saldo360.Domain.Common;

namespace Saldo360.Domain.Planning;

public enum PlanHistorySubject { Line, CategoryLimit }

public enum PlanHistoryAction { Created, Revised, Removed, Restored, Copied }

/// <summary>
/// T10.2: histórico mínimo e imutável das alterações do planejamento (criação, revisão, remoção, restauração e cópia), com valor anterior e novo.
/// Só se acrescenta: nunca é editado nem apagado individualmente (apenas pelo reset completo do orçamento). O rótulo preserva o nome que o item tinha
/// no momento da alteração.
/// </summary>
public sealed class FinancialPlanHistoryEntry : Entity
{
    public const int LabelMaxLength = 200;
    public const int NoteMaxLength = 300;

    private FinancialPlanHistoryEntry() { Label = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public DateOnly Competence { get; private init; }
    public PlanHistorySubject Subject { get; private init; }
    public Guid SubjectId { get; private init; }
    public PlanHistoryAction Action { get; private init; }
    public string Label { get; private init; }
    public decimal? PreviousAmount { get; private init; }
    public decimal? NewAmount { get; private init; }
    public string? Note { get; private init; }
    public Guid UserId { get; private init; }
    public DateTimeOffset At { get; private init; }

    public static FinancialPlanHistoryEntry Create(
        Guid workspaceId, DateOnly competence, PlanHistorySubject subject, Guid subjectId, PlanHistoryAction action, string label,
        decimal? previousAmount, decimal? newAmount, string? note, Guid userId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || subjectId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Histórico inválido.");
        if (competence.Day != 1) throw new ArgumentException("A competência deve ser o primeiro dia do mês.", nameof(competence));
        var trimmedLabel = (label ?? string.Empty).Trim();
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return new()
        {
            WorkspaceId = workspaceId, Competence = competence, Subject = subject, SubjectId = subjectId, Action = action,
            Label = trimmedLabel.Length > LabelMaxLength ? trimmedLabel[..LabelMaxLength] : trimmedLabel,
            PreviousAmount = previousAmount, NewAmount = newAmount,
            Note = trimmedNote is { Length: > NoteMaxLength } ? trimmedNote[..NoteMaxLength] : trimmedNote,
            UserId = userId, At = now,
        };
    }
}

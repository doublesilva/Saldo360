using Saldo360.Domain.Common;

namespace Saldo360.Domain.Budgets;

public enum MonthlyAggregateTargetType { FixedExpenses, CreditCards, TotalExpenses, RecoveryAmount }

/// <summary>Independent approved snapshot; never a summable category target.</summary>
public sealed class FinancialMonthlyAggregateTarget : Entity
{
    private FinancialMonthlyAggregateTarget() { }
    public Guid WorkspaceId { get; private init; }
    public DateOnly Competence { get; private init; }
    public MonthlyAggregateTargetType TargetType { get; private init; }
    public decimal Amount { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public static FinancialMonthlyAggregateTarget Create(Guid workspaceId, DateOnly competence, MonthlyAggregateTargetType type, decimal amount, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || competence.Day != 1 || !Enum.IsDefined(type) || amount < 0 || Money.Validate(amount, "Meta") is not null)
            throw new ArgumentException("Meta agregada inválida.");
        return new() { WorkspaceId = workspaceId, Competence = competence, TargetType = type, Amount = amount, CreatedAt = now };
    }
}

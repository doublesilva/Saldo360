using Saldo360.Domain.Common;

namespace Saldo360.Domain.Accounts;

/// <summary>Fotografia imutável do plano de recuperação de uma conta em uma competência.</summary>
public sealed class OverdraftRecoverySnapshot : Entity
{
    private OverdraftRecoverySnapshot() { Composition = string.Empty; Quality = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public Guid AccountId { get; private init; }
    public DateOnly Competence { get; private init; }
    public decimal OpeningBalance { get; private init; }
    public DateOnly ReferenceDate { get; private init; }
    public decimal PlannedIncome { get; private init; }
    public decimal PlannedExpense { get; private init; }
    public decimal PlannedClosingBalance { get; private init; }
    public decimal RecoveryTarget { get; private init; }
    public DateTimeOffset CapturedAt { get; private init; }
    public string Quality { get; private init; }
    public string Composition { get; private init; }

    public static OverdraftRecoverySnapshot Create(Guid workspaceId, Guid accountId, DateOnly competence, decimal openingBalance,
        DateOnly referenceDate, decimal plannedIncome, decimal plannedExpense, decimal plannedClosingBalance, string quality, string composition, DateTimeOffset capturedAt)
    {
        var openingUsed = Math.Max(0m, -openingBalance);
        var closingUsed = Math.Max(0m, -plannedClosingBalance);
        return new OverdraftRecoverySnapshot
        {
            WorkspaceId = workspaceId, AccountId = accountId, Competence = new(competence.Year, competence.Month, 1),
            OpeningBalance = openingBalance, ReferenceDate = referenceDate, PlannedIncome = plannedIncome, PlannedExpense = plannedExpense,
            PlannedClosingBalance = plannedClosingBalance, RecoveryTarget = openingUsed - closingUsed, Quality = quality,
            Composition = composition, CapturedAt = capturedAt,
        };
    }
}

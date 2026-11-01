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
    /// <summary>Fator aplicado à capacidade de recuperação. Nulo = meta histórica (anterior à regra de fixação no dia 10), preservada como foi gravada.</summary>
    public decimal? TargetFactor { get; private init; }
    /// <summary>Recuperação integral projetada (antes do fator) usada na fixação. Nulo nas metas históricas.</summary>
    public decimal? TargetCapacity { get; private init; }
    /// <summary>Momento da fixação automática da meta. Nulo nas metas históricas (a fixação delas é <see cref="CapturedAt"/>).</summary>
    public DateTimeOffset? FixedAt { get; private init; }

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

    /// <summary>Meta fixada pela regra vigente: <c>target = round(capacity × factor, 2)</c>, gravada uma única vez (o fator fica registrado para distinguir de metas históricas).</summary>
    public static OverdraftRecoverySnapshot CreateFixed(Guid workspaceId, Guid accountId, DateOnly competence, decimal openingBalance, DateOnly referenceDate,
        decimal plannedIncome, decimal plannedExpense, decimal projectedClosingBalance, decimal capacity, decimal factor, decimal target, string quality, string composition, DateTimeOffset now) =>
        new()
        {
            WorkspaceId = workspaceId, AccountId = accountId, Competence = new(competence.Year, competence.Month, 1),
            OpeningBalance = openingBalance, ReferenceDate = referenceDate, PlannedIncome = plannedIncome, PlannedExpense = plannedExpense,
            PlannedClosingBalance = projectedClosingBalance, RecoveryTarget = target, Quality = quality, Composition = composition, CapturedAt = now,
            TargetFactor = factor, TargetCapacity = capacity, FixedAt = now,
        };
}

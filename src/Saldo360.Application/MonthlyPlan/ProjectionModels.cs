namespace Saldo360.Application.MonthlyPlan;

// Entrega 2: contrato da projeção financeira consolidada (cenários Atual e Tetos). Dinheiro como texto decimal exato. Leitura apenas.

public sealed record ProjectionPfDto(
    string? Opening, string Entries, string OutsideCardsExpenses, string OutsideCardsComplement, string CardConsumption, string CardConsumptionComplement, string CardPaymentsInCash,
    string TransfersIn, string? Result, string? Closing);

public sealed record ProjectionBudgetDto(
    string? CardTarget, string CardProjected, string? CardMargin, string? CardExcess, string? OutsideTarget, string OutsideProjected, string? OutsideMargin, string? OutsideExcess);

public sealed record ProjectionOverdraftDto(
    string? Limit, string UsedStart, string UsedEnd, string? Available, string MonthlyRecovery, string CumulativeRecovery, string PositiveBalance);

public sealed record ProjectionMonthDto(
    string Competence, string PeriodKind, IntegratedIncomeDto Income, IntegratedPjDto Pj, IntegratedTransfersDto Transfers, ProjectionPfDto Pf, ProjectionBudgetDto Budget,
    ProjectionOverdraftDto? Overdraft, bool Complete, IReadOnlyList<string> Quality);

/// <summary><c>Name</c>: Current (despesas conhecidas, recorrências e estimativas já existentes) ou Ceilings (consumo integral dos tetos, sem reduzir o que os excede).</summary>
public sealed record ProjectionScenarioDto(string Name, IReadOnlyList<ProjectionMonthDto> Months);

public sealed record FinancialProjectionDto(
    string From, int MonthCount, DateOnly ReferenceDate, Guid PjAccountId, Guid PfAccountId, IReadOnlyList<ProjectionScenarioDto> Scenarios, IReadOnlyList<string> Notes);

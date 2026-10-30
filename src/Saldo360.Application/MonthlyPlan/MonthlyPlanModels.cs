using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.MonthlyPlan;

// T10.2: planejamento mensal. Valores monetários trafegam como texto decimal exato (duas casas, ponto); nenhuma fórmula existe no navegador.
// O plano é META: não gera lançamento, previsão, pagamento nem transferência, e nunca altera saldo ou projeção da Agenda.

// ---- Fatos lidos do banco (somente o que é necessário para os cálculos) ------------------------------------------------------------------------------

public sealed record PlanLineFact(
    Guid Id, DateOnly Competence, PlanLineKind Kind, PlanLineNature? Nature, string Description, Guid? AccountId, Guid? DestinationAccountId, Guid? CategoryId,
    decimal OriginalAmount, decimal Amount, DateOnly? ExpectedDate, decimal? HourlyRate, decimal? HoursPerDay, int? BillableDays, DateTimeOffset UpdatedAt);

/// <summary>Limite mensal por categoria (meta do orçamento existente). <c>OriginalAmount</c> vem do histórico; nulo quando a meta é anterior ao histórico e nunca foi revista.</summary>
public sealed record PlanLimitFact(Guid TargetId, Guid BudgetId, DateOnly Competence, Guid CategoryId, string CategoryName, decimal Amount, decimal? OriginalAmount);

public sealed record PlanFacts(IReadOnlyList<PlanLineFact> Lines, IReadOnlyList<PlanLimitFact> Limits)
{
    public static PlanFacts Empty { get; } = new([], []);
}

/// <summary>Totais planejados de um escopo, na mesma base do indicador de consumo do Dashboard (reservas ficam à parte).</summary>
public sealed record PlanTotals(
    decimal? Income, int IncomeLines, decimal? Expense, int ExpenseLines, decimal Reserve, int ReserveLines, int LimitCount, bool LimitsNotAttributable,
    int TransferCount, decimal TransfersOut, decimal TransfersIn)
{
    public bool HasPlan => IncomeLines > 0 || ExpenseLines > 0 || LimitCount > 0 || ReserveLines > 0 || TransferCount > 0;
}

// ---- Contratos ----------------------------------------------------------------------------------------------------------------------------------

public sealed record HourlyDto(string HourlyRate, string HoursPerDay, int BillableDays, int BusinessDays, bool ExceedsCalendar);

public sealed record PlanLineDto(
    Guid Id, string Competence, string Kind, string? Nature, string Description, Guid? AccountId, string? AccountName, string? AccountScope, bool AccountActive,
    Guid? DestinationAccountId, string? DestinationAccountName, Guid? CategoryId, string? CategoryName, string OriginalAmount, string Amount, string Difference,
    bool Revised, DateOnly? ExpectedDate, HourlyDto? Hourly, DateTimeOffset UpdatedAt);

public sealed record LimitExecutionDto(string Realized, string Committed, string CardCommitted, string Projected, string Available, string Exceeded, string? Percent, string Status);

public sealed record CategoryLimitDto(
    Guid TargetId, Guid CategoryId, string CategoryName, string Amount, string? OriginalAmount, bool Revised, string ItemizedAmount, bool ItemizedAboveLimit, LimitExecutionDto? Execution);

public sealed record PlanTotalsDto(
    string? Income, int IncomeLines, string? Expense, int ExpenseLines, string Reserve, int ReserveLines, string? Result, int LimitCount, bool LimitsNotAttributable,
    int TransferCount, string TransfersOut, string TransfersIn, string? IncomeSource, string? ExpenseSource);

public sealed record PendencyDto(string Code, int Count);

/// <summary>Realização do mês, vinda do próprio Dashboard (T10.1): a mesma base financeira para o planejado, o projetado e o realizado.</summary>
public sealed record MonthlyExecutionDto(IndicatorDto Income, IndicatorDto Consumption, CardTargetDto Cards, CoverageDto Coverage, bool SnapshotConsistent);

public sealed record MonthlyPlanDto(
    string Competence, DateOnly AsOf, string PeriodKind, PlanActualScopeDto Scope, bool HasPlan, PlanTotalsDto Planned, IReadOnlyList<PlanLineDto> Lines,
    IReadOnlyList<CategoryLimitDto> CategoryLimits, MonthlyExecutionDto Execution, int BusinessDays, IReadOnlyList<PendencyDto> Pendencies);

public sealed record PlanHistoryDto(
    Guid Id, string Competence, string Subject, Guid SubjectId, string Action, string Label, string? PreviousAmount, string? NewAmount, string? Note, Guid UserId, DateTimeOffset At);

public sealed record NonWorkingDayDto(Guid Id, DateOnly Date, string Kind, string Description, bool Weekend);

public sealed record WorkingDaysDto(string Competence, int CalendarDays, int Weekdays, int NonWorkingWeekdays, int BusinessDays, IReadOnlyList<NonWorkingDayDto> NonWorkingDays);

public sealed record HourlyIncomeDto(string Competence, string HourlyRate, string HoursPerDay, int BillableDays, int BusinessDays, string Amount);

public sealed record CopyPlanResultDto(
    string FromCompetence, string ToCompetence, int LinesCopied, int LinesSkipped, int LimitsCopied, int LimitsSkipped, IReadOnlyList<Guid> RecalculatedHourlyLines);

public sealed record MonthCoverageDto(bool DataComplete, string ObligationsCoverage, IReadOnlyList<string> Reasons);

public sealed record PlanMonthDto(
    string Competence, string PeriodKind, string? PlannedIncome, string? PlannedIncomeSource, string ProjectedIncome, string RealizedIncome,
    string? PlannedExpense, string? PlannedExpenseSource, string ProjectedExpense, string RealizedExpense, string? PlannedReserve,
    string? PlannedResult, string ProjectedResult, string RealizedResult, string KnownObligations, string? ClosingBalance, string? MinimumProjectedBalance,
    DateOnly? FirstNegativeProjectedDate, string BalanceStatus, string Risk, IReadOnlyList<string> RiskReasons, MonthCoverageDto Coverage);

public sealed record SixMonthSummaryDto(IReadOnlyList<string> HighestRiskMonths, int MonthsWithoutPlan, int MonthsWithRisk, string? PlannedResult, string ProjectedResult);

public sealed record SixMonthPlanDto(string From, string Through, DateOnly AsOf, PlanActualScopeDto Scope, IReadOnlyList<PlanMonthDto> Months, SixMonthSummaryDto Summary);

// ---- Pedidos ------------------------------------------------------------------------------------------------------------------------------------

/// <summary>Linha de plano. <c>Amount</c> é o valor direto; na receita por hora, informe <c>HourlyRate</c>, <c>HoursPerDay</c> e <c>BillableDays</c> e o valor é calculado pelo backend.</summary>
public sealed record PlanLineRequest(
    string? Competence, string? Kind, string? Nature, string? Description, Guid? AccountId, Guid? DestinationAccountId, Guid? CategoryId, string? Amount,
    DateOnly? ExpectedDate, string? HourlyRate, string? HoursPerDay, int? BillableDays, string? Note);

public sealed record CategoryLimitRequest(string? Competence, Guid? CategoryId, string? Amount, string? Note);

public sealed record CopyPlanRequest(string? FromCompetence, string? ToCompetence, bool Confirm);

public sealed record NonWorkingDayRequest(DateOnly? Date, string? Kind, string? Description);

public enum MonthlyPlanAccess { Allowed, NotFound, Invalid, Conflict }

public sealed record MonthlyPlanResult<T>(MonthlyPlanAccess Access, T? Value, IDictionary<string, string[]>? Errors, string? Message = null) where T : class
{
    public static MonthlyPlanResult<T> NotFound { get; } = new(MonthlyPlanAccess.NotFound, null, null);
    public static MonthlyPlanResult<T> Ok(T value) => new(MonthlyPlanAccess.Allowed, value, null);
    public static MonthlyPlanResult<T> Invalid(string field, string message) => new(MonthlyPlanAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
    public static MonthlyPlanResult<T> Invalid(IDictionary<string, string[]> errors) => new(MonthlyPlanAccess.Invalid, null, errors);
    public static MonthlyPlanResult<T> Conflict(string message) => new(MonthlyPlanAccess.Conflict, null, null, message);
}

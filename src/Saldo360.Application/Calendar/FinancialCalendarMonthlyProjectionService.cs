using Saldo360.Application.Workspaces;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Calendar;

/// <summary>Consolida competências mensais reutilizando integralmente a evolução diária da Agenda.</summary>
public sealed class FinancialCalendarMonthlyProjectionService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialCalendarBalanceRepository repository,
    TimeProvider timeProvider)
{
    public async Task<FinancialCalendarMonthlyProjectionResult> GetAsync(
        Guid workspaceId,
        Guid userId,
        FinancialCalendarMonthlyProjectionQuery query,
        CancellationToken cancellationToken,
        DateOnly? capturedToday = null)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is null)
        {
            return FinancialCalendarMonthlyProjectionResult.NotFoundResult;
        }

        if (query.Months is < 1 or > 12)
        {
            return new FinancialCalendarMonthlyProjectionResult(null, new Dictionary<string, string[]>
            {
                ["months"] = ["A quantidade de meses deve estar entre 1 e 12."],
            }, false);
        }

        if (query.From.Day != 1 || query.From.Year >= 9999)
            return new(null, new Dictionary<string, string[]> { ["from"] = ["Informe uma competência inicial válida."] }, false);

        var today = capturedToday ?? BusinessCalendar.Today(timeProvider.GetUtcNow());
        var through = query.From.AddMonths(query.Months).AddDays(-1);
        var snapshot = await repository.ReadMonthlyAsync(
            workspaceId, query.AccountId, query.From, through, today, cancellationToken);
        if (snapshot is null)
        {
            return FinancialCalendarMonthlyProjectionResult.NotFoundResult;
        }

        var realized = snapshot.PeriodRealized ?? [];
        var months = new List<FinancialCalendarMonthlyProjectionMonth>(query.Months);
        for (var index = 0; index < query.Months; index++)
        {
            var month = query.From.AddMonths(index);
            var monthThrough = month.AddMonths(1).AddDays(-1);
            var balances = FinancialCalendarBalanceService.CalculateBalances(
                new FinancialCalendarBalanceQuery(month, monthThrough, query.AccountId), today, snapshot);
            var realizedTotals = Sum(realized.Where(row => row.Date >= month && row.Date <= monthThrough));
            var pendingRows = (snapshot.Pending ?? []).Concat(snapshot.CreditCardObligations ?? []).Concat(snapshot.DebtObligations ?? []).Where(row => row.Date >= month && row.Date <= monthThrough).ToList();
            var pendingTotals = Sum(pendingRows);
            var overdueTotals = Sum(pendingRows.Where(row => row.Date < today));
            var projectedDays = balances.Days.Where(day => day.Kind == FinancialCalendarBalanceKind.Projected
                && day.ClosingBalance is not null).ToList();
            var budget = snapshot.Budgets?.SingleOrDefault(x => x.Competence == month) is { } source
                ? source with { Targets = source.Targets.Where(x => x.GroupingType == Saldo360.Domain.Budgets.BudgetTargetGroupingType.Category).ToList() } // fixas e cartões repetem os mesmos gastos: só metas de categoria somam
                : null;
            var budgetStatus = budget is null || budget.Targets.Count == 0 ? "NotDefined" : budget.Targets.Max(x =>
                Saldo360.Application.Budgets.FinancialBudgetExecutionService.Classify(x.Planned, x.Realized + x.Committed + x.CreditCardCommitted)).ToString();

            months.Add(new FinancialCalendarMonthlyProjectionMonth(
                month,
                realizedTotals.Income,
                realizedTotals.Expense,
                pendingTotals.Income,
                pendingTotals.Expense,
                overdueTotals.Income,
                overdueTotals.Expense,
                balances.OpeningBalance,
                balances.Days[^1].ClosingBalance,
                projectedDays.Count == 0 ? null : projectedDays.Min(day => day.ClosingBalance!.Value),
                projectedDays.FirstOrDefault(day => day.IsNegative)?.Date,
                balances.UnavailableReason,
                balances.CreditCardProjectionComplete,
                (snapshot.CreditCardObligations ?? []).Where(row => row.Date >= month && row.Date <= monthThrough).Sum(row => row.Expense),
                (snapshot.CardPayments ?? []).Where(row => row.Date >= month && row.Date <= monthThrough).Sum(row => row.Expense),
                budget?.Targets.Sum(x => x.Planned),
                budget?.Targets.Sum(x => x.Realized + x.Committed + x.CreditCardCommitted), budgetStatus,
                monthThrough < today ? "Historical" : month <= today ? "Current" : "Future", balances.DebtProjectionComplete));
        }

        return new FinancialCalendarMonthlyProjectionResult(
            new FinancialCalendarMonthlyProjection(query.From, months), null, false);
    }

    private static (decimal Income, decimal Expense) Sum(IEnumerable<FinancialCalendarDailyTotals> rows) =>
        rows.Aggregate((Income: 0m, Expense: 0m), (total, row) =>
            (total.Income + row.Income, total.Expense + row.Expense));
}

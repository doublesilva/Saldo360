using Saldo360.Application.Accounts;
using Saldo360.Application.Families;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Calendar;

/// <summary>Evolução diária realizada/projetada da Agenda, sem persistir saldos calculados.</summary>
public sealed class FinancialCalendarBalanceService(
    IFamilyRepository families,
    IFinancialCalendarBalanceRepository repository,
    TimeProvider timeProvider)
{
    public async Task<FinancialCalendarBalanceResult> GetAsync(
        Guid familyId,
        Guid userId,
        FinancialCalendarBalanceQuery query,
        CancellationToken cancellationToken)
    {
        if (await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is null)
        {
            return FinancialCalendarBalanceResult.NotFoundResult;
        }

        if (query.Through < query.From)
        {
            return new FinancialCalendarBalanceResult(null, new Dictionary<string, string[]>
            {
                ["through"] = ["A data final deve ser posterior ou igual à inicial."],
            }, false);
        }

        var today = BusinessCalendar.Today(timeProvider.GetUtcNow());
        var snapshot = await repository.ReadAsync(
            familyId, query.AccountId, query.Through, today, cancellationToken);
        if (snapshot is null)
        {
            return FinancialCalendarBalanceResult.NotFoundResult;
        }

        return new FinancialCalendarBalanceResult(CalculateBalances(query, today, snapshot), null, false);
    }

    public static FinancialCalendarBalances CalculateBalances(
        FinancialCalendarBalanceQuery query,
        DateOnly today,
        FinancialCalendarBalanceSnapshot snapshot)
    {
        var missingOpening = snapshot.Accounts.Any(account =>
            account.OpeningBalance is null || account.OpeningBalanceDate is null);
        var latestOpeningDate = snapshot.Accounts
            .Where(account => account.OpeningBalanceDate is not null)
            .Select(account => account.OpeningBalanceDate!.Value)
            .DefaultIfEmpty(DateOnly.MinValue)
            .Max();
        var beforeOpening = !missingOpening && snapshot.Accounts.Count > 0
            && (query.From < latestOpeningDate || (query.Through >= today && today < latestOpeningDate));

        if (missingOpening || beforeOpening)
        {
            return Unavailable(
                query,
                today,
                snapshot,
                missingOpening
                    ? BalanceUnavailableReason.OpeningBalanceNotConfigured
                    : BalanceUnavailableReason.BeforeOpeningBalanceDate);
        }

        var allPending = AggregatePending(snapshot);
        var realizedByDate = snapshot.Realized.ToDictionary(row => row.Date);
        var pendingByDate = allPending.ToDictionary(row => row.Date);
        var openingTotal = snapshot.Accounts.Sum(account => account.OpeningBalance ?? 0m);
        var overdue = Sum(allPending.Where(row => row.Date < today));
        var hasOverdue = overdue.Income != 0m || overdue.Expense != 0m;

        decimal CurrentActualBefore(DateOnly date) => snapshot.Realized
            .Where(row => row.Date < date)
            .Aggregate(openingTotal, (balance, row) => Apply(balance, row));

        decimal current;
        if (query.From <= today)
        {
            current = CurrentActualBefore(query.From);
        }
        else
        {
            var realizedToday = snapshot.Realized
                .Where(row => row.Date <= today)
                .Aggregate(openingTotal, (balance, row) => Apply(balance, row));
            var plannedBefore = Sum(allPending.Where(row => row.Date >= today && row.Date < query.From));
            current = FinancialBalanceCalculator.ApplyMovements(
                realizedToday,
                overdue.Income + plannedBefore.Income,
                overdue.Expense + plannedBefore.Expense);
        }

        var openingBalance = current;
        var days = new List<FinancialCalendarBalanceDay>();
        for (var date = query.From; ; date = date.AddDays(1))
        {
            realizedByDate.TryGetValue(date, out var realized);
            pendingByDate.TryGetValue(date, out var pending);
            var realizedIncome = realized?.Income ?? 0m;
            var realizedExpense = realized?.Expense ?? 0m;
            var transferIn = realized?.TransferIn ?? 0m;
            var transferOut = realized?.TransferOut ?? 0m;

            if (date < today)
            {
                current = FinancialBalanceCalculator.ApplyMovements(
                    current, realizedIncome, realizedExpense, transferIn, transferOut);
                days.Add(AvailableDay(
                    date,
                    FinancialCalendarBalanceKind.Realized,
                    realizedIncome,
                    realizedExpense,
                    transferIn,
                    transferOut,
                    current,
                    pending is { Income: not 0m } || pending is { Expense: not 0m }));
            }
            else if (date == today)
            {
                var realizedClosing = FinancialBalanceCalculator.ApplyMovements(
                    current, realizedIncome, realizedExpense, transferIn, transferOut);
                var todayPending = new PlannedTotals(
                    overdue.Income,
                    overdue.Expense,
                    pending?.Income ?? 0m,
                    pending?.Expense ?? 0m);
                current = FinancialBalanceCalculator.Project(realizedClosing, todayPending);
                days.Add(AvailableDay(
                    date,
                    FinancialCalendarBalanceKind.Projected,
                    realizedIncome + todayPending.PlannedIncome,
                    realizedExpense + todayPending.PlannedExpense,
                    transferIn,
                    transferOut,
                    current,
                    hasOverdue));
            }
            else
            {
                var pendingIncome = pending?.Income ?? 0m;
                var pendingExpense = pending?.Expense ?? 0m;
                current = FinancialBalanceCalculator.ApplyMovements(current, pendingIncome, pendingExpense);
                days.Add(AvailableDay(
                    date,
                    FinancialCalendarBalanceKind.Projected,
                    pendingIncome,
                    pendingExpense,
                    0m,
                    0m,
                    current,
                    hasOverdue));
            }

            if (date == query.Through)
            {
                break;
            }
        }

        days = AddChartPositions(days);
        var projected = days.Where(day => day.Kind == FinancialCalendarBalanceKind.Projected).ToList();
        var risk = new FinancialCalendarBalanceRisk(
            projected.Count > 0 ? projected.Min(day => day.ClosingBalance!.Value) : null,
            projected.FirstOrDefault(day => day.IsNegative)?.Date,
            projected.Count > 0 && query.Through >= today ? projected[^1].ClosingBalance : null);
        return new FinancialCalendarBalances(
            query.From, query.Through, query.AccountId, openingBalance, null, days, risk,
            !snapshot.HasUnallocatedCreditCardObligations && !snapshot.HasUnallocatedDebtObligations,
            (snapshot.CreditCardObligations ?? []).Sum(row => row.Expense),
            !snapshot.HasUnallocatedDebtObligations,
            (snapshot.DebtObligations ?? []).Sum(row => row.Expense));
    }

    private static FinancialCalendarBalances Unavailable(
        FinancialCalendarBalanceQuery query,
        DateOnly today,
        FinancialCalendarBalanceSnapshot snapshot,
        BalanceUnavailableReason reason)
    {
        var allPending = AggregatePending(snapshot);
        var pendingByDate = allPending.ToDictionary(row => row.Date);
        var hasOverdue = allPending.Any(row => row.Date < today && (row.Income != 0m || row.Expense != 0m));
        var days = new List<FinancialCalendarBalanceDay>();
        for (var date = query.From; ; date = date.AddDays(1))
        {
            pendingByDate.TryGetValue(date, out var pending);
            days.Add(new FinancialCalendarBalanceDay(
                date,
                date < today ? FinancialCalendarBalanceKind.Realized : FinancialCalendarBalanceKind.Projected,
                null,
                null,
                null,
                null,
                null,
                date < today
                    ? pending is { Income: not 0m } || pending is { Expense: not 0m }
                    : hasOverdue,
                false,
                null,
                reason));
            if (date == query.Through)
            {
                break;
            }
        }

        return new FinancialCalendarBalances(
            query.From,
            query.Through,
            query.AccountId,
            null,
            reason,
            days,
            new FinancialCalendarBalanceRisk(null, null, null),
            !snapshot.HasUnallocatedCreditCardObligations && !snapshot.HasUnallocatedDebtObligations,
            (snapshot.CreditCardObligations ?? []).Sum(row => row.Expense),
            !snapshot.HasUnallocatedDebtObligations,
            (snapshot.DebtObligations ?? []).Sum(row => row.Expense));
    }

    private static List<FinancialCalendarDailyTotals> AggregatePending(FinancialCalendarBalanceSnapshot snapshot) =>
        (snapshot.Pending ?? []).Concat(snapshot.CreditCardObligations ?? []).Concat(snapshot.DebtObligations ?? [])
            .GroupBy(row => row.Date)
            .Select(group => new FinancialCalendarDailyTotals(group.Key, group.Sum(row => row.Income),
                group.Sum(row => row.Expense), group.Sum(row => row.TransferIn), group.Sum(row => row.TransferOut)))
            .ToList();

    private static FinancialCalendarBalanceDay AvailableDay(
        DateOnly date,
        FinancialCalendarBalanceKind kind,
        decimal income,
        decimal expense,
        decimal transferIn,
        decimal transferOut,
        decimal closingBalance,
        bool hasOverduePending) =>
        new(
            date,
            kind,
            income,
            expense,
            transferIn,
            transferOut,
            closingBalance,
            hasOverduePending,
            closingBalance < 0m,
            null,
            null);

    private static decimal Apply(decimal balance, FinancialCalendarDailyTotals row) =>
        FinancialBalanceCalculator.ApplyMovements(
            balance, row.Income, row.Expense, row.TransferIn, row.TransferOut);

    private static (decimal Income, decimal Expense) Sum(IEnumerable<FinancialCalendarDailyTotals> rows) =>
        rows.Aggregate((Income: 0m, Expense: 0m), (totals, row) =>
            (totals.Income + row.Income, totals.Expense + row.Expense));

    private static List<FinancialCalendarBalanceDay> AddChartPositions(List<FinancialCalendarBalanceDay> days)
    {
        var balances = days.Where(day => day.ClosingBalance is not null).Select(day => day.ClosingBalance!.Value).ToList();
        if (balances.Count == 0)
        {
            return days;
        }

        var minimum = balances.Min();
        var maximum = balances.Max();
        var range = maximum - minimum;
        return days.Select(day => day.ClosingBalance is not { } balance
            ? day
            : day with
            {
                ChartPosition = range == 0m
                    ? 500
                    : decimal.ToInt32(decimal.Round((maximum - balance) * 1000m / range)),
            }).ToList();
    }
}

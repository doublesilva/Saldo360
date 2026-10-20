using Saldo360.Application.Accounts;
using Saldo360.Application.Budgets;
using Saldo360.Application.Calendar;
using Saldo360.Application.CreditCards;
using Saldo360.Application.Debts;
using Saldo360.Application.Workspaces;
using Saldo360.Application.Goals;
using Saldo360.Domain.Common;
using Saldo360.Domain.Debts;
using Saldo360.Domain.Goals;

namespace Saldo360.Application.Dashboard;

public sealed class FinancialDashboardService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialAccountRepository accounts,
    FinancialCalendarService calendar,
    FinancialCalendarMonthlyProjectionService monthlyProjection,
    IFinancialBudgetRepository budgets,
    IFinancialBudgetExecutionRepository budgetExecutions,
    IFinancialCreditCardRepository cards,
    IFinancialCreditCardPurchaseRepository purchases,
    IFinancialDebtRepository debts,
    IFinancialGoalAllocationRepository allocations,
    TimeProvider timeProvider)
{
    public async Task<FinancialDashboardResult> GetAsync(Guid workspaceId, Guid userId, DateOnly competence, int months, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return FinancialDashboardResult.NotFoundResult;
        return await BuildAsync(workspaceId, userId, competence, months, ct);
    }

    private async Task<FinancialDashboardResult> BuildAsync(Guid workspaceId, Guid userId, DateOnly competence, int months, CancellationToken ct)
    {
        if (months is < 1 or > 12) return new(null, new Dictionary<string, string[]> { ["months"] = ["A quantidade de meses deve estar entre 1 e 12."] }, false);

        var asOf = BusinessCalendar.Today(timeProvider.GetUtcNow());
        var projection = await monthlyProjection.GetAsync(workspaceId, userId, new FinancialCalendarMonthlyProjectionQuery(competence, months, null), ct);
        if (projection.NotFound) return FinancialDashboardResult.NotFoundResult;
        if (projection.Errors is not null) return new(null, projection.Errors, false);
        var monthsData = projection.Projection!.Months;
        var first = monthsData[0];
        var calendarFrom = competence <= asOf ? competence : asOf;
        var calendarThrough = competence.AddMonths(months).AddDays(-1);
        var calendarResult = await calendar.ListAsync(workspaceId, userId, new FinancialCalendarQuery(calendarFrom, calendarThrough, null, null), ct);
        if (calendarResult.NotFound) return FinancialDashboardResult.NotFoundResult;

        var accountDtos = await accounts.ListAsync(workspaceId, true, ct);
        var accountBalances = new List<FinancialDashboardAccountBalance>();
        foreach (var account in accountDtos)
        {
            decimal? balance = null;
            var complete = account.OpeningBalance is not null && account.OpeningBalanceDate is not null;
            if (complete)
            {
                var totals = await accounts.SumMovementsAsync(workspaceId, account.Id, account.OpeningBalanceDate!.Value, asOf, ct);
                balance = account.OpeningBalance!.Value + totals.Income - totals.Expense + totals.TransferIn - totals.TransferOut;
            }
            accountBalances.Add(new(account.Id, account.Name, balance, complete));
        }
        var knownBalances = accountBalances.Where(x => x.RealizedBalance is not null).Select(x => x.RealizedBalance!.Value).ToList();
        var realized = knownBalances.Count == accountBalances.Count && accountBalances.Count > 0 ? knownBalances.Sum() : (decimal?)null;
        var negative = knownBalances.Where(x => x < 0).Sum(x => -x);
        var goalSummary = await allocations.SummaryAsync(workspaceId, ct);

        var budgetItems = await budgets.ListAsync(workspaceId, competence, false, ct);
        var targetCount = 0; var planned = 0m; var committed = 0m; var realizedBudget = 0m; var exceeded = 0; var attention = 0;
        foreach (var budget in budgetItems)
        {
            var source = await budgetExecutions.GetAsync(workspaceId, budget.Id, ct);
            if (source is null) continue;
            targetCount += source.Targets.Count;
            planned += source.Targets.Sum(x => x.Planned);
            committed += source.Targets.Sum(x => x.Committed + x.CreditCardCommitted);
            realizedBudget += source.Targets.Sum(x => x.Realized);
            foreach (var target in source.Targets)
            {
                var consumption = target.Realized + target.Committed + target.CreditCardCommitted;
                if (consumption > target.Planned) exceeded++;
                else if (target.Planned > 0 && consumption / target.Planned >= .8m) attention++;
            }
        }

        var cardDtos = await cards.ListAsync(workspaceId, false, ct);
        var creditOutstanding = monthsData.Sum(x => x.CreditCardOutstanding);
        var currentCreditOutstanding = first.CreditCardOutstanding ?? 0m;
        var availableLimit = cardDtos.Count == 0 ? 0m : cardDtos.Sum(x => decimal.Parse(x.CreditLimit, System.Globalization.CultureInfo.InvariantCulture)) - currentCreditOutstanding;
        var hasUnallocatedCards = monthsData.Any(x => !x.CreditCardProjectionComplete);
        var overdueInvoices = 0;
        var futureInvoices = 0;
        var invoiceFrom = competence.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var invoiceThrough = competence.AddMonths(months - 1).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var card in cardDtos)
        {
            var invoices = await purchases.ListInvoicesAsync(workspaceId, card.Id, invoiceFrom, invoiceThrough, asOf, ct);
            overdueInvoices += invoices.Count(x => x.DueDate < asOf && decimal.Parse(x.RemainingAmount, System.Globalization.CultureInfo.InvariantCulture) > 0m);
            futureInvoices += invoices.Count(x => x.DueDate >= asOf && decimal.Parse(x.RemainingAmount, System.Globalization.CultureInfo.InvariantCulture) > 0m);
        }

        var debtDtos = await debts.ListAsync(workspaceId, false, ct);
        var activeDebts = debtDtos.Where(x => x.Status is DebtStatus.Active or DebtStatus.Suspended).ToList();
        var declaredDebt = activeDebts.Select(x => x.OutstandingBalance).Where(x => x is not null).Cast<decimal>().DefaultIfEmpty().Sum();
        var debtUnallocated = activeDebts.Count(x => x.ResponsibleAccountId is null);
        var installmentDue = activeDebts.Where(x => x.InstallmentAmount is not null && x.FirstDueDate is not null)
            .Sum(x => x.InstallmentAmount!.Value * Math.Max(0, (x.InstallmentCount ?? 0) - (x.PaidInstallmentCount ?? 0)));

        var monthly = monthsData.Select(x => new FinancialDashboardMonthlyProjection(x.Month, x.ClosingBalance, x.RealizedIncome, x.RealizedExpense, x.PendingIncome, x.PendingExpense, x.CreditCardOutstanding ?? 0m, x.UnavailableReason is null && x.CreditCardProjectionComplete)).ToList();
        var pendingIncome = first.PendingIncome; var pendingExpense = first.PendingExpense;
        var horizonPendingIncome = monthsData.Sum(x => x.PendingIncome);
        var horizonPendingExpense = monthsData.Sum(x => x.PendingExpense);
        var accountCoverageComplete = accountBalances.Count > 0 && accountBalances.All(x => x.Complete);
        var coverageComplete = accountCoverageComplete && !hasUnallocatedCards && debtUnallocated == 0 && monthly.All(x => x.ProjectionComplete);
        var pendingItems = calendarResult.Calendar!.Items.Where(x => x.Status is FinancialCalendarStatus.Pending or FinancialCalendarStatus.Overdue).OrderBy(x => x.Date).ThenBy(x => x.Id).ToList();
        var next = pendingItems.FirstOrDefault();
        var nextIncome = pendingItems.Where(x => x.Type == Saldo360.Domain.Categories.CategoryType.Income).OrderBy(x => x.Date).ThenBy(x => x.Id).FirstOrDefault();
        var trend = DetermineTrend(monthly);
        var alerts = new List<FinancialDashboardAlert>();
        foreach (var account in accountBalances.Where(x => x.RealizedBalance is < 0m))
            alerts.Add(new("negative-realized-balance", FinancialDashboardAlertSeverity.Critical, "Saldo realizado negativo", $"A conta {account.Name} possui saldo realizado negativo.", null, -account.RealizedBalance, account.AccountId, account.Name, asOf));
        var negativeMonth = monthly.FirstOrDefault(x => x.ProjectionComplete && x.ClosingBalance < 0);
        if (negativeMonth is not null)
            alerts.Add(new("projected-negative-balance-risk", FinancialDashboardAlertSeverity.Warning, "Risco de saldo projetado negativo", "A projeção mensal conhecida indica fechamento negativo.", negativeMonth.Competence, -negativeMonth.ClosingBalance, null, null, asOf));
        foreach (var item in pendingItems.Where(x => x.Status == FinancialCalendarStatus.Overdue))
            alerts.Add(new("overdue-obligation", FinancialDashboardAlertSeverity.Warning, "Obrigação vencida", item.Description, null, item.Amount, item.Id, item.Source.ToString(), item.Date));
        if (exceeded > 0)
            alerts.Add(new("budget-exceeded", FinancialDashboardAlertSeverity.Critical, "Orçamento acima da meta", "Uma ou mais metas excederam o consumo conhecido.", competence, null, null, null, asOf));
        else if (attention > 0)
            alerts.Add(new("budget-near-limit", FinancialDashboardAlertSeverity.Warning, "Orçamento próximo do limite", "Uma ou mais metas atingiram pelo menos 85% do planejado.", competence, null, null, null, asOf));
        if (!coverageComplete)
            alerts.Add(new("incomplete-coverage", FinancialDashboardAlertSeverity.Warning, "Cobertura incompleta", "Parte das obrigações ou contas não está alocada para uma projeção integral.", competence, null, null, dashboardCoverageReason(accountCoverageComplete, hasUnallocatedCards, debtUnallocated), asOf));
        alerts = alerts.OrderByDescending(x => x.Severity).ThenBy(x => x.ReferenceDate).ThenBy(x => x.Code).ThenBy(x => x.SourceId).ToList();
        var indicators = new FinancialDashboardIndicators(
            realized,
            first.ClosingBalance,
            first.ClosingBalance is { } projected && realized is { } baseBalance ? projected - baseBalance : null,
            first.ClosingBalance is null ? null : competence.ToString("yyyy-MM"),
            first.RealizedIncome - first.RealizedExpense,
            first.PendingIncome - first.PendingExpense,
            first.RealizedIncome - first.RealizedExpense + first.PendingIncome - first.PendingExpense,
            first.PendingIncome + first.PendingExpense + (first.CreditCardOutstanding ?? 0m),
            goalSummary.UnallocatedRealizedBalance,
            trend,
            next is null ? null : new(next.Source, next.Id, next.Date, next.Amount, next.Description, next.Account?.Id),
            nextIncome is null ? new("Unknown", null, null, null, null) : new("Known", nextIncome.Date, nextIncome.Amount, null, null));
        var dashboard = new FinancialDashboard(competence, months,
            new(realized, accountBalances, accountBalances.Count, negative, goalSummary.AllocatedToGoals, realized is null ? null : realized - goalSummary.AllocatedToGoals),
            new(first.RealizedIncome, first.RealizedExpense, pendingIncome, pendingExpense),
            new(horizonPendingIncome, horizonPendingExpense, creditOutstanding ?? 0m, installmentDue, 0),
            new(targetCount, planned, committed, realizedBudget, exceeded, attention),
            new(cardDtos.Count, creditOutstanding ?? 0m, overdueInvoices, futureInvoices, hasUnallocatedCards, availableLimit),
            new(activeDebts.Count, 0, installmentDue, declaredDebt == 0m ? null : declaredDebt, debtUnallocated),
            new(goalSummary.ActiveGoals, goalSummary.CompletedGoals, goalSummary.PausedGoals, goalSummary.AllocatedToGoals, goalSummary.UnallocatedRealizedBalance, goalSummary.CoverageComplete),
            monthly,
            new(coverageComplete, hasUnallocatedCards, debtUnallocated > 0, !accountCoverageComplete ? "Uma ou mais contas não possuem saldo inicial configurado." : hasUnallocatedCards ? "Existem obrigações de cartão sem conta de pagamento." : debtUnallocated > 0 ? "Existem parcelas de dívida sem conta responsável." : null),
            indicators,
            alerts,
            asOf);
        return new(dashboard, null, false);
    }

    private static FinancialDashboardTrend DetermineTrend(IReadOnlyList<FinancialDashboardMonthlyProjection> months)
    {
        if (months.Count < 2 || months.Any(x => !x.ProjectionComplete || x.ClosingBalance is null)) return FinancialDashboardTrend.Unknown;
        var delta = months[^1].ClosingBalance!.Value - months[0].ClosingBalance!.Value;
        return Math.Abs(delta) <= 0.01m ? FinancialDashboardTrend.Stable : delta > 0m ? FinancialDashboardTrend.Improving : FinancialDashboardTrend.Deteriorating;
    }

    private static string dashboardCoverageReason(bool accountsComplete, bool cardsIncomplete, int debtUnallocated) =>
        !accountsComplete ? "contas sem saldo inicial" : cardsIncomplete ? "cartões sem conta de pagamento" : debtUnallocated > 0 ? "dívidas sem conta responsável" : "projeção financeira incompleta";
}

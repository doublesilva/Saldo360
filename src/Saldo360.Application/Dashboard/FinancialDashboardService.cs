using Saldo360.Application.Accounts;
using Saldo360.Application.Budgets;
using Saldo360.Application.Calendar;
using Saldo360.Application.CreditCards;
using Saldo360.Application.Debts;
using Saldo360.Application.Families;
using Saldo360.Application.Goals;
using Saldo360.Domain.Common;
using Saldo360.Domain.Debts;
using Saldo360.Domain.Goals;

namespace Saldo360.Application.Dashboard;

public sealed class FinancialDashboardService(
    IFamilyRepository families,
    IFinancialAccountRepository accounts,
    FinancialCalendarMonthlyProjectionService monthlyProjection,
    IFinancialBudgetRepository budgets,
    IFinancialBudgetExecutionRepository budgetExecutions,
    IFinancialCreditCardRepository cards,
    IFinancialCreditCardPurchaseRepository purchases,
    IFinancialDebtRepository debts,
    IFinancialGoalAllocationRepository allocations,
    TimeProvider timeProvider)
{
    public async Task<FinancialDashboardResult> GetAsync(Guid familyId, Guid userId, DateOnly competence, int months, CancellationToken ct)
    {
        if (await families.GetMemberRoleAsync(familyId, userId, ct) is null) return FinancialDashboardResult.NotFoundResult;
        return await BuildAsync(familyId, userId, competence, months, ct);
    }

    private async Task<FinancialDashboardResult> BuildAsync(Guid familyId, Guid userId, DateOnly competence, int months, CancellationToken ct)
    {
        if (months is < 1 or > 12) return new(null, new Dictionary<string, string[]> { ["months"] = ["A quantidade de meses deve estar entre 1 e 12."] }, false);

        var asOf = BusinessCalendar.Today(timeProvider.GetUtcNow());
        var projection = await monthlyProjection.GetAsync(familyId, userId, new FinancialCalendarMonthlyProjectionQuery(competence, months, null), ct);
        if (projection.NotFound) return FinancialDashboardResult.NotFoundResult;
        if (projection.Errors is not null) return new(null, projection.Errors, false);
        var monthsData = projection.Projection!.Months;
        var first = monthsData[0];

        var accountDtos = await accounts.ListAsync(familyId, true, ct);
        var accountBalances = new List<FinancialDashboardAccountBalance>();
        foreach (var account in accountDtos)
        {
            decimal? balance = null;
            var complete = account.OpeningBalance is not null && account.OpeningBalanceDate is not null;
            if (complete)
            {
                var totals = await accounts.SumMovementsAsync(familyId, account.Id, account.OpeningBalanceDate!.Value, asOf, ct);
                balance = account.OpeningBalance!.Value + totals.Income - totals.Expense + totals.TransferIn - totals.TransferOut;
            }
            accountBalances.Add(new(account.Id, account.Name, balance, complete));
        }
        var knownBalances = accountBalances.Where(x => x.RealizedBalance is not null).Select(x => x.RealizedBalance!.Value).ToList();
        var realized = knownBalances.Count == accountBalances.Count && accountBalances.Count > 0 ? knownBalances.Sum() : (decimal?)null;
        var negative = knownBalances.Where(x => x < 0).Sum(x => -x);
        var goalSummary = await allocations.SummaryAsync(familyId, ct);

        var budgetItems = await budgets.ListAsync(familyId, competence, false, ct);
        var targetCount = 0; var planned = 0m; var committed = 0m; var realizedBudget = 0m; var exceeded = 0; var attention = 0;
        foreach (var budget in budgetItems)
        {
            var source = await budgetExecutions.GetAsync(familyId, budget.Id, ct);
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

        var cardDtos = await cards.ListAsync(familyId, false, ct);
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
            var invoices = await purchases.ListInvoicesAsync(familyId, card.Id, invoiceFrom, invoiceThrough, asOf, ct);
            overdueInvoices += invoices.Count(x => x.DueDate < asOf && decimal.Parse(x.RemainingAmount, System.Globalization.CultureInfo.InvariantCulture) > 0m);
            futureInvoices += invoices.Count(x => x.DueDate >= asOf && decimal.Parse(x.RemainingAmount, System.Globalization.CultureInfo.InvariantCulture) > 0m);
        }

        var debtDtos = await debts.ListAsync(familyId, false, ct);
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
            asOf);
        return new(dashboard, null, false);
    }
}

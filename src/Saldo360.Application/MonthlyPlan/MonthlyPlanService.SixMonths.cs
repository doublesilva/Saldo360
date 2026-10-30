using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.Accounts;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.MonthlyPlan;

public sealed partial class MonthlyPlanService
{
    // ---- Seis meses ------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// O mês selecionado e os cinco seguintes: planejado (plano mensal), projetado e realizado, na MESMA base de cada mês do Dashboard. Receitas e despesas
    /// realizadas e pendentes vêm da projeção mensal da Agenda (T07.5); o consumo dos cartões, da projeção T09.4. Nada é gravado e nenhuma receita ou despesa é
    /// inventada para meses vazios: mês futuro sem compromissos conhecidos mostra projetado zero.
    /// </summary>
    public async Task<MonthlyPlanResult<SixMonthPlanDto>> SixMonthsAsync(Guid workspaceId, Guid userId, string? from, string? scope, Guid? accountId, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<SixMonthPlanDto>.NotFound;
        var today = Today;
        var first = new DateOnly(today.Year, today.Month, 1);
        if (!string.IsNullOrWhiteSpace(from) && !TryMonth(from, out first)) return MonthlyPlanResult<SixMonthPlanDto>.Invalid("from", "Use uma competência válida no formato yyyy-MM.");
        var requested = string.IsNullOrWhiteSpace(scope) ? PlanActualScopes.Primary : scope.Trim().ToLowerInvariant();
        if (requested is not (PlanActualScopes.Primary or PlanActualScopes.All or PlanActualScopes.Account)) return MonthlyPlanResult<SixMonthPlanDto>.Invalid("scope", "Use primary, all ou account.");

        var all = await accounts.ListAsync(workspaceId, true, ct);
        var primary = all.FirstOrDefault(a => a.IsPrimary);
        FinancialAccountDto? selected = null;
        if (requested == PlanActualScopes.Account)
        {
            if (accountId is null) return MonthlyPlanResult<SixMonthPlanDto>.Invalid("accountId", "Informe a conta.");
            selected = all.FirstOrDefault(a => a.Id == accountId);
            if (selected is null) return MonthlyPlanResult<SixMonthPlanDto>.NotFound;
        }
        else if (requested == PlanActualScopes.Primary) selected = primary;
        var kind = selected is null ? PlanActualScopes.All : requested == PlanActualScopes.Primary ? PlanActualScopes.Primary : PlanActualScopes.Account;
        IReadOnlyList<Guid>? scopeIds = selected is null ? null : [selected.Id];

        var last = first.AddMonths(5);
        var projection = await monthlyProjection.GetAsync(workspaceId, userId, new(first, 6, selected?.Id), ct, today);
        if (projection.NotFound || projection.Projection is null) return projection.Errors is { } errors ? MonthlyPlanResult<SixMonthPlanDto>.Invalid(errors) : MonthlyPlanResult<SixMonthPlanDto>.NotFound;
        var cards = (await cardProjection.ComputeWithBillsAsync(workspaceId, today, ct)).Where(c => selected is null || c.Projection.Card.PayerAccountId == selected.Id).ToList();
        var planFacts = await repository.LoadAsync(workspaceId, first, last, ct);
        var planAccounts = await repository.AccountsWithPlanAsync(workspaceId, first, last, ct);

        var months = new List<PlanMonthDto>(); var minimums = new Dictionary<string, decimal?>();
        decimal? plannedResultSum = 0m; var projectedResultSum = 0m;
        foreach (var m in projection.Projection.Months)
        {
            var key = Key(m.Month);
            var basisFacts = await planActualRepository.LoadBasisAsync(workspaceId, m.Month, scopeIds, ct);
            var slices = cards.Select(c => PlanActualCalculator.Slice(c.Projection, c.Bills, key)).ToList();
            var consumption = PlanActualCalculator.Consumption(m.RealizedExpense, m.CardPayments, m.PendingExpense, m.CreditCardOutstanding, slices, basisFacts.PendingBank.Expense);
            var monthFacts = new PlanFacts(planFacts.Lines.Where(l => l.Competence == m.Month).ToList(), planFacts.Limits.Where(l => l.Competence == m.Month).ToList());
            var totals = MonthlyPlanCalculator.Planned(monthFacts, selected?.Id);
            var (plannedIncome, incomeSource) = MonthlyPlanCalculator.PlannedIncome(totals, basisFacts.Planned);
            var (plannedExpense, expenseSource) = MonthlyPlanCalculator.PlannedConsumption(totals, m.BudgetTarget, basisFacts.AggregateTotalExpenses, selected is null);

            var projectedIncome = m.RealizedIncome + m.PendingIncome;
            var projectedExpense = consumption.Realized + consumption.Pending;
            var plannedResult = MonthlyPlanCalculator.Result(plannedIncome, plannedExpense);
            var projectedResult = projectedIncome - projectedExpense;
            var realizedResult = m.RealizedIncome - consumption.Realized;
            plannedResultSum = plannedResultSum is { } sum && plannedResult is { } pr ? sum + pr : null;
            projectedResultSum += projectedResult;

            var reasons = new List<string>();
            if (!m.CreditCardProjectionComplete || cards.Any(c => !c.Projection.Coverage.Complete)) reasons.Add(PlanActualCalculator.Reasons.CardCoverageIncomplete);
            if (!m.DebtProjectionComplete) reasons.Add(PlanActualCalculator.Reasons.DebtCoverageIncomplete);
            if (m.UnavailableReason is { } unavailable) reasons.Add(PlanActualCalculator.Reasons.ProjectionUnavailable + ":" + unavailable);
            var complete = reasons.Count == 0;
            var (risk, riskReasons) = MonthlyPlanCalculator.Risk(m.Status, m.PeriodKind, plannedExpense, projectedExpense, plannedResult, projectedResult, complete);
            minimums[key] = m.MinimumProjectedBalance;

            months.Add(new(key, m.PeriodKind, MonthlyPlanCalculator.CentsOrNull(plannedIncome), incomeSource, MonthlyPlanCalculator.Cents(projectedIncome), MonthlyPlanCalculator.Cents(m.RealizedIncome),
                MonthlyPlanCalculator.CentsOrNull(plannedExpense), expenseSource, MonthlyPlanCalculator.Cents(projectedExpense), MonthlyPlanCalculator.Cents(consumption.Realized),
                totals.ReserveLines > 0 ? MonthlyPlanCalculator.Cents(totals.Reserve) : null, MonthlyPlanCalculator.CentsOrNull(plannedResult), MonthlyPlanCalculator.Cents(projectedResult),
                MonthlyPlanCalculator.Cents(realizedResult), MonthlyPlanCalculator.Cents(m.PendingExpense), MonthlyPlanCalculator.CentsOrNull(m.ClosingBalance), MonthlyPlanCalculator.CentsOrNull(m.MinimumProjectedBalance),
                m.FirstNegativeProjectedDate, m.Status, risk, riskReasons,
                new(complete, MonthlyPlanCalculator.ObligationsCoverage(m.ClosingBalance, m.MinimumProjectedBalance, m.FirstNegativeProjectedDate), reasons)));
        }

        var ranked = months.Where(x => MonthlyPlanCalculator.RiskRank(x.Risk) >= 2)
            .OrderByDescending(x => MonthlyPlanCalculator.RiskRank(x.Risk)).ThenBy(x => minimums[x.Competence] ?? decimal.MaxValue).ThenBy(x => x.Competence, StringComparer.Ordinal)
            .Take(3).Select(x => x.Competence).ToList();
        var summary = new SixMonthSummaryDto(ranked, months.Count(x => x.PlannedIncome is null && x.PlannedExpense is null), months.Count(x => MonthlyPlanCalculator.RiskRank(x.Risk) >= 2),
            MonthlyPlanCalculator.CentsOrNull(plannedResultSum), MonthlyPlanCalculator.Cents(projectedResultSum));
        var scopeDto = new PlanActualScopeDto(requested, kind, selected?.Id, selected?.Name, selected?.FinancialScope.ToString(), primary?.Id, primary?.Name,
            requested == PlanActualScopes.Primary && primary is null, PlanActualService.ScopeOptions(all, planAccounts, selected));
        return MonthlyPlanResult<SixMonthPlanDto>.Ok(new(Key(first), Key(last), today, scopeDto, months, summary));
    }
}

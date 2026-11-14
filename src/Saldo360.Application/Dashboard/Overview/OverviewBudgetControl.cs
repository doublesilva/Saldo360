using System.Globalization;
using Saldo360.Application.Accounts;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.MonthlyPlan;

namespace Saldo360.Application.Dashboard.Overview;

// T11.2: monta o bloco "BudgetControl" da Visão Geral a partir dos MESMOS fatos do Plano × Realizado (consumo por competência, cartões pela fatura), da meta configurada do orçamento,
// da provisão alimentar e das metas de categoria/fixas já lidas. Somente leitura: nenhuma meta é gravada, presumida ou aplicada.

public sealed partial class DashboardOverviewService
{
    private static decimal Dec(string? value) => value is null ? 0m : decimal.Parse(value, CultureInfo.InvariantCulture);

    // Razões que descrevem incompletude de DADOS; as de "meta não definida/atribuível" são tratadas pelo estado do grupo.
    private static readonly HashSet<string> TargetReasons = [PlanActualCalculator.Reasons.PlanNotDefined, PlanActualCalculator.Reasons.PlanNotAttributable, PlanActualCalculator.Reasons.NoCards];

    /// <summary>Provisão alimentar aplicada: a parte da reserva (submeta dos cartões) ainda não coberta pelos gastos conhecidos e que cabe no espaço da meta dos cartões. Só do mês corrente em diante.</summary>
    private async Task<(decimal Applied, decimal? Reserve)> FoodForecastAsync(Guid workspaceId, DateOnly first, DateOnly today, decimal cardBase, decimal? cardTarget, CancellationToken ct)
    {
        var food = await monthlyPlan.FoodProvisionAsync(workspaceId, first, first, ct);
        if (!food.Applicable) return (0m, null);
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        var applied = first >= currentMonth ? ProjectionScenarios.FoodForecastWithinCardTarget(food.AmountFor(first), food.Known.GetValueOrDefault(first), cardBase, cardTarget) : 0m;
        return (applied, food.AmountFor(first));
    }

    /// <summary>Consumo da competência que alimenta o controle: o do Plano × Realizado de uma conta ou do consolidado, ou a soma conta a conta das contas PF (orçamento PF).</summary>
    private sealed record BudgetTotals(decimal CardsConfirmed, decimal CardsPending, decimal CardsEstimated, decimal Realized, decimal Pending, decimal FinanceCharges, bool CardsApplicable,
        IReadOnlyList<string> CardReasons, IReadOnlyList<string> ConsumptionReasons);

    private static BudgetTotals TotalsOf(PlanActualDashboardDto dashboard) =>
        new(Dec(dashboard.Cards.Confirmed), Dec(dashboard.Cards.PendingKnown), Dec(dashboard.Cards.Estimated), Dec(dashboard.Expenses.Consumption.Realized), Dec(dashboard.Expenses.Consumption.Pending),
            Dec(dashboard.Cards.FinanceCharges), dashboard.Cards.Applicable, dashboard.Cards.Reasons, dashboard.Expenses.Consumption.Reasons);

    /// <summary>
    /// <paramref name="pfAccounts"/> nulo = escopo bancário escolhido (conta ou consolidado, como antes). Com a lista, é o orçamento PF: a soma do Plano × Realizado de cada conta PF (<c>FinancialScope</c>), com os
    /// cartões pela conta que paga a fatura; cartão sem pagador não é atribuído a ninguém e a cobertura sai parcial. As metas são as do orçamento, sempre atribuíveis.
    /// </summary>
    private async Task<BudgetControlDto?> BuildBudgetControlAsync(Guid workspaceId, Guid userId, string month, string requestedScope, FinancialAccountDto? selected, string kind, DateOnly today,
        OverviewBudgetDto budget, OverviewClassificationDto classification, FinancialDecisionDto? decision, CancellationToken ct, IReadOnlyList<FinancialAccountDto>? pfAccounts = null)
    {
        try
        {
            var first = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
            var last = first.AddMonths(1).AddDays(-1);
            var attributable = pfAccounts is not null || selected is null;
            BudgetTotals totals;
            if (pfAccounts is null)
            {
                var plan = await PlanOnceAsync(workspaceId, userId, month, requestedScope, selected?.Id, ct);
                if (plan.Value is not { } dashboard) return null;
                totals = TotalsOf(dashboard);
            }
            else
            {
                if (pfAccounts.Count == 0) return null;
                var parts = new List<BudgetTotals>(); var incompleteInactive = false;
                foreach (var account in pfAccounts)
                {
                    var plan = await PlanOnceAsync(workspaceId, userId, month, PlanActualScopes.Account, account.Id, ct);
                    if (plan.Value is { } dashboard) parts.Add(TotalsOf(dashboard));
                    else if (account.IsActive) return null; // sem uma conta ativa o total PF seria parcial sem aviso: indisponível
                    else incompleteInactive = true; // conta inativa sem dados consolidáveis: o total sai marcado como parcial
                }
                if (parts.Count == 0) return null;
                var unpaid = (await cardRepository.ListAsync(workspaceId, includeArchived: false, ct)).Any(c => c.DefaultPaymentAccountId is null);
                var cardReasons = parts.SelectMany(p => p.CardReasons).Distinct().ToList();
                if (unpaid) cardReasons.Add(PlanActualCalculator.Reasons.MissingPayerAccount);
                totals = new(parts.Sum(p => p.CardsConfirmed), parts.Sum(p => p.CardsPending), parts.Sum(p => p.CardsEstimated), parts.Sum(p => p.Realized), parts.Sum(p => p.Pending), parts.Sum(p => p.FinanceCharges),
                    parts.Any(p => p.CardsApplicable), cardReasons.Distinct().ToList(), parts.SelectMany(p => p.ConsumptionReasons).Concat(incompleteInactive ? [PlanActualCalculator.Reasons.PartialPeriod] : []).Distinct().ToList());
            }

            var cardsConfirmed = totals.CardsConfirmed; var cardsPending = totals.CardsPending; var cardsEstimated = totals.CardsEstimated;
            // Os dois grupos são exclusivos: o consumo do cartão (pela fatura) e todo o resto. O pagamento de fatura já está fora do consumo, então não se conta de novo.
            var outsideRealized = totals.Realized - cardsConfirmed;
            var outsideCommitted = totals.Pending - cardsPending - cardsEstimated;
            var cardReasonsList = totals.CardReasons.Where(r => !TargetReasons.Contains(r)).Distinct().ToList();
            var outsideReasons = totals.ConsumptionReasons.Where(r => !TargetReasons.Contains(r) && r != PlanActualCalculator.Reasons.CardCoverageIncomplete).Distinct().ToList();

            decimal? totalTarget = null, cardTarget = null;
            string? totalOrigin = null, totalFrom = null;
            if (attributable)
            {
                var basis = await planBasis.LoadBasisAsync(workspaceId, first, null, ct);
                totalTarget = basis.AggregateTotalExpenses; cardTarget = basis.CardBudgetTarget;
                // T12.2: de onde vem a meta total (herdada do padrão, personalizada no mês, registro mensal anterior ou não definida). Só informação: o valor continua vindo do resolvedor único.
                var total = (await effectiveBudgets.BuildAsync(workspaceId, first, ct)).Targets.FirstOrDefault(t => t.Target == nameof(Saldo360.Domain.Budgets.BudgetRuleTarget.TotalExpenses));
                totalOrigin = total?.Origin ?? Saldo360.Application.Budgets.TargetOrigin.Undefined; totalFrom = total?.StandardFrom;
            }

            var (foodComplement, _) = await FoodForecastAsync(workspaceId, first, today, cardsConfirmed + cardsPending + cardsEstimated, attributable ? cardTarget : null, ct);

            var overTargets = new List<BudgetOverTarget>();
            if (budget.Fixed is { } fixedTarget && Dec(fixedTarget.Exceeded) > 0m) overTargets.Add(new("Fixed", "Despesas fixas", null, Dec(fixedTarget.Exceeded), Dec(fixedTarget.Planned), fixedTarget.State != "Complete"));
            overTargets.AddRange(budget.Categories.Targets.Where(t => Dec(t.Exceeded) > 0m).Select(t => new BudgetOverTarget("Category", t.Name, t.CategoryId, Dec(t.Exceeded), Dec(t.Planned), t.State != "Complete")));

            decimal overdueIncome = 0m;
            try
            {
                // A receita atrasada de uma conta é contexto bancário: não faz parte do orçamento PF.
                if (pfAccounts is null && (await calendar.ListAsync(workspaceId, userId, new(first, last, selected?.Id, null), ct)).Calendar is { } data) overdueIncome = data.Summary.OverdueIncome;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* a receita atrasada é informativa: sem ela o fator simplesmente não aparece */ }

            var forecast = await monthlyPlan.LoadIncomeForecastAsync(workspaceId, first, last, today, ct);
            var forecastApplies = attributable || selected?.Id == forecast.Settings?.AccountId;
            var unconfirmed = pfAccounts is null && forecastApplies && (forecast.Effective || forecast.Inferred)
                && forecast.Occurrences.Any(o => o.PaymentDate >= first && o.PaymentDate <= last && o.Status is IncomeForecastStatus.Forecast or IncomeForecastStatus.PartiallyReceived);

            var period = FinancialDecisionCalculator.Period(first, last, today);
            var input = new BudgetControlInput(month, period, kind, attributable, totalTarget, cardTarget, totals.CardsApplicable, cardsConfirmed, cardsPending, cardsEstimated, foodComplement, cardReasonsList,
                outsideRealized, outsideCommitted, outsideReasons, totals.FinanceCharges, overTargets, pfAccounts is null ? Dec(decision?.Commitments.Overdue) : 0m, overdueIncome,
                classification.State == "Complete" ? classification.NeedsReview + classification.NotEvaluated : 0, unconfirmed, pfAccounts is null && decision?.Context.OverdraftUsed is { } used ? Dec(used) : null);
            // Horário verificável da origem: a sincronização mais antiga das contas integradas do conjunto (mesma regra de Decision.Context.LastSyncedAt). Sem integração, nulo: nunca inventado.
            var scoped = pfAccounts ?? (selected is null ? [] : [selected]);
            var synced = scoped.Select(a => a.Integration?.LastSyncedAt).Where(v => v is not null).Select(v => v!.Value).ToList();
            return BudgetControlCalculator.Build(input) with { TotalTargetOrigin = totalOrigin, TotalTargetFrom = totalFrom, DataUpdatedAt = synced.Count > 0 ? synced.Min() : null };
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // complementar: nunca derruba os demais blocos
        }
    }
}

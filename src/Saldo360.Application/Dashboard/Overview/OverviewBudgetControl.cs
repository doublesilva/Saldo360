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

    private async Task<BudgetControlDto?> BuildBudgetControlAsync(Guid workspaceId, Guid userId, string month, string requestedScope, FinancialAccountDto? selected, string kind, DateOnly today,
        OverviewBudgetDto budget, OverviewClassificationDto classification, FinancialDecisionDto? decision, CancellationToken ct)
    {
        try
        {
            var first = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
            var last = first.AddMonths(1).AddDays(-1);
            var plan = await planActual.GetAsync(workspaceId, userId, month, requestedScope, selected?.Id, ct);
            if (plan.Value is not { } dashboard) return null;
            var attributable = selected is null;

            var cards = dashboard.Cards; var consumption = dashboard.Expenses.Consumption;
            var cardsConfirmed = Dec(cards.Confirmed); var cardsPending = Dec(cards.PendingKnown); var cardsEstimated = Dec(cards.Estimated);
            // Os dois grupos são exclusivos: o consumo do cartão (pela fatura) e todo o resto. O pagamento de fatura já está fora do consumo, então não se conta de novo.
            var outsideRealized = Dec(consumption.Realized) - cardsConfirmed;
            var outsideCommitted = Dec(consumption.Pending) - cardsPending - cardsEstimated;
            var cardReasons = cards.Reasons.Where(r => !TargetReasons.Contains(r)).Distinct().ToList();
            var outsideReasons = consumption.Reasons.Where(r => !TargetReasons.Contains(r) && r != PlanActualCalculator.Reasons.CardCoverageIncomplete).Distinct().ToList();

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

            // Provisão alimentar: valor estimado complementar dos cartões (o que falta para a provisão além das compras já categorizadas). Só do mês corrente em diante.
            var food = await monthlyPlan.FoodProvisionAsync(workspaceId, first, first, ct);
            var currentMonth = new DateOnly(today.Year, today.Month, 1);
            var foodComplement = food.Applicable && first >= currentMonth ? ProjectionScenarios.FoodComplement(food.AmountFor(first), food.Known.GetValueOrDefault(first)) : 0m;

            var overTargets = new List<BudgetOverTarget>();
            if (budget.Fixed is { } fixedTarget && Dec(fixedTarget.Exceeded) > 0m) overTargets.Add(new("Fixed", "Despesas fixas", null, Dec(fixedTarget.Exceeded), Dec(fixedTarget.Planned), fixedTarget.State != "Complete"));
            overTargets.AddRange(budget.Categories.Targets.Where(t => Dec(t.Exceeded) > 0m).Select(t => new BudgetOverTarget("Category", t.Name, t.CategoryId, Dec(t.Exceeded), Dec(t.Planned), t.State != "Complete")));

            decimal overdueIncome = 0m;
            try
            {
                var agenda = await calendar.ListAsync(workspaceId, userId, new(first, last, selected?.Id, null), ct);
                if (agenda.Calendar is { } data) overdueIncome = data.Summary.OverdueIncome;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* a receita atrasada é informativa: sem ela o fator simplesmente não aparece */ }

            var forecast = await monthlyPlan.LoadIncomeForecastAsync(workspaceId, first, last, today, ct);
            var forecastApplies = attributable || selected?.Id == forecast.Settings?.AccountId;
            var unconfirmed = forecastApplies && (forecast.Effective || forecast.Inferred)
                && forecast.Occurrences.Any(o => o.PaymentDate >= first && o.PaymentDate <= last && o.Status is IncomeForecastStatus.Forecast or IncomeForecastStatus.PartiallyReceived);

            var period = FinancialDecisionCalculator.Period(first, last, today);
            var input = new BudgetControlInput(month, period, kind, attributable, totalTarget, cardTarget, cards.Applicable, cardsConfirmed, cardsPending, cardsEstimated, foodComplement, cardReasons,
                outsideRealized, outsideCommitted, outsideReasons, Dec(cards.FinanceCharges), overTargets, Dec(decision?.Commitments.Overdue), overdueIncome,
                classification.State == "Complete" ? classification.NeedsReview + classification.NotEvaluated : 0, unconfirmed, decision?.Context.OverdraftUsed is { } used ? Dec(used) : null);
            return BudgetControlCalculator.Build(input) with { TotalTargetOrigin = totalOrigin, TotalTargetFrom = totalFrom };
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // complementar: nunca derruba os demais blocos
        }
    }
}

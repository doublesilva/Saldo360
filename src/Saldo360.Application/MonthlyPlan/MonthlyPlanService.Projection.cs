using Saldo360.Application.Accounts;

namespace Saldo360.Application.MonthlyPlan;

public sealed partial class MonthlyPlanService
{
    public const int MaxProjectionMonths = 36;

    /// <summary>
    /// Projeção consolidada de 1 a 36 meses (Entrega 2) a partir da mesma base da Agenda: saldo de referência do Pluggy, receita PJ e repasse da Entrega 1, cartões e cheque especial.
    /// Uma data de referência é capturada por consulta. Os dois cenários usam os mesmos fatos; o Tetos só acrescenta o complemento de consumo até os tetos. Nada é gravado.
    /// </summary>
    public async Task<MonthlyPlanResult<FinancialProjectionDto>> ProjectionAsync(Guid workspaceId, Guid userId, string? from, int? months, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<FinancialProjectionDto>.NotFound;
        var today = Today;
        var first = new DateOnly(today.Year, today.Month, 1);
        if (!string.IsNullOrWhiteSpace(from) && !TryMonth(from, out first)) return MonthlyPlanResult<FinancialProjectionDto>.Invalid("from", "Use uma competência válida no formato yyyy-MM.");
        var count = months ?? 6;
        if (count is < 1 or > MaxProjectionMonths) return MonthlyPlanResult<FinancialProjectionDto>.Invalid("months", $"A quantidade de meses deve estar entre 1 e {MaxProjectionMonths}.");

        var all = await accounts.ListAsync(workspaceId, true, ct);
        var allCards = await cardProjection.ComputeWithBillsAsync(workspaceId, today, ct);
        var facts = new List<ScenarioMonthFacts>();
        var integrated = await IntegratedAsync(workspaceId, userId, first, first.AddMonths(count - 1), today, all, allCards, ct, facts);
        if (integrated is not { PfAccountId: { } pfId, PjAccountId: { } pjId }) return MonthlyPlanResult<FinancialProjectionDto>.NotFound;
        var pf = all.First(a => a.Id == pfId);
        var food = await repository.FoodProvisionAsync(workspaceId, first, first.AddMonths(count - 1), ct);
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        var foodComplements = facts.Select(f => food.Applicable && f.Month >= currentMonth ? ProjectionScenarios.FoodComplement(food.AmountFor(f.Month), food.Known.GetValueOrDefault(f.Month)) : 0m).ToList();
        for (var i = 0; i < facts.Count; i++) facts[i] = facts[i] with { FoodComplement = foodComplements[i] };
        var ceilings = ProjectionScenarios.Ceilings(facts);
        var referenceOpening = facts.Count > 0 ? facts[0].Opening : null;

        var current = new List<ProjectionMonthDto>(); var ceiling = new List<ProjectionMonthDto>();
        for (var i = 0; i < integrated.Months.Count; i++)
        {
            var m = integrated.Months[i]; var f = facts[i]; var c = ceilings[i];
            current.Add(Build(m, f, c, pf, referenceOpening, c.CurrentOpening, c.CurrentClosing, ceilingScenario: false));
            ceiling.Add(Build(m, f, c, pf, referenceOpening, c.Opening ?? f.Opening, c.Closing, ceilingScenario: true));
        }
        var notes = new List<string>();
        if (!food.Applicable) notes.Add("FoodProvisionCategoriesNotConfigured");
        else { if (food.CategoriesInferred) notes.Add("FoodCategoriesInferred"); if (!food.AmountConfigured) notes.Add("FoodProvisionDefaultAmount"); notes.Add("FoodKnownFromCategorizedPurchasesOnly"); }
        if (ceilings.Any(x => x.CurrentClosing is { } v && v < 0m)) notes.Add("OverdraftChargesNotEstimated");
        return MonthlyPlanResult<FinancialProjectionDto>.Ok(new(Key(first), count, today, pjId, pfId, [new("Current", current), new("Ceilings", ceiling)], notes));
    }

    private static ProjectionMonthDto Build(IntegratedMonthDto m, ScenarioMonthFacts f, CeilingMonth c, FinancialAccountDto pf, decimal? referenceOpening, decimal? opening, decimal? closing, bool ceilingScenario)
    {
        var quality = m.Quality.ToList();
        if (ceilingScenario && !c.TargetsDefined) quality.Add("CeilingTargetsNotDefined");
        if (ceilingScenario && c.OutsideExcess > 0m) quality.Add("OutsideCardsCeilingExceeded");
        if (f.FoodComplement > 0m) quality.Add("FoodProvisionEstimated");
        if (opening is < 0m || closing is < 0m) quality.Add("OverdraftChargesNotEstimated");
        if (ceilingScenario && c.CardExcess > 0m) quality.Add("CardsCeilingExceeded");
        var cardComplement = ceilingScenario ? c.CardComplement : 0m; var outsideComplement = ceilingScenario ? c.OutsideComplement : 0m;
        var cardProjected = f.CardProjected + f.FoodComplement + cardComplement; var outsideProjected = f.OutsideKnown + outsideComplement;

        var pfDto = new ProjectionPfDto(MonthlyPlanCalculator.CentsOrNull(opening), m.Pf.Entries, MonthlyPlanCalculator.Cents(f.OutsideKnown), MonthlyPlanCalculator.Cents(outsideComplement),
            MonthlyPlanCalculator.Cents(f.CardProjected), MonthlyPlanCalculator.Cents(f.FoodComplement + cardComplement), m.Pf.CardsOutstanding, m.Pf.TransfersIn,
            opening is { } o && closing is { } cl ? MonthlyPlanCalculator.Cents(cl - o) : null, MonthlyPlanCalculator.CentsOrNull(closing));
        var budget = new ProjectionBudgetDto(
            MonthlyPlanCalculator.CentsOrNull(f.CardTarget), MonthlyPlanCalculator.Cents(cardProjected),
            f.CardTarget is { } cardTarget ? MonthlyPlanCalculator.Cents(Math.Max(0m, cardTarget - cardProjected)) : null,
            f.CardTarget is { } cardCeiling ? MonthlyPlanCalculator.Cents(Math.Max(0m, cardProjected - cardCeiling)) : null,
            MonthlyPlanCalculator.CentsOrNull(c.OutsideTarget), MonthlyPlanCalculator.Cents(outsideProjected),
            c.OutsideTarget is { } outsideTarget ? MonthlyPlanCalculator.Cents(Math.Max(0m, outsideTarget - outsideProjected)) : null,
            c.OutsideTarget is { } outsideCeiling ? MonthlyPlanCalculator.Cents(Math.Max(0m, outsideProjected - outsideCeiling)) : null);
        var overdraft = ProjectionScenarios.Overdraft(opening, closing, referenceOpening, pf.OverdraftLimit);
        var overdraftDto = overdraft is null ? null : new ProjectionOverdraftDto(MonthlyPlanCalculator.CentsOrNull(pf.OverdraftLimit), MonthlyPlanCalculator.Cents(overdraft.UsedStart),
            MonthlyPlanCalculator.Cents(overdraft.UsedEnd), MonthlyPlanCalculator.CentsOrNull(overdraft.Available), MonthlyPlanCalculator.Cents(overdraft.MonthlyRecovery),
            MonthlyPlanCalculator.Cents(overdraft.CumulativeRecovery), MonthlyPlanCalculator.Cents(overdraft.PositiveBalance));
        return new(m.Competence, m.PeriodKind, m.Income, m.Pj, m.Transfers, pfDto, budget, overdraftDto, quality.Count == 0, quality);
    }
}

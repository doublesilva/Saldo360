using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Dashboard.Overview;

// T11.2: consumo do orçamento, pressões e recomendações. Tudo calculado aqui (decimal) sobre os fatos que o Plano × Realizado, os cartões e o orçamento já entregam; nada recalcula
// fatura, saldo ou meta e nada é gravado. Valores monetários são texto decimal exato; "não definido" é null, nunca zero.

/// <summary>
/// Um dos dois grupos mutuamente exclusivos (<c>Cards</c> = todos os cartões, com a provisão alimentar; <c>OutsideCards</c> = o resto). Consumo é por COMPETÊNCIA (cartão pela fatura; pagamento de
/// fatura e transferências ficam fora). <c>State</c>: Complete, Partial (ressalvas em <c>Reasons</c>), TargetNotDefined, TargetNotAttributable (meta global não pertence a uma conta) ou NoCards.
/// <c>Status</c>: WithinBudget, Exceeded ou NoTarget. <c>ExcessRealized</c> = quanto o JÁ realizado passou da meta (preservado, nunca reduzido).
/// </summary>
public sealed record BudgetGroupDto(string Key, string State, string? Target, string Realized, string Committed, string Estimated, string Projected, string? Margin, string? Excess, string? ExcessRealized,
    string? RealizedPercent, string? ProjectedPercent, string Status, IReadOnlyList<string> Reasons);

public sealed record BudgetTotalDto(bool TargetDefined, string? Target, string Realized, string Committed, string Estimated, string Projected, string? Margin, string? Excess, string? RealizedPercent,
    string? ProjectedPercent, string Status);

/// <summary>
/// Um fator de pressão. <c>Amount</c> = desvio verificável (null = não estimável). <c>AmountKind</c>: OverTarget, Overdue, Charged ou NotEstimable. <c>Quality</c>: Confirmed (já realizado), Projected
/// (inclui comprometido), Estimated (inclui estimativa), Preliminary (fonte incompleta) ou NotEstimable. <c>Impact</c>: Budget, Cash, BudgetAndCash ou NotEstimable. Nada afirma causa nem saldo hipotético.
/// </summary>
public sealed record BudgetPressureDto(int Rank, string Kind, string? Subject, string? Amount, string AmountKind, string? ReferenceAmount, string ReferenceKind, string Impact, string Quality,
    string Action, string? ActionContext);

public sealed record BudgetRecommendationDto(string Code, string Action, string? ActionContext);

public sealed record BudgetControlDto(string State, string Competence, string Period, string ScopeKind, bool TargetsAttributable, BudgetTotalDto Total, BudgetGroupDto Cards, BudgetGroupDto OutsideCards,
    IReadOnlyList<BudgetPressureDto> Pressures, IReadOnlyList<BudgetRecommendationDto> Recommendations, IReadOnlyList<string> Notes,
    string? TotalTargetOrigin = null, string? TotalTargetFrom = null);

/// <summary>Meta de despesa fixa ou de categoria JÁ excedida (visões complementares: nunca somadas ao total).</summary>
public sealed record BudgetOverTarget(string Kind, string Name, Guid? CategoryId, decimal Exceeded, decimal Planned, bool Partial);

public sealed record BudgetControlInput(
    string Competence, string Period, string ScopeKind, bool Attributable, decimal? TotalTarget, decimal? CardTarget,
    bool CardsApplicable, decimal CardsRealized, decimal CardsCommitted, decimal CardsEstimated, decimal FoodComplement, IReadOnlyList<string> CardReasons,
    decimal OutsideRealized, decimal OutsideCommitted, IReadOnlyList<string> OutsideReasons,
    decimal CardFinanceCharges, IReadOnlyList<BudgetOverTarget> OverTargets, decimal OverdueExpenses, decimal OverdueIncome, int UncategorizedCount,
    bool IncomeEstimateUnconfirmed, decimal? OverdraftUsed);

public static class BudgetControlCalculator
{
    public const int MaxPressures = 5;
    private static string F(decimal value) => PlanActualCalculator.Cents(value);
    private static string? F(decimal? value) => PlanActualCalculator.Cents(value);

    // Prioridade fixa desempata fatores de mesmo valor: o ranking é determinístico.
    private static readonly string[] KindOrder = ["CardsOverTarget", "OutsideCardsOverTarget", "FixedExpensesOverTarget", "CategoryOverTarget", "OverdueExpenses", "CardFinanceCharges", "IncomeNotReceived", "OverdraftChargesNotEstimable"];

    private static BudgetGroupDto Group(string key, decimal? target, bool attributable, bool applicable, decimal realized, decimal committed, decimal estimated, IReadOnlyList<string> reasons)
    {
        var projected = realized + committed + estimated;
        var state = !applicable ? "NoCards" : !attributable ? "TargetNotAttributable" : target is null ? "TargetNotDefined" : reasons.Count > 0 ? "Partial" : "Complete";
        var hasTarget = attributable && target is not null;
        var t = hasTarget ? target : null;
        return new(key, state, F(t), F(realized), F(committed), F(estimated), F(projected),
            t is { } a ? F(Math.Max(0m, a - projected)) : null, t is { } b ? F(Math.Max(0m, projected - b)) : null, t is { } c ? F(Math.Max(0m, realized - c)) : null,
            t is > 0m ? PlanActualCalculator.Percent(realized * 100m / t.Value) : null, t is > 0m ? PlanActualCalculator.Percent(projected * 100m / t.Value) : null,
            t is null ? "NoTarget" : projected > t ? "Exceeded" : "WithinBudget", reasons);
    }

    public static BudgetControlDto Build(BudgetControlInput x)
    {
        var notes = new List<string>();
        var total = x.Attributable ? x.TotalTarget : null;
        var cardTarget = x.Attributable ? x.CardTarget : null;
        decimal? outsideTarget = null;
        if (total is { } whole && cardTarget is { } cards)
        {
            outsideTarget = Math.Max(0m, whole - cards);
            if (whole < cards) notes.Add("TotalBelowCardTarget");
        }
        if (!x.Attributable) notes.Add("TargetsGlobalOnly");
        // A provisão alimentar vale para o conjunto dos cartões; em uma conta isolada ela não é atribuível.
        var food = x.Attributable ? x.FoodComplement : 0m;
        var cardGroup = Group("Cards", cardTarget, x.Attributable, x.CardsApplicable, x.CardsRealized, x.CardsCommitted, x.CardsEstimated + food, x.CardReasons);
        var outsideGroup = Group("OutsideCards", outsideTarget, x.Attributable, true, x.OutsideRealized, x.OutsideCommitted, 0m, x.OutsideReasons);

        var realized = x.CardsRealized + x.OutsideRealized; var committed = x.CardsCommitted + x.OutsideCommitted; var estimated = x.CardsEstimated + food;
        var projected = realized + committed + estimated;
        var totalDto = new BudgetTotalDto(total is not null, F(total), F(realized), F(committed), F(estimated), F(projected),
            total is { } a ? F(Math.Max(0m, a - projected)) : null, total is { } b ? F(Math.Max(0m, projected - b)) : null,
            total is > 0m ? PlanActualCalculator.Percent(realized * 100m / total.Value) : null, total is > 0m ? PlanActualCalculator.Percent(projected * 100m / total.Value) : null,
            total is null ? "NoTarget" : projected > total ? "Exceeded" : "WithinBudget");

        var pressures = Pressures(x, cardTarget, outsideTarget, cardGroup, outsideGroup);
        var recommendations = Recommendations(x, total, cardTarget, projected, cardGroup, outsideGroup);
        var partial = cardGroup.State == "Partial" || outsideGroup.State == "Partial" || x.CardReasons.Count > 0 || x.OutsideReasons.Count > 0;
        var state = total is null && x.Attributable ? "TargetNotDefined" : partial ? "Partial" : "Complete";
        return new(state, x.Competence, x.Period, x.ScopeKind, x.Attributable, totalDto, cardGroup, outsideGroup, pressures, recommendations, notes);
    }

    private static string Quality(BudgetGroupDto group, decimal estimatedPart)
    {
        if (group.State == "Partial") return "Preliminary";
        if (group.ExcessRealized is { } real && decimal.Parse(real, System.Globalization.CultureInfo.InvariantCulture) > 0m) return "Confirmed";
        return estimatedPart > 0m ? "Estimated" : "Projected";
    }

    private static List<BudgetPressureDto> Pressures(BudgetControlInput x, decimal? cardTarget, decimal? outsideTarget, BudgetGroupDto cards, BudgetGroupDto outside)
    {
        var items = new List<(string Kind, string? Subject, decimal? Amount, BudgetPressureDto Dto)>();
        void Add(string kind, string? subject, decimal? amount, string amountKind, decimal? reference, string referenceKind, string impact, string quality, string action, string? context) =>
            items.Add((kind, subject, amount, new(0, kind, subject, F(amount), amountKind, F(reference), referenceKind, impact, quality, action, context)));

        if (cardTarget is { } ct && decimal.Parse(cards.Excess ?? "0", System.Globalization.CultureInfo.InvariantCulture) is var cardExcess and > 0m)
            Add("CardsOverTarget", null, cardExcess, "OverTarget", ct, "CardsTarget", "Budget", Quality(cards, decimal.Parse(cards.Estimated, System.Globalization.CultureInfo.InvariantCulture)), "OpenCards", null);
        if (outsideTarget is { } ot && decimal.Parse(outside.Excess ?? "0", System.Globalization.CultureInfo.InvariantCulture) is var outsideExcess and > 0m)
            Add("OutsideCardsOverTarget", null, outsideExcess, "OverTarget", ot, "OutsideCardsTarget", "Budget", Quality(outside, 0m), "OpenBudget", "fixed");
        if (x.Attributable)
            foreach (var over in x.OverTargets.Where(o => o.Exceeded > 0m))
                Add(over.Kind == "Fixed" ? "FixedExpensesOverTarget" : "CategoryOverTarget", over.Name, over.Exceeded, "OverTarget", over.Planned, over.Kind == "Fixed" ? "FixedTarget" : "CategoryTarget",
                    "Budget", over.Partial ? "Preliminary" : "Confirmed", "OpenBudget", over.Kind == "Fixed" ? "fixed" : "categories");
        if (x.OverdueExpenses > 0m) Add("OverdueExpenses", null, x.OverdueExpenses, "Overdue", null, "None", "Cash", "Confirmed", "OpenAgenda", null);
        if (x.CardFinanceCharges > 0m) Add("CardFinanceCharges", null, x.CardFinanceCharges, "Charged", null, "None", "BudgetAndCash", "Confirmed", "OpenCards", null);
        if (x.OverdueIncome > 0m) Add("IncomeNotReceived", null, x.OverdueIncome, "Overdue", null, "None", "Cash", "Confirmed", "OpenAgenda", null);
        if (x.Period != "Closed" && x.OverdraftUsed is > 0m) Add("OverdraftChargesNotEstimable", null, null, "NotEstimable", null, "None", "NotEstimable", "NotEstimable", "OpenAccounts", null);

        // Valor verificável maior primeiro; sem valor por último; empate pela prioridade fixa e, depois, pelo nome.
        return items.OrderBy(i => i.Amount is null ? 1 : 0).ThenByDescending(i => i.Amount ?? 0m).ThenBy(i => Array.IndexOf(KindOrder, i.Kind))
            .ThenBy(i => i.Subject, StringComparer.CurrentCultureIgnoreCase).Take(MaxPressures).Select((i, index) => i.Dto with { Rank = index + 1 }).ToList();
    }

    private static List<BudgetRecommendationDto> Recommendations(BudgetControlInput x, decimal? total, decimal? cardTarget, decimal projected, BudgetGroupDto cards, BudgetGroupDto outside)
    {
        var list = new List<BudgetRecommendationDto>();
        if (x.Attributable && total is null) list.Add(new("BudgetTotalNotDefined", "OpenBudget", null));
        else if (x.Attributable && cardTarget is null && x.CardsApplicable) list.Add(new("CardTargetNotDefined", "OpenBudget", "cards"));
        if (cards.Status == "Exceeded" && cards.Target is not null) list.Add(new("CardsOverTarget", "OpenCards", null));
        if (outside.Status == "Exceeded" && outside.Target is not null) list.Add(new("OutsideCardsOverTarget", "OpenBudget", "fixed"));
        if (total is { } whole && projected > whole) list.Add(new("ProjectedOverTotal", "OpenPlanning", null));
        if (x.UncategorizedCount > 0) list.Add(new("UncategorizedExpenses", "OpenReview", null));
        if (x.IncomeEstimateUnconfirmed) list.Add(new("IncomeEstimateUnconfirmed", "OpenPlanning", null));
        if (x.OverdueExpenses > 0m) list.Add(new("OverdueExpenses", "OpenAgenda", null));
        if (x.CardReasons.Count > 0) list.Add(new("CardCoverageIncomplete", "OpenCards", null));
        return list;
    }
}

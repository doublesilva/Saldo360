using Saldo360.Application.Budgets;
using Saldo360.Application.Categories;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using System.Globalization;

namespace Saldo360.Application.Dashboard.Overview;

// Previsão de despesas do orçamento PF por categoria (dentro da Meta total do card Orçamento mensal), sob demanda. Reaproveita a lista de fatos do orçamento (BudgetFactReader), agrupada por categoria
// principal, e concilia com a referência pfBudgetControl.total (realizado, comprometido, estimado): o que os fatos não discriminam fica como componente explícito, nunca absorvido por uma categoria.
// Somente leitura; nenhum lançamento, categoria ou estimativa fictícia é criado.

/// <summary>Uma subcategoria (ou a própria principal, sem subcategoria) dentro de uma categoria principal.</summary>
public sealed record BudgetCategoryChildDto(Guid? CategoryId, string Name, string Projected);

/// <summary>Uma perspectiva da composição de uma categoria. <c>Group</c>: Payment (Cards ou Bank) ou Nature (Fixed, Variable, Undefined). Cada grupo soma o total previsto da categoria: são visões, nunca somadas entre si.</summary>
public sealed record BudgetCategorySourceDto(string Group, string Key, string Amount);

/// <summary>
/// Uma linha da previsão. <c>Kind</c>: Category, Others ("Outras categorias", só apresentação) ou Uncategorized ("Sem categoria"). <c>Realized</c> = despesas efetivadas (inclui compras de cartão já lançadas),
/// <c>Committed</c> = compromissos ainda pendentes (previsões, recorrências, parcelas conhecidas), <c>Estimated</c> = 0 por categoria (estimativas não têm categoria: ficam em <c>UnallocatedEstimated</c>).
/// </summary>
public sealed record BudgetCategoryLineDto(Guid? CategoryId, string Name, string Kind, int CategoryCount, string Realized, string Committed, string Estimated, string Projected,
    IReadOnlyList<BudgetCategoryChildDto> Children, IReadOnlyList<BudgetCategorySourceDto> Sources);

/// <summary>
/// <c>Projected</c> = <c>pfBudgetControl.total.projected</c> (referência). Categorias + <c>Unallocated*</c> = a referência: <c>UnallocatedEstimated</c> são as estimativas sem categoria (cartões e reserva alimentar) e
/// <c>UnallocatedRealized</c>/<c>UnallocatedCommitted</c> o que os fatos categorizados não discriminam (total do banco de fatura fechada, dívida sem conta, etc.). Diferença nesses dois = <c>Partial</c> com o motivo.
/// <c>Categories</c> = as cinco maiores, "Outras categorias" e "Sem categoria"; <c>AllCategories</c> = todas, para "Ver todas".
/// </summary>
public sealed record BudgetCategoriesDto(string State, string Competence, string Scope, string? Target, string Realized, string Committed, string Estimated, string Projected,
    IReadOnlyList<BudgetCategoryLineDto> Categories, IReadOnlyList<BudgetCategoryLineDto> AllCategories, int CategoryCount,
    string UnallocatedRealized, string UnallocatedCommitted, string UnallocatedEstimated, IReadOnlyList<string> Reasons);

/// <summary>Totais de referência do orçamento PF (realizado, comprometido e estimado) para a conciliação.</summary>
public sealed record BudgetReference(decimal Realized, decimal Committed, decimal Estimated, decimal? Target);

/// <summary>Ocorrência de cartão calculada pelo motor (parcela conhecida/estimada, assinatura) já com a categoria da série. Só leitura: é a mesma ocorrência que o Razão e o pfBudgetControl enxergam.</summary>
public sealed record BudgetProjectedCardLine(Guid CardId, Guid CategoryId, decimal Amount, bool Committed);

public static class BudgetCategoryForecastCalculator
{
    public const int OverviewTop = 5;

    public static class Reasons
    {
        public const string RealizedNotDiscriminated = "RealizedNotDiscriminated";
        public const string CommittedNotDiscriminated = "CommittedNotDiscriminated";
        public const string ReferenceUnavailable = "ReferenceUnavailable";
    }

    private sealed record Group(Guid? Id, string Name, decimal Realized, decimal Committed, List<(Guid? Id, string Name, decimal Amount)> Children, decimal Card, decimal Bank, decimal Fixed, decimal Variable, decimal Undefined, decimal Estimated = 0m)
    {
        public decimal Projected => Realized + Committed + Estimated;
    }

    /// <summary>
    /// <paramref name="facts"/> = fatos do mês; entram os das contas PF (<paramref name="pfAccountIds"/>, inclusive inativas) e os de cartões pagos por conta PF (<paramref name="cardPayers"/>). Fato sem conta nem
    /// cartão (ex.: parcela de dívida pendente) não é atribuível ao escopo e fica na diferença da conciliação.
    /// </summary>
    public static BudgetCategoriesDto Build(string competence, IReadOnlyList<BudgetFact> facts, IReadOnlyDictionary<Guid, FinancialCategoryDto> categories, IReadOnlyDictionary<Guid, Guid> cardPayers,
        IReadOnlySet<Guid> pfAccountIds, BudgetReference? reference, int top = OverviewTop, IReadOnlyList<BudgetProjectedCardLine>? projectedCards = null)
    {
        bool InScope(BudgetFact f) => f.CardId is { } card ? cardPayers.TryGetValue(card, out var payer) && pfAccountIds.Contains(payer) : f.AccountId is { } account && pfAccountIds.Contains(account);
        bool IsCommitted(BudgetFact f) => f.State == BudgetFactState.Pending || f.Origin == BudgetFactOrigin.CardKnownInstallment;

        (Guid? Id, string Name) MainOf(Guid? categoryId, out (Guid? Id, string Name) leaf)
        {
            if (categoryId is not { } id) { leaf = (null, ExpenseDistributionCalculator.Uncategorized); return leaf; }
            if (!categories.TryGetValue(id, out var category)) { leaf = (id, ExpenseDistributionCalculator.RemovedCategory); return leaf; }
            leaf = (category.Id, category.Name);
            return category.ParentCategoryId is { } parent && categories.TryGetValue(parent, out var main) ? (main.Id, main.Name) : leaf;
        }

        var grouped = new Dictionary<(Guid?, string), Group>();
        foreach (var f in facts.Where(InScope))
        {
            var main = MainOf(f.CategoryId, out var leaf);
            if (!grouped.TryGetValue(main, out var g)) grouped[main] = g = new(main.Id, main.Name, 0m, 0m, [], 0m, 0m, 0m, 0m, 0m);
            var isCommitted = IsCommitted(f);
            g = g with { Realized = g.Realized + (isCommitted ? 0m : f.Amount), Committed = g.Committed + (isCommitted ? f.Amount : 0m),
                Card = g.Card + (f.IsCard ? f.Amount : 0m), Bank = g.Bank + (f.IsCard ? 0m : f.Amount),
                Fixed = g.Fixed + (f.Nature == ExpenseNature.Fixed ? f.Amount : 0m), Variable = g.Variable + (f.Nature == ExpenseNature.Variable ? f.Amount : 0m),
                Undefined = g.Undefined + (f.Nature == ExpenseNature.Undefined ? f.Amount : 0m) };
            g.Children.Add((leaf.Id, leaf.Name, f.Amount));
            grouped[main] = g;
        }

        // Parcelas e assinaturas de cartão ainda não faturadas que o motor projeta: a categoria vem da série já classificada (nenhuma chamada à IA aqui). Conhecida/fixa = comprometido; estimada = estimado.
        foreach (var line in (projectedCards ?? []).Where(l => cardPayers.TryGetValue(l.CardId, out var payer) && pfAccountIds.Contains(payer)))
        {
            var main = MainOf(line.CategoryId, out var leaf);
            if (!grouped.TryGetValue(main, out var g)) grouped[main] = g = new(main.Id, main.Name, 0m, 0m, [], 0m, 0m, 0m, 0m, 0m);
            grouped[main] = g with { Committed = g.Committed + (line.Committed ? line.Amount : 0m), Estimated = g.Estimated + (line.Committed ? 0m : line.Amount), Card = g.Card + line.Amount, Undefined = g.Undefined + line.Amount };
            g.Children.Add((leaf.Id, leaf.Name, line.Amount));
        }

        var all = grouped.Values.Where(g => g.Projected != 0m).ToList();
        var ranked = all.Where(g => g.Id is not null).OrderByDescending(g => g.Projected).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id).ToList();
        var uncategorized = all.FirstOrDefault(g => g.Id is null);

        BudgetCategoryLineDto Line(Group g, string kind, int count, string? name = null) => new(g.Id, name ?? g.Name, kind, count, Cents(g.Realized), Cents(g.Committed), Cents(g.Estimated), Cents(g.Projected),
            g.Children.GroupBy(c => (c.Id, c.Name)).Select(c => (c.Key.Id, c.Key.Name, Amount: c.Sum(x => x.Amount))).Where(c => c.Amount != 0m)
                .OrderByDescending(c => c.Amount).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).Select(c => new BudgetCategoryChildDto(c.Id, c.Name, Cents(c.Amount))).ToList(),
            [new("Payment", "Cards", Cents(g.Card)), new("Payment", "Bank", Cents(g.Bank)), new("Nature", "Fixed", Cents(g.Fixed)), new("Nature", "Variable", Cents(g.Variable)), new("Nature", "Undefined", Cents(g.Undefined))]);
        Group Merge(IEnumerable<Group> items, string name) => items.Aggregate(new Group(null, name, 0m, 0m, [], 0m, 0m, 0m, 0m, 0m), (a, g) => { a.Children.AddRange(g.Children);
            return a with { Realized = a.Realized + g.Realized, Committed = a.Committed + g.Committed, Estimated = a.Estimated + g.Estimated, Card = a.Card + g.Card, Bank = a.Bank + g.Bank, Fixed = a.Fixed + g.Fixed, Variable = a.Variable + g.Variable, Undefined = a.Undefined + g.Undefined }; });

        var allLines = ranked.Select(g => Line(g, "Category", 1)).ToList();
        if (uncategorized is not null) allLines.Add(Line(uncategorized, "Uncategorized", 0));
        var shown = ranked.Count > top ? ranked.Take(top).ToList() : ranked;
        var rows = shown.Select(g => Line(g, "Category", 1)).ToList();
        var rest = ranked.Skip(shown.Count).ToList();
        if (rest.Count > 0) rows.Add(Line(Merge(rest, ExpenseDistributionCalculator.OtherCategories), "Others", rest.Count));
        if (uncategorized is not null) rows.Add(Line(uncategorized, "Uncategorized", 0));

        var realized = all.Sum(g => g.Realized); var committed = all.Sum(g => g.Committed); var estimatedAllocated = all.Sum(g => g.Estimated);
        var reasons = new List<string>();
        decimal unallocatedRealized = 0m, unallocatedCommitted = 0m, unallocatedEstimated = 0m;
        string projected;
        if (reference is null) { reasons.Add(Reasons.ReferenceUnavailable); projected = Cents(realized + committed + estimatedAllocated); }
        else
        {
            unallocatedRealized = reference.Realized - realized; unallocatedCommitted = reference.Committed - committed; unallocatedEstimated = reference.Estimated - estimatedAllocated;
            if (unallocatedRealized != 0m) reasons.Add(Reasons.RealizedNotDiscriminated);
            if (unallocatedCommitted != 0m) reasons.Add(Reasons.CommittedNotDiscriminated);
            projected = Cents(reference.Realized + reference.Committed + reference.Estimated);
        }
        var partial = reasons.Count > 0;
        return new(partial ? "Partial" : "Complete", competence, "PF", reference?.Target is { } t ? Cents(t) : null, Cents(reference?.Realized ?? realized), Cents(reference?.Committed ?? committed), Cents(reference?.Estimated ?? estimatedAllocated), projected,
            rows, allLines, ranked.Count, Cents(unallocatedRealized), Cents(unallocatedCommitted), Cents(unallocatedEstimated), reasons);
    }

    private static string Cents(decimal value) => PlanActualCalculator.Cents(value);
}

public sealed partial class DashboardOverviewService
{
    public async Task<PlanActualResult<BudgetCategoriesDto>> GetBudgetCategoriesAsync(Guid workspaceId, Guid userId, string? competence, CancellationToken ct)
    {
        var resolved = await ResolveAsync(workspaceId, userId, competence, null, null, ct);
        if (resolved.Error is { } error) return new(error.Access, null, error.Errors);
        var (month, today) = (resolved.Month!, resolved.Today);
        var first = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
        var pfAccounts = (await accountService.ListAsync(workspaceId, userId, includeInactive: true, ct) ?? resolved.All).Where(a => a.FinancialScope == Saldo360.Domain.Accounts.FinancialScope.PF).ToList();

        var (facts, _) = await budgetFacts.ReadFactsAsync(workspaceId, first, first.AddMonths(1), ct);
        var payers = (await cardRepository.ListAsync(workspaceId, includeArchived: true, ct)).Where(c => c.DefaultPaymentAccountId is not null).ToDictionary(c => c.Id, c => c.DefaultPaymentAccountId!.Value);
        var categories = (await categoryRepository.ListAsync(workspaceId, includeInactive: true, ct)).ToDictionary(c => c.Id);
        // Referência: o mesmo pfBudgetControl do card (realizado + comprometido + estimado = total previsto).
        var control = await BuildBudgetControlAsync(workspaceId, userId, month, PlanActualScopes.All, null, "pf", today, new("Complete", false, null, month, null, null, NoCategories()), new("Unavailable", 0, 0), null, ct, pfAccounts);
        BudgetReference? reference = control is null ? null : new(Dec(control.Total.Realized), Dec(control.Total.Committed), Dec(control.Total.Estimated), control.Total.Target is null ? null : Dec(control.Total.Target));
        // Ocorrências do motor de cartões com categoria da série (leitura do que já foi classificado; o Dashboard nunca chama a IA).
        var projectedCards = (await cardProjection.ComputeAsync(workspaceId, today, null, ct)).SelectMany(p => p.Occurrences ?? [])
            .Where(o => o.Competence == month && o.CategoryId is not null && o.Kind is "KnownInstallment" or "EstimatedInstallment" or "InvoiceFinancing" or "Recurring" or "ManualRecurring")
            .Select(o => new BudgetProjectedCardLine(o.CardId, o.CategoryId!.Value, o.Amount, o.Quality == "Known")).ToList();
        return PlanActualResult<BudgetCategoriesDto>.Ok(BudgetCategoryForecastCalculator.Build(month, facts, categories, payers, pfAccounts.Select(a => a.Id).ToHashSet(), reference, projectedCards: projectedCards));
    }
}

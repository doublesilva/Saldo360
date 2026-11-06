using Saldo360.Application.Budgets;
using Saldo360.Application.Categories;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Domain.Common;
using System.Globalization;

namespace Saldo360.Application.Dashboard.Overview;

// Dashboard Folio: para onde foi o dinheiro do mês. Agrupa a MESMA lista de fatos do orçamento (BudgetFactReader) por categoria principal; nenhum outro motor de despesas.
// Somente o consumo realizado da competência: despesas efetivas (sem pagamento de fatura nem de parcela de dívida), devoluções pelo valor líquido, pagamentos de dívida e
// compras/encargos/créditos do cartão na competência da fatura. Previsões e saldos pendentes ficam de fora. Somente leitura.

/// <summary>
/// Uma linha da distribuição. <c>Kind</c>: Category (a categoria principal, com as subcategorias), Others ("Outras categorias": as que passam das maiores exibidas, só apresentação;
/// <c>CategoryCount</c> diz quantas) ou Uncategorized ("Sem categoria", sempre separada e por último). <c>Percentage</c> = parte do total com uma casa decimal, distribuída pelo maior resto para
/// que as linhas somem exatamente 100,0; nulo quando o total líquido não permite percentuais representativos (ver <c>PercentagesNotRepresentative</c>).
/// </summary>
public sealed record ExpenseCategoryShareDto(Guid? CategoryId, string Name, string Amount, string? Percentage, string Kind = ExpenseDistributionCalculator.Kinds.Category, int CategoryCount = 1);

/// <summary>
/// <c>State</c>: Complete, Partial (o total não confere com "Despesas do mês" ou a referência não pôde ser lida: <c>Reasons</c>) ou Unavailable (<c>Total</c> nulo, sem linhas).
/// <c>Reference</c> = despesas realizadas por competência do mesmo escopo (Plano × Realizado), usada só para conferir o total; nada é ajustado para fechar a conta.
/// <c>CategoryCount</c> = categorias principais com gasto (sem contar "Sem categoria"), mesmo quando parte delas está agrupada em "Outras categorias".
/// </summary>
public sealed record ExpenseDistributionDto(string State, string Competence, string? Total, string? Reference, IReadOnlyList<ExpenseCategoryShareDto> Categories, IReadOnlyList<string> Reasons,
    int CategoryCount = 0);

public static class ExpenseDistributionCalculator
{
    public const string Uncategorized = "Sem categoria";
    public const string RemovedCategory = "Categoria removida";
    public const string OtherCategories = "Outras categorias";
    /// <summary>Quantas categorias a Visão Geral mostra antes de agrupar o resto em "Outras categorias".</summary>
    public const int OverviewTop = 5;

    public static class Kinds
    {
        public const string Category = "Category";
        public const string Others = "Others";
        public const string Uncategorized = "Uncategorized";
    }

    public static class Reasons
    {
        public const string TotalDiffersFromExpenses = "TotalDiffersFromExpenses";
        public const string ReferenceUnavailable = "ReferenceUnavailable";
        public const string PercentagesNotRepresentative = "PercentagesNotRepresentative";
    }

    /// <summary>
    /// Distribui os fatos do mês. <paramref name="accountId"/> nulo = consolidado; senão, cartão conta pela conta que paga a fatura (<paramref name="cardPayers"/>, a mesma regra do
    /// Plano × Realizado) e os demais registros pela conta do lançamento. <paramref name="reference"/> = "Despesas do mês" do mesmo escopo. <paramref name="top"/> limita as categorias
    /// exibidas (o resto vira "Outras categorias"); nulo = todas.
    /// </summary>
    public static ExpenseDistributionDto Build(string competence, IReadOnlyList<BudgetFact> facts, IReadOnlyDictionary<Guid, FinancialCategoryDto> categories,
        IReadOnlyDictionary<Guid, Guid> cardPayers, Guid? accountId, string? reference, int? top = null)
    {
        // Parcela histórica conhecida do cartão é "pendente conhecida" em Despesas do mês: fica fora do realizado para os dois totais conferirem.
        var realized = facts.Where(f => f.State is BudgetFactState.Realized or BudgetFactState.CardLaunched && f.Origin != BudgetFactOrigin.CardKnownInstallment)
            .Where(f => accountId is null || (f.CardId is { } card ? cardPayers.TryGetValue(card, out var payer) && payer == accountId : f.AccountId == accountId)).ToList();

        (Guid? Id, string Name) MainOf(Guid? categoryId)
        {
            if (categoryId is not { } id) return (null, Uncategorized);
            if (!categories.TryGetValue(id, out var category)) return (id, RemovedCategory);
            return category.ParentCategoryId is { } parent && categories.TryGetValue(parent, out var main) ? (main.Id, main.Name) : (category.Id, category.Name);
        }

        var groups = realized.GroupBy(f => MainOf(f.CategoryId)).Select(g => (g.Key.Id, g.Key.Name, Amount: g.Sum(f => f.Amount)))
            .Where(g => g.Amount != 0m) // categoria cujas compras foram integralmente devolvidas não ocupa espaço
            .ToList();
        // Ordem determinística: maior valor líquido, depois nome e identificador (empates nunca trocam de lugar entre leituras).
        var ranked = groups.Where(g => g.Id is not null).OrderByDescending(g => g.Amount).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id).ToList();
        var uncategorized = groups.Where(g => g.Id is null).Select(g => g.Amount).DefaultIfEmpty(0m).Sum();

        var rows = new List<(Guid? Id, string Name, decimal Amount, string Kind, int Count)>();
        var shown = top is { } limit && ranked.Count > limit ? ranked.Take(limit).ToList() : ranked;
        rows.AddRange(shown.Select(g => (g.Id, g.Name, g.Amount, Kinds.Category, 1)));
        var rest = ranked.Skip(shown.Count).ToList();
        if (rest.Count > 0) rows.Add((null, OtherCategories, rest.Sum(g => g.Amount), Kinds.Others, rest.Count));
        if (uncategorized != 0m) rows.Add((null, Uncategorized, uncategorized, Kinds.Uncategorized, 0));
        var total = decimal.Round(realized.Sum(f => f.Amount), Money.MaxScale, MidpointRounding.AwayFromZero);

        var reasons = new List<string>();
        // Créditos maiores que as compras de uma linha (ou do mês) tornam "parte do total" sem sentido: os valores continuam exibidos, sem percentuais.
        var representative = total > 0m && rows.All(r => r.Amount > 0m);
        if (!representative && rows.Count > 0) reasons.Add(Reasons.PercentagesNotRepresentative);
        if (reference is null) reasons.Add(Reasons.ReferenceUnavailable);
        else if (decimal.Parse(reference, CultureInfo.InvariantCulture) != total) reasons.Add(Reasons.TotalDiffersFromExpenses);

        var percentages = representative ? Shares(rows.Select(r => r.Amount).ToList(), total) : null;
        var lines = rows.Select((r, index) => new ExpenseCategoryShareDto(r.Id, r.Name, PlanActualCalculator.Cents(r.Amount), percentages?[index], r.Kind, r.Count)).ToList();
        var partial = reasons.Any(r => r is Reasons.TotalDiffersFromExpenses or Reasons.ReferenceUnavailable);
        return new(partial ? "Partial" : "Complete", competence, PlanActualCalculator.Cents(total), reference, lines, reasons, ranked.Count);
    }

    /// <summary>
    /// Percentuais com uma casa que somam exatamente 100,0 (método do maior resto, em décimos): cada linha recebe o piso e os décimos que faltam vão para os maiores restos, na ordem
    /// das linhas em caso de empate. Só para valores positivos cuja soma é o total.
    /// </summary>
    public static IReadOnlyList<string> Shares(IReadOnlyList<decimal> amounts, decimal total)
    {
        var tenths = amounts.Select(a => a * 1000m / total).ToList();
        var floors = tenths.Select(decimal.Floor).ToList();
        var missing = (int)(1000m - floors.Sum());
        foreach (var index in tenths.Select((t, i) => (Remainder: t - floors[i], Index: i)).OrderByDescending(x => x.Remainder).ThenBy(x => x.Index).Take(missing).Select(x => x.Index)) floors[index] += 1m;
        return floors.Select(f => (f / 10m).ToString("0.0", CultureInfo.InvariantCulture)).ToList();
    }
}

public sealed partial class DashboardOverviewService
{
    private async Task<ExpenseDistributionDto> BuildExpenseDistributionAsync(Guid workspaceId, string month, Guid? accountId, OverviewCompetenceDto competence, int? top, CancellationToken ct)
    {
        try
        {
            var first = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
            var (facts, _) = await budgetFacts.ReadFactsAsync(workspaceId, first, first.AddMonths(1), ct);
            var payers = accountId is null ? new Dictionary<Guid, Guid>()
                : (await cardRepository.ListAsync(workspaceId, includeArchived: true, ct)).Where(c => c.DefaultPaymentAccountId is not null).ToDictionary(c => c.Id, c => c.DefaultPaymentAccountId!.Value);
            var categories = (await categoryRepository.ListAsync(workspaceId, includeInactive: true, ct)).ToDictionary(c => c.Id);
            return ExpenseDistributionCalculator.Build(month, facts, categories, payers, accountId, competence.Expenses, top);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Unavailable", month, null, competence.Expenses, [], []); // complementar: nunca derruba os demais blocos
        }
    }

    /// <summary>
    /// "Ver todas" da Visão Geral: a distribuição completa (todas as categorias com gasto) da mesma competência e escopo, sob demanda, para a página principal não carregar dezenas de linhas.
    /// Mesma validação, autorização e conferência com "Despesas do mês" do <c>overview</c>.
    /// </summary>
    public async Task<PlanActualResult<ExpenseDistributionDto>> GetExpenseDistributionAsync(Guid workspaceId, Guid userId, string? competence, string? scope, Guid? accountId, CancellationToken ct)
    {
        var resolved = await ResolveAsync(workspaceId, userId, competence, scope, accountId, ct);
        if (resolved.Error is { } error) return new(error.Access, null, error.Errors);
        var (month, requested, selected) = (resolved.Month!, resolved.Requested!, resolved.Selected);
        var byCompetence = await BuildCompetenceAsync(workspaceId, userId, month, requested, selected?.Id, ct);
        return PlanActualResult<ExpenseDistributionDto>.Ok(await BuildExpenseDistributionAsync(workspaceId, month, selected?.Id, byCompetence, null, ct));
    }
}

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
/// Uma fatia da distribuição: a categoria principal (com as subcategorias) ou, com <c>CategoryId</c> nulo, o grupo explícito "Sem categoria".
/// <c>Percentage</c> = parte do total com uma casa decimal; nulo quando o total líquido não permite percentuais representativos (ver <c>PercentagesNotRepresentative</c>).
/// </summary>
public sealed record ExpenseCategoryShareDto(Guid? CategoryId, string Name, string Amount, string? Percentage);

/// <summary>
/// <c>State</c>: Complete, Partial (o total não confere com "Despesas do mês" ou a referência não pôde ser lida: <c>Reasons</c>) ou Unavailable (<c>Total</c> nulo, sem fatias).
/// <c>Reference</c> = despesas realizadas por competência do mesmo escopo (Plano × Realizado), usada só para conferir o total; nada é ajustado para fechar a conta.
/// </summary>
public sealed record ExpenseDistributionDto(string State, string Competence, string? Total, string? Reference, IReadOnlyList<ExpenseCategoryShareDto> Categories, IReadOnlyList<string> Reasons);

public static class ExpenseDistributionCalculator
{
    public const string Uncategorized = "Sem categoria";
    public const string RemovedCategory = "Categoria removida";

    public static class Reasons
    {
        public const string TotalDiffersFromExpenses = "TotalDiffersFromExpenses";
        public const string ReferenceUnavailable = "ReferenceUnavailable";
        public const string PercentagesNotRepresentative = "PercentagesNotRepresentative";
    }

    /// <summary>
    /// Distribui os fatos do mês. <paramref name="accountId"/> nulo = consolidado; senão, cartão conta pela conta que paga a fatura (<paramref name="cardPayers"/>, a mesma regra do
    /// Plano × Realizado) e os demais registros pela conta do lançamento. <paramref name="reference"/> = "Despesas do mês" do mesmo escopo.
    /// </summary>
    public static ExpenseDistributionDto Build(string competence, IReadOnlyList<BudgetFact> facts, IReadOnlyDictionary<Guid, FinancialCategoryDto> categories,
        IReadOnlyDictionary<Guid, Guid> cardPayers, Guid? accountId, string? reference)
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
            .OrderByDescending(g => g.Amount).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var total = decimal.Round(realized.Sum(f => f.Amount), Money.MaxScale, MidpointRounding.AwayFromZero);

        var reasons = new List<string>();
        // Créditos maiores que as compras de uma categoria (ou do mês) tornam "parte do total" sem sentido: os valores continuam exibidos, sem percentuais.
        var representative = total > 0m && groups.All(g => g.Amount > 0m);
        if (!representative && groups.Count > 0) reasons.Add(Reasons.PercentagesNotRepresentative);
        if (reference is null) reasons.Add(Reasons.ReferenceUnavailable);
        else if (decimal.Parse(reference, CultureInfo.InvariantCulture) != total) reasons.Add(Reasons.TotalDiffersFromExpenses);

        var shares = groups.Select(g => new ExpenseCategoryShareDto(g.Id, g.Name, PlanActualCalculator.Cents(g.Amount), representative ? PlanActualCalculator.Percent(g.Amount * 100m / total) : null)).ToList();
        var partial = reasons.Any(r => r is Reasons.TotalDiffersFromExpenses or Reasons.ReferenceUnavailable);
        return new(partial ? "Partial" : "Complete", competence, PlanActualCalculator.Cents(total), reference, shares, reasons);
    }
}

public sealed partial class DashboardOverviewService
{
    private async Task<ExpenseDistributionDto> BuildExpenseDistributionAsync(Guid workspaceId, string month, Guid? accountId, OverviewCompetenceDto competence, CancellationToken ct)
    {
        try
        {
            var first = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
            var (facts, _) = await budgetFacts.ReadFactsAsync(workspaceId, first, first.AddMonths(1), ct);
            var payers = accountId is null ? new Dictionary<Guid, Guid>()
                : (await cardRepository.ListAsync(workspaceId, includeArchived: true, ct)).Where(c => c.DefaultPaymentAccountId is not null).ToDictionary(c => c.Id, c => c.DefaultPaymentAccountId!.Value);
            var categories = (await categoryRepository.ListAsync(workspaceId, includeInactive: true, ct)).ToDictionary(c => c.Id);
            return ExpenseDistributionCalculator.Build(month, facts, categories, payers, accountId, competence.Expenses);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Unavailable", month, null, competence.Expenses, [], []); // complementar: nunca derruba os demais blocos
        }
    }
}

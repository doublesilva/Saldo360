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
/// <c>CategoryCount</c> diz quantas) ou Uncategorized ("Sem categoria", sempre separada e por último). <c>Gross</c> = só os gastos (fatos positivos) da linha e é a base da barra e dos
/// percentuais; <c>Amount</c> = valor líquido (gastos menos devoluções e estornos dela), preservado. <c>Percentage</c> = parte do gasto bruto total, com uma casa decimal e distribuída pelo
/// maior resto para somar exatamente 100,0; nulo só quando não houve nenhum gasto.
/// </summary>
public sealed record ExpenseCategoryShareDto(Guid? CategoryId, string Name, string Amount, string? Percentage, string Kind = ExpenseDistributionCalculator.Kinds.Category, int CategoryCount = 1,
    string Gross = "0.00");

/// <summary>Um bloco da conferência com "Despesas do mês": o que a distribuição soma × o que o indicador soma, no mesmo escopo. <c>Bucket</c>: Bank (débitos, devoluções e dívidas) ou Cards (compras, encargos e créditos pela fatura).</summary>
public sealed record ExpenseReconciliationDto(string Bucket, string Distributed, string Reference, string Difference);

/// <summary>
/// <c>State</c>: Complete, Partial (o total não confere com "Despesas do mês" ou a referência não pôde ser lida: <c>Reasons</c>, <c>Reconciliation</c>) ou Unavailable (<c>Total</c> nulo, sem linhas).
/// <c>Total</c> = líquido (gastos menos <c>Adjustments</c>); <c>Gross</c> = só gastos; <c>Adjustments</c> = devoluções e estornos (negativo ou zero), mostrados à parte. <c>Reference</c> = despesas
/// realizadas por competência do mesmo escopo (Plano × Realizado); nada é ajustado para fechar a conta. <c>CategoryCount</c> = categorias principais com gasto, sem "Sem categoria".
/// </summary>
public sealed record ExpenseDistributionDto(string State, string Competence, string? Total, string? Reference, IReadOnlyList<ExpenseCategoryShareDto> Categories, IReadOnlyList<string> Reasons,
    int CategoryCount = 0, string Gross = "0.00", string Adjustments = "0.00", IReadOnlyList<ExpenseReconciliationDto>? Reconciliation = null);

/// <summary>Valores do indicador "Despesas do mês" por bloco (componentes <c>BankExpenses</c> e <c>CardPurchases</c> do Plano × Realizado).</summary>
public sealed record ExpenseReference(decimal Total, decimal Bank, decimal Cards);

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
        public const string BankBasisDiffers = "BankBasisDiffers";
        public const string CardBasisDiffers = "CardBasisDiffers";
    }

    private static bool IsCardBucket(BudgetFact f) => f.Origin is BudgetFactOrigin.CardInstallment or BudgetFactOrigin.CardAdjustment;

    /// <summary>
    /// Distribui os fatos do mês. <paramref name="accountId"/> nulo = consolidado; senão, cartão conta pela conta que paga a fatura (<paramref name="cardPayers"/>) e os demais registros pela conta do
    /// lançamento. <paramref name="reference"/> = "Despesas do mês" e seus blocos. <paramref name="top"/> limita as categorias exibidas (o resto vira "Outras categorias"); nulo = todas.
    /// A barra e os percentuais usam só os gastos; devoluções e estornos aparecem à parte e o total continua líquido.
    /// </summary>
    public static ExpenseDistributionDto Build(string competence, IReadOnlyList<BudgetFact> facts, IReadOnlyDictionary<Guid, FinancialCategoryDto> categories,
        IReadOnlyDictionary<Guid, Guid> cardPayers, Guid? accountId, ExpenseReference? reference, int? top = null)
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

        var groups = realized.GroupBy(f => MainOf(f.CategoryId))
            .Select(g => (g.Key.Id, g.Key.Name, Gross: g.Where(f => f.Amount > 0m).Sum(f => f.Amount), Net: g.Sum(f => f.Amount)))
            .Where(g => g.Gross > 0m) // sem gasto não há linha: a devolução dele continua no total e em "Devoluções e estornos"
            .ToList();
        // Ordem determinística: maior gasto, depois nome e identificador (empates nunca trocam de lugar entre leituras).
        var ranked = groups.Where(g => g.Id is not null).OrderByDescending(g => g.Gross).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id).ToList();
        var uncategorized = groups.Where(g => g.Id is null).ToList();

        var rows = new List<(Guid? Id, string Name, decimal Gross, decimal Net, string Kind, int Count)>();
        var shown = top is { } limit && ranked.Count > limit ? ranked.Take(limit).ToList() : ranked;
        rows.AddRange(shown.Select(g => (g.Id, g.Name, g.Gross, g.Net, Kinds.Category, 1)));
        var rest = ranked.Skip(shown.Count).ToList();
        if (rest.Count > 0) rows.Add((null, OtherCategories, rest.Sum(g => g.Gross), rest.Sum(g => g.Net), Kinds.Others, rest.Count));
        if (uncategorized.Count > 0) rows.Add((null, Uncategorized, uncategorized.Sum(g => g.Gross), uncategorized.Sum(g => g.Net), Kinds.Uncategorized, 0));

        var gross = realized.Where(f => f.Amount > 0m).Sum(f => f.Amount);
        var adjustments = realized.Where(f => f.Amount < 0m).Sum(f => f.Amount);
        var total = decimal.Round(gross + adjustments, Money.MaxScale, MidpointRounding.AwayFromZero);

        var reasons = new List<string>();
        List<ExpenseReconciliationDto>? reconciliation = null;
        if (reference is null) reasons.Add(Reasons.ReferenceUnavailable);
        else
        {
            if (reference.Total != total) reasons.Add(Reasons.TotalDiffersFromExpenses);
            var cards = realized.Where(IsCardBucket).Sum(f => f.Amount); var bank = realized.Where(f => !IsCardBucket(f)).Sum(f => f.Amount);
            reconciliation = [Bucket("Bank", bank, reference.Bank), Bucket("Cards", cards, reference.Cards)];
            if (reference.Total != total)
            {
                if (bank != reference.Bank) reasons.Add(Reasons.BankBasisDiffers);
                if (cards != reference.Cards) reasons.Add(Reasons.CardBasisDiffers);
            }
        }

        var percentages = gross > 0m ? Shares(rows.Select(r => r.Gross).ToList(), gross) : null;
        var lines = rows.Select((r, index) => new ExpenseCategoryShareDto(r.Id, r.Name, PlanActualCalculator.Cents(r.Net), percentages?[index], r.Kind, r.Count, PlanActualCalculator.Cents(r.Gross))).ToList();
        var partial = reasons.Contains(Reasons.TotalDiffersFromExpenses) || reasons.Contains(Reasons.ReferenceUnavailable);
        return new(partial ? "Partial" : "Complete", competence, PlanActualCalculator.Cents(total), reference is null ? null : PlanActualCalculator.Cents(reference.Total), lines, reasons, ranked.Count,
            PlanActualCalculator.Cents(gross), PlanActualCalculator.Cents(adjustments), reconciliation);
    }

    private static ExpenseReconciliationDto Bucket(string bucket, decimal distributed, decimal reference) =>
        new(bucket, PlanActualCalculator.Cents(distributed), PlanActualCalculator.Cents(reference), PlanActualCalculator.Cents(distributed - reference));

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
    private async Task<ExpenseDistributionDto> BuildExpenseDistributionAsync(Guid workspaceId, Guid userId, string month, string requestedScope, Guid? accountId, int? top, CancellationToken ct)
    {
        try
        {
            var first = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
            var (facts, _) = await budgetFacts.ReadFactsAsync(workspaceId, first, first.AddMonths(1), ct);
            var payers = accountId is null ? new Dictionary<Guid, Guid>()
                : (await cardRepository.ListAsync(workspaceId, includeArchived: true, ct)).Where(c => c.DefaultPaymentAccountId is not null).ToDictionary(c => c.Id, c => c.DefaultPaymentAccountId!.Value);
            var categories = (await categoryRepository.ListAsync(workspaceId, includeInactive: true, ct)).ToDictionary(c => c.Id);
            // "Despesas do mês" e seus blocos, do mesmo escopo, só para conferir: nada é ajustado para fechar a conta.
            ExpenseReference? reference = null;
            try
            {
                if ((await PlanOnceAsync(workspaceId, userId, month, requestedScope, accountId, ct)).Value is { } dashboard)
                {
                    var consumption = dashboard.Expenses.Consumption;
                    decimal Component(string code) => consumption.Components.Where(c => c.Code == code).Select(c => Num(c.Amount)).FirstOrDefault();
                    reference = new(Num(consumption.Realized), Component("BankExpenses"), Component("CardPurchases"));
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* sem a referência o bloco sai como parcial */ }
            return ExpenseDistributionCalculator.Build(month, facts, categories, payers, accountId, reference, top);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Unavailable", month, null, null, [], []); // complementar: nunca derruba os demais blocos
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
        return PlanActualResult<ExpenseDistributionDto>.Ok(await BuildExpenseDistributionAsync(workspaceId, userId, resolved.Month!, resolved.Requested!, resolved.Selected?.Id, null, ct));
    }
}

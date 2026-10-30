namespace Saldo360.Application.Budgets;

public interface IFinancialBudgetExecutionRepository
{
    /// <summary>
    /// Reads the active budget, its active targets and category aggregates in one read-only repeatable-read snapshot.
    /// Returns null when the budget does not exist in the family or is archived.
    /// </summary>
    Task<FinancialBudgetExecutionSource?> GetAsync(
        Guid workspaceId, Guid budgetId, CancellationToken cancellationToken);

    /// <summary>Todos os registros de despesa do intervalo [first, through), pela mesma lista do orçamento, com os caminhos "Categoria / Subcategoria". Somente leitura.</summary>
    Task<(IReadOnlyList<BudgetFact> Facts, IReadOnlyDictionary<Guid, string> CategoryPaths)> ReadFactsAsync(Guid workspaceId, DateOnly first, DateOnly through, CancellationToken cancellationToken);

    /// <summary>Os registros que compõem uma meta, pela MESMA lista e regra de cobertura do resumo. Null se o orçamento ou a meta não existirem (ou estiverem arquivados).</summary>
    Task<BudgetCompositionSource?> GetCompositionAsync(
        Guid workspaceId, Guid budgetId, Guid targetId, CancellationToken cancellationToken);
}


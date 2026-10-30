namespace Saldo360.Application.Budgets;

public interface IFinancialBudgetExecutionRepository
{
    /// <summary>
    /// Reads the active budget, its active targets and category aggregates in one read-only repeatable-read snapshot.
    /// Returns null when the budget does not exist in the family or is archived.
    /// </summary>
    Task<FinancialBudgetExecutionSource?> GetAsync(
        Guid workspaceId, Guid budgetId, CancellationToken cancellationToken);

    /// <summary>Os registros que compõem uma meta, pela MESMA lista e regra de cobertura do resumo. Null se o orçamento ou a meta não existirem (ou estiverem arquivados).</summary>
    Task<BudgetCompositionSource?> GetCompositionAsync(
        Guid workspaceId, Guid budgetId, Guid targetId, CancellationToken cancellationToken);
}


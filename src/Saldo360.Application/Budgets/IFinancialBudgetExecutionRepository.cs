namespace Saldo360.Application.Budgets;

public interface IFinancialBudgetExecutionRepository
{
    /// <summary>
    /// Reads the active budget, its active targets and category aggregates in one read-only repeatable-read snapshot.
    /// Returns null when the budget does not exist in the family or is archived.
    /// </summary>
    Task<FinancialBudgetExecutionSource?> GetAsync(
        Guid familyId, Guid budgetId, CancellationToken cancellationToken);
}


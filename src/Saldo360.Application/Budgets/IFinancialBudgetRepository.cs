using Saldo360.Domain.Budgets;

namespace Saldo360.Application.Budgets;

public interface IFinancialBudgetRepository
{
    Task<bool> BudgetExistsAsync(Guid workspaceId, DateOnly competence, CancellationToken cancellationToken);
    Task<FinancialBudget?> FindBudgetAsync(Guid workspaceId, Guid budgetId, CancellationToken cancellationToken);
    Task<FinancialBudgetTarget?> FindTargetAsync(Guid budgetId, Guid targetId, CancellationToken cancellationToken);
    Task<bool> TargetExistsAsync(Guid budgetId, Guid categoryId, CancellationToken cancellationToken);
    Task<BudgetCategoryState?> GetCategoryAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken);
    Task<bool> TryAddBudgetAsync(FinancialBudget budget, CancellationToken cancellationToken);
    Task<bool> TryAddTargetAsync(FinancialBudgetTarget target, CancellationToken cancellationToken);
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialBudgetDto>> ListAsync(
        Guid workspaceId, DateOnly? competence, bool includeArchived, CancellationToken cancellationToken);
    Task<FinancialBudgetDto?> GetAsync(Guid workspaceId, Guid budgetId, CancellationToken cancellationToken);
}

public sealed record BudgetCategoryState(string Name, bool IsActive, bool IsExpense);

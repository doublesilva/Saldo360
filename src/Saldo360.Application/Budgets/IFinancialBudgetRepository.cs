using Saldo360.Domain.Budgets;

namespace Saldo360.Application.Budgets;

public interface IFinancialBudgetRepository
{
    Task<bool> BudgetExistsAsync(Guid workspaceId, DateOnly competence, CancellationToken cancellationToken);
    Task<FinancialBudget?> FindBudgetAsync(Guid workspaceId, Guid budgetId, CancellationToken cancellationToken);
    Task<FinancialBudgetTarget?> FindTargetAsync(Guid budgetId, Guid targetId, CancellationToken cancellationToken);
    /// <summary>Existe, no mesmo orçamento, meta ativa da categoria principal ou de uma subcategoria desta categoria (ou da principal dela): contaria o mesmo gasto duas vezes.</summary>
    Task<bool> RelatedTargetExistsAsync(Guid budgetId, Guid categoryId, CancellationToken cancellationToken);

    /// <summary>Meta consolidada (despesas fixas ou todos os cartões) do orçamento, inclusive arquivada, para reativá-la em vez de duplicar.</summary>
    Task<FinancialBudgetTarget?> FindAggregateTargetAsync(Guid budgetId, BudgetTargetGroupingType groupingType, CancellationToken cancellationToken);

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

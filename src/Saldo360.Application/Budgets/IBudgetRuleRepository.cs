using Saldo360.Domain.Budgets;

namespace Saldo360.Application.Budgets;

public sealed record BudgetCategoryName(Guid Id, string Name, string? ParentName, Guid? ParentId);

public sealed record RuleHistoryRow(BudgetRuleScope Scope, BudgetRuleTarget Target, Guid? CategoryId, DateOnly Competence, int Revision, BudgetRuleAction Action, decimal? Amount, string? Note,
    DateTimeOffset CreatedAt);

public interface IBudgetRuleRepository
{
    /// <summary>Grava todas as alterações em uma única transação. Falso quando outra alteração concorrente ocupou a mesma revisão (nada é gravado).</summary>
    Task<bool> TryAddAsync(IReadOnlyList<FinancialBudgetRuleChange> changes, CancellationToken cancellationToken);

    Task<IReadOnlyList<RuleHistoryRow>> ListHistoryAsync(Guid workspaceId, DateOnly? competence, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, BudgetCategoryName>> CategoryNamesAsync(Guid workspaceId, IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken);
}

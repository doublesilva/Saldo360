using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Budgets;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Persistence.Configurations;

namespace Saldo360.Infrastructure.Budgets;

internal sealed class BudgetRuleRepository(Saldo360DbContext db) : IBudgetRuleRepository
{
    public async Task<int> NextRevisionAsync(Guid workspaceId, BudgetRuleScope scope, BudgetRuleTarget target, Guid? categoryId, DateOnly competence, CancellationToken cancellationToken) =>
        (await db.FinancialBudgetRuleChanges.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.Scope == scope && c.Target == target && c.CategoryId == categoryId && c.Competence == competence)
            .MaxAsync(c => (int?)c.Revision, cancellationToken) ?? 0) + 1;

    public async Task<bool> TryAddAsync(IReadOnlyList<FinancialBudgetRuleChange> changes, CancellationToken cancellationToken)
    {
        db.FinancialBudgetRuleChanges.AddRange(changes);
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: FinancialBudgetRuleChangeConfiguration.UniqueRevisionIndex })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<RuleHistoryRow>> ListHistoryAsync(Guid workspaceId, DateOnly? competence, int limit, CancellationToken cancellationToken)
    {
        var query = db.FinancialBudgetRuleChanges.AsNoTracking().Where(c => c.WorkspaceId == workspaceId);
        if (competence is { } month) query = query.Where(c => c.Competence == month);
        return await query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Revision).ThenBy(c => c.Id).Take(limit)
            .Select(c => new RuleHistoryRow(c.Scope, c.Target, c.CategoryId, c.Competence, c.Revision, c.Action, c.Amount, c.Note, c.CreatedAt)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, BudgetCategoryName>> CategoryNamesAsync(Guid workspaceId, IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken) =>
        categoryIds.Count == 0 ? new Dictionary<Guid, BudgetCategoryName>() : (await db.FinancialCategories.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && categoryIds.Contains(c.Id))
            .Select(c => new BudgetCategoryName(c.Id, c.Name, db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.Name).FirstOrDefault(), c.ParentCategoryId))
            .ToListAsync(cancellationToken)).ToDictionary(c => c.Id);
}

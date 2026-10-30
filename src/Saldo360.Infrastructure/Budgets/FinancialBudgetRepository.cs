using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Persistence.Configurations;

namespace Saldo360.Infrastructure.Budgets;

internal sealed class FinancialBudgetRepository(Saldo360DbContext db) : IFinancialBudgetRepository
{
    public Task<bool> BudgetExistsAsync(Guid workspaceId, DateOnly competence, CancellationToken cancellationToken) =>
        db.FinancialBudgets.AnyAsync(item => item.WorkspaceId == workspaceId && item.Competence == competence, cancellationToken);

    public Task<FinancialBudget?> FindBudgetAsync(Guid workspaceId, Guid budgetId, CancellationToken cancellationToken) =>
        db.FinancialBudgets.SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.Id == budgetId, cancellationToken);

    public Task<FinancialBudgetTarget?> FindTargetAsync(Guid budgetId, Guid targetId, CancellationToken cancellationToken) =>
        db.FinancialBudgetTargets.SingleOrDefaultAsync(item => item.BudgetId == budgetId && item.Id == targetId, cancellationToken);

    public Task<bool> RelatedTargetExistsAsync(Guid budgetId, Guid categoryId, CancellationToken cancellationToken) =>
        db.FinancialBudgetTargets.AnyAsync(target => target.BudgetId == budgetId && !target.IsArchived && target.CategoryId != categoryId
            && (db.FinancialCategories.Any(c => c.Id == target.CategoryId && c.ParentCategoryId == categoryId)
                || db.FinancialCategories.Any(c => c.Id == categoryId && c.ParentCategoryId == target.CategoryId)), cancellationToken);

    public Task<bool> TargetExistsAsync(Guid budgetId, Guid categoryId, CancellationToken cancellationToken) =>
        db.FinancialBudgetTargets.AnyAsync(item => item.BudgetId == budgetId && item.CategoryId == categoryId, cancellationToken);

    public Task<BudgetCategoryState?> GetCategoryAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.Id == categoryId)
            .Select(item => new BudgetCategoryState(item.Name, item.IsActive && (item.ParentCategoryId == null
                || db.FinancialCategories.Any(p => p.Id == item.ParentCategoryId && p.IsActive)), item.Type == CategoryType.Expense))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TryAddBudgetAsync(FinancialBudget budget, CancellationToken cancellationToken)
    {
        db.FinancialBudgets.Add(budget);
        return await TrySaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryAddTargetAsync(FinancialBudgetTarget target, CancellationToken cancellationToken)
    {
        db.FinancialBudgetTargets.Add(target);
        return await TrySaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: FinancialBudgetConfiguration.UniqueCompetenceIndex
                or FinancialBudgetTargetConfiguration.UniqueGroupingIndex })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<FinancialBudgetDto>> ListAsync(
        Guid workspaceId, DateOnly? competence, bool includeArchived, CancellationToken cancellationToken)
    {
        var query = db.FinancialBudgets.AsNoTracking().Where(item => item.WorkspaceId == workspaceId);
        if (competence is { } month) query = query.Where(item => item.Competence == month);
        if (!includeArchived) query = query.Where(item => !item.IsArchived);
        var entities = await query.OrderByDescending(item => item.Competence).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        return await ProjectAsync(entities, cancellationToken);
    }

    public async Task<FinancialBudgetDto?> GetAsync(Guid workspaceId, Guid budgetId, CancellationToken cancellationToken)
    {
        var entity = await db.FinancialBudgets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.Id == budgetId, cancellationToken);
        return entity is null ? null : (await ProjectAsync([entity], cancellationToken)).Single();
    }

    private async Task<IReadOnlyList<FinancialBudgetDto>> ProjectAsync(
        IReadOnlyList<FinancialBudget> entities, CancellationToken cancellationToken)
    {
        var ids = entities.Select(item => item.Id).ToList();
        var targets = await (
            from target in db.FinancialBudgetTargets.AsNoTracking()
            join category in db.FinancialCategories.AsNoTracking()
                on new { target.WorkspaceId, Id = target.CategoryId, target.CategoryType }
                equals new { category.WorkspaceId, category.Id, CategoryType = category.Type }
            where ids.Contains(target.BudgetId)
            orderby target.IsArchived, target.Name, target.Id
            select new
            {
                target.BudgetId,
                Dto = new FinancialBudgetTargetDto(target.Id, target.Name, target.PlannedAmount, target.GroupingType,
                    new FinancialBudgetCategoryDto(category.Id, category.Name, category.IsActive, db.FinancialCategories.Where(p => p.Id == category.ParentCategoryId).Select(p => p.Name).FirstOrDefault()), target.IsArchived,
                    target.CreatedAt, target.UpdatedAt, target.ArchivedAt),
            }).ToListAsync(cancellationToken);
        return entities.Select(item =>
        {
            var children = targets.Where(target => target.BudgetId == item.Id).Select(target => target.Dto).ToList();
            return new FinancialBudgetDto(item.Id, item.Competence, item.Description, item.IsArchived,
                children.Where(target => !target.IsArchived).Sum(target => target.PlannedAmount), children,
                item.CreatedAt, item.UpdatedAt, item.ArchivedAt);
        }).ToList();
    }
}

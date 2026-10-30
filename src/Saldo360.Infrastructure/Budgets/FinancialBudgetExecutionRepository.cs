using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Budgets;

internal sealed class FinancialBudgetExecutionRepository(Saldo360DbContext db)
    : IFinancialBudgetExecutionRepository
{
    public async Task<FinancialBudgetExecutionSource?> GetAsync(
        Guid workspaceId, Guid budgetId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var result = await ReadRangeAsync(db, workspaceId, null, null, budgetId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result.SingleOrDefault();
    }

    public async Task<BudgetCompositionSource?> GetCompositionAsync(
        Guid workspaceId, Guid budgetId, Guid targetId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var budget = await db.FinancialBudgets.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.Id == budgetId && !item.IsArchived)
            .Select(item => new { item.Id, item.Competence }).SingleOrDefaultAsync(cancellationToken);
        var target = budget is null ? null : await db.FinancialBudgetTargets.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.BudgetId == budgetId && item.Id == targetId && !item.IsArchived)
            .Select(item => new { item.Id, item.GroupingType, item.CategoryId, item.PlannedAmount }).SingleOrDefaultAsync(cancellationToken);
        if (budget is null || target is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var parentOf = await BudgetFactReader.ParentsAsync(db, workspaceId, target.CategoryId is { } category ? [category] : [], cancellationToken);
        var month = await BudgetFactReader.ReadAsync(db, workspaceId, budget.Competence, budget.Competence.AddMonths(1), cancellationToken);
        var covered = month.Where(fact => BudgetTargetMatcher.Covers(target.GroupingType, target.CategoryId, parentOf, fact))
            .OrderBy(fact => fact.Date).ThenBy(fact => fact.Description, StringComparer.CurrentCultureIgnoreCase).ThenBy(fact => fact.RecordId).ToList();
        var hasCards = await HasCardsAsync(db, workspaceId, cancellationToken);
        var coverage = CoverageOf(target.GroupingType, covered, month, hasCards);
        var ids = covered.Where(fact => fact.CategoryId != null).Select(fact => fact.CategoryId!.Value).Distinct().ToList();
        var categories = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, ParentName = db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.Name).FirstOrDefault() }).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(budget.Id, budget.Competence, target.Id, target.GroupingType, target.CategoryId, target.PlannedAmount, covered, coverage,
            categories.ToDictionary(c => c.Id, c => c.ParentName is null ? c.Name : $"{c.ParentName} / {c.Name}"));
    }

    private static Task<bool> HasCardsAsync(Saldo360DbContext db, Guid workspaceId, CancellationToken cancellationToken) =>
        db.FinancialCreditCards.AsNoTracking().AnyAsync(card => card.WorkspaceId == workspaceId && !card.IsArchived, cancellationToken);

    /// <summary>
    /// Sinais de dados incompletos: competência estimada nos cartões e despesas sem tipo definido (que não entram nas fixas). Ausência de dado nunca
    /// vira "zero confirmado": sem cartões cadastrados a meta de cartões é marcada como indisponível.
    /// </summary>
    internal static BudgetCoverage CoverageOf(BudgetTargetGroupingType grouping, IReadOnlyList<BudgetFact> covered, IReadOnlyList<BudgetFact> month, bool hasCards)
    {
        var estimated = covered.Count(fact => fact.IsCard && !fact.CompetenceReliable);
        if (grouping != BudgetTargetGroupingType.FixedExpenses)
            return new(estimated, NoCards: grouping == BudgetTargetGroupingType.AllCards && !hasCards);
        var undefined = month.Where(fact => fact.Nature == ExpenseNature.Undefined && fact.Amount > 0m).ToList();
        return new(estimated, undefined.Count, undefined.Sum(fact => fact.Amount));
    }

    internal static async Task<IReadOnlyList<FinancialBudgetExecutionSource>> ReadRangeAsync(
        Saldo360DbContext db, Guid workspaceId, DateOnly? from, DateOnly? until, Guid? budgetId, CancellationToken cancellationToken)
    {
        var budgets = await db.FinancialBudgets.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && (budgetId == null || item.Id == budgetId) && !item.IsArchived
                && (from == null || item.Competence >= from) && (until == null || item.Competence < until))
            .Select(item => new { item.Id, item.Competence })
            .ToListAsync(cancellationToken);
        if (budgets.Count == 0) return [];

        var first = budgets.Min(x => x.Competence);
        var through = budgets.Max(x => x.Competence).AddMonths(1);
        var budgetIds = budgets.Select(x => x.Id).ToList();
        var targets = await db.FinancialBudgetTargets.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && budgetIds.Contains(item.BudgetId) && !item.IsArchived)
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.BudgetId, item.GroupingType, item.CategoryId, item.PlannedAmount })
            .ToListAsync(cancellationToken);
        // Meta de categoria principal cobre os registros diretos e os das subcategorias (R02): cada registro tem UM categoryId, então é contado uma só vez.
        var parentOf = await BudgetFactReader.ParentsAsync(db, workspaceId, targets.Where(item => item.CategoryId != null).Select(item => item.CategoryId!.Value).ToList(), cancellationToken);
        var facts = await BudgetFactReader.ReadAsync(db, workspaceId, first, through, cancellationToken);
        var hasCards = await HasCardsAsync(db, workspaceId, cancellationToken);

        return budgets.Select(budget =>
        {
            var month = facts.Where(fact => fact.Date.Year == budget.Competence.Year && fact.Date.Month == budget.Competence.Month).ToList();
            return new FinancialBudgetExecutionSource(budget.Id, budget.Competence,
                targets.Where(item => item.BudgetId == budget.Id).Select(item =>
                {
                    var covered = month.Where(fact => BudgetTargetMatcher.Covers(item.GroupingType, item.CategoryId, parentOf, fact)).ToList();
                    return new FinancialBudgetExecutionSourceTarget(item.Id, item.CategoryId, item.PlannedAmount,
                        covered.Where(fact => fact.State == BudgetFactState.Realized).Sum(fact => fact.Amount),
                        covered.Where(fact => fact.State == BudgetFactState.Pending).Sum(fact => fact.Amount),
                        covered.Where(fact => fact.State == BudgetFactState.CardLaunched).Sum(fact => fact.Amount),
                        item.GroupingType, CoverageOf(item.GroupingType, covered, month, hasCards));
                }).ToList());
        }).ToList();
    }
}

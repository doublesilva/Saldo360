using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Budgets;

internal sealed class FinancialBudgetExecutionRepository(Saldo360DbContext db)
    : IFinancialBudgetExecutionRepository
{
    public async Task<FinancialBudgetExecutionSource?> GetAsync(
        Guid familyId, Guid budgetId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

        var budget = await db.FinancialBudgets.AsNoTracking()
            .Where(item => item.FamilyId == familyId && item.Id == budgetId && !item.IsArchived)
            .Select(item => new { item.Id, item.Competence })
            .SingleOrDefaultAsync(cancellationToken);
        if (budget is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var through = budget.Competence.AddMonths(1);
        var targets = await db.FinancialBudgetTargets.AsNoTracking()
            .Where(item => item.FamilyId == familyId && item.BudgetId == budgetId && !item.IsArchived)
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.CategoryId, item.PlannedAmount })
            .ToListAsync(cancellationToken);
        var categoryIds = targets.Select(item => item.CategoryId).ToList();

        var realized = await db.FinancialTransactions.AsNoTracking()
            .Where(item => item.FamilyId == familyId
                && item.CategoryId != null
                && categoryIds.Contains(item.CategoryId.Value)
                && item.Type == CategoryType.Expense
                && !item.IsCancelled
                && item.TransactionDate >= budget.Competence
                && item.TransactionDate < through)
            .GroupBy(item => item.CategoryId!.Value)
            .Select(group => new CategoryTotal(group.Key, group.Sum(item => item.Amount)))
            .ToListAsync(cancellationToken);

        var committed = await db.FinancialPlannedTransactions.AsNoTracking()
            .Where(item => item.FamilyId == familyId
                && categoryIds.Contains(item.CategoryId)
                && item.Type == CategoryType.Expense
                && !item.IsCancelled
                && item.DueDate >= budget.Competence
                && item.DueDate < through
                && !db.FinancialTransactions.Any(transactionItem =>
                    transactionItem.FamilyId == item.FamilyId
                    && transactionItem.PlannedTransactionId == item.Id
                    && !transactionItem.IsCancelled))
            .GroupBy(item => item.CategoryId)
            .Select(group => new CategoryTotal(group.Key, group.Sum(item => item.Amount)))
            .ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        var realizedByCategory = realized.ToDictionary(item => item.CategoryId, item => item.Amount);
        var committedByCategory = committed.ToDictionary(item => item.CategoryId, item => item.Amount);
        return new FinancialBudgetExecutionSource(
            budget.Id,
            budget.Competence,
            targets.Select(item => new FinancialBudgetExecutionSourceTarget(
                item.Id,
                item.CategoryId,
                item.PlannedAmount,
                realizedByCategory.GetValueOrDefault(item.CategoryId),
                committedByCategory.GetValueOrDefault(item.CategoryId))).ToList());
    }

    private sealed record CategoryTotal(Guid CategoryId, decimal Amount);
}


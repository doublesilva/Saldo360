using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Calendar;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Calendar;

internal sealed class FinancialCalendarRepository(Saldo360DbContext db) : IFinancialCalendarRepository
{
    public async Task<FinancialCalendarSnapshot> ListAsync(
        Guid familyId,
        FinancialCalendarFilter filter,
        CancellationToken cancellationToken)
    {
        // As duas coleções precisam representar o mesmo instante lógico: uma realização concorrente não pode fazer
        // a previsão sumir da primeira leitura sem que o lançamento apareça na segunda (ou vice-versa).
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

        var effectiveTransactions = db.FinancialTransactions
            .AsNoTracking()
            .Where(t => t.FamilyId == familyId
                && !t.IsCancelled
                && t.TransactionDate >= filter.From
                && t.TransactionDate <= filter.Through
                && (filter.AccountId == null || t.AccountId == filter.AccountId)
                && (filter.Type == null || t.Type == filter.Type));

        var pendingPlannedTransactions = db.FinancialPlannedTransactions
            .AsNoTracking()
            .Where(p => p.FamilyId == familyId
                && !p.IsCancelled
                && p.DueDate >= filter.From
                && p.DueDate <= filter.Through
                && (filter.AccountId == null || p.AccountId == filter.AccountId)
                && (filter.Type == null || p.Type == filter.Type)
                && !db.FinancialTransactions.Any(t =>
                    t.FamilyId == p.FamilyId && t.PlannedTransactionId == p.Id && !t.IsCancelled));

        var effective =
            from t in effectiveTransactions
            join a in db.FinancialAccounts on new { t.FamilyId, Id = t.AccountId } equals new { a.FamilyId, a.Id }
            join c in db.FinancialCategories on new { t.FamilyId, Id = t.CategoryId } equals new { c.FamilyId, c.Id }
            select new FinancialCalendarItemDto(
                t.Id,
                FinancialCalendarSource.Transaction,
                FinancialCalendarStatus.Realized,
                t.Type,
                t.Amount,
                t.TransactionDate,
                t.Description,
                new TransactionReferenceDto(a.Id, a.Name, a.IsActive),
                new TransactionReferenceDto(c.Id, c.Name, c.IsActive),
                t.Currency,
                t.PlannedTransactionId);

        var planned =
            from p in pendingPlannedTransactions
            join a in db.FinancialAccounts on new { p.FamilyId, Id = p.AccountId } equals new { a.FamilyId, a.Id }
            join c in db.FinancialCategories on new { p.FamilyId, Id = p.CategoryId } equals new { c.FamilyId, c.Id }
            select new FinancialCalendarItemDto(
                p.Id,
                FinancialCalendarSource.PlannedTransaction,
                p.DueDate < filter.Today ? FinancialCalendarStatus.Overdue : FinancialCalendarStatus.Pending,
                p.Type,
                p.Amount,
                p.DueDate,
                p.Description,
                new TransactionReferenceDto(a.Id, a.Name, a.IsActive),
                new TransactionReferenceDto(c.Id, c.Name, c.IsActive),
                p.Currency,
                null);

        var effectiveItems = await effective.ToListAsync(cancellationToken);
        var plannedItems = await planned.ToListAsync(cancellationToken);
        var effectiveTotals = await effectiveTransactions
            .GroupBy(_ => 1)
            .Select(group => new TypeTotals(
                group.Sum(item => item.Type == CategoryType.Income ? item.Amount : 0m),
                group.Sum(item => item.Type == CategoryType.Expense ? item.Amount : 0m)))
            .SingleOrDefaultAsync(cancellationToken) ?? new TypeTotals(0m, 0m);
        var pendingTotals = await pendingPlannedTransactions
            .GroupBy(_ => 1)
            .Select(group => new PendingTotals(
                group.Sum(item => item.Type == CategoryType.Income ? item.Amount : 0m),
                group.Sum(item => item.Type == CategoryType.Expense ? item.Amount : 0m),
                group.Sum(item => item.Type == CategoryType.Income && item.DueDate < filter.Today ? item.Amount : 0m),
                group.Sum(item => item.Type == CategoryType.Expense && item.DueDate < filter.Today ? item.Amount : 0m)))
            .SingleOrDefaultAsync(cancellationToken) ?? new PendingTotals(0m, 0m, 0m, 0m);
        await transaction.CommitAsync(cancellationToken);

        var items = effectiveItems
            .Concat(plannedItems)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Source)
            .ThenBy(item => item.Id)
            .ToList();

        return new FinancialCalendarSnapshot(
            items,
            new FinancialCalendarSummaryDto(
                effectiveTotals.Income,
                effectiveTotals.Expense,
                pendingTotals.Income,
                pendingTotals.Expense,
                pendingTotals.OverdueIncome,
                pendingTotals.OverdueExpense));
    }

    private sealed record TypeTotals(decimal Income, decimal Expense);
    private sealed record PendingTotals(decimal Income, decimal Expense, decimal OverdueIncome, decimal OverdueExpense);
}

using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Calendar;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Transactions;
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
                    t.FamilyId == p.FamilyId && t.PlannedTransactionId == p.Id && !t.IsCancelled)
                && !db.FinancialPlannedInvoiceReconciliations.Any(r =>
                    r.FamilyId == p.FamilyId && r.PlannedTransactionId == p.Id && r.RemovedAt == null)
                && !db.FinancialDebtInstallmentReconciliations.Any(r =>
                    r.FamilyId == p.FamilyId && r.PlannedTransactionId == p.Id && r.RemovedAt == null));


        var effective =
            from t in effectiveTransactions
            join a in db.FinancialAccounts on new { t.FamilyId, Id = t.AccountId } equals new { a.FamilyId, a.Id }
            join category in db.FinancialCategories on new { t.FamilyId, t.CategoryId } equals new { category.FamilyId, CategoryId = (Guid?)category.Id } into categories
            from c in categories.DefaultIfEmpty()
            join materialization in db.FinancialRecurrenceMaterializations on new { t.FamilyId, PlannedTransactionId = t.PlannedTransactionId }
                equals new { materialization.FamilyId, PlannedTransactionId = (Guid?)materialization.PlannedTransactionId } into materializations
            from m in materializations.DefaultIfEmpty()
            select new FinancialCalendarItemDto(
                t.Id,
                FinancialCalendarSource.Transaction,
                FinancialCalendarStatus.Realized,
                t.Type,
                t.Amount,
                t.TransactionDate,
                t.Description,
                new TransactionReferenceDto(a.Id, a.Name, a.IsActive),
                c == null ? null : new TransactionReferenceDto(c.Id, c.Name, c.IsActive),
                t.Currency,
                t.PlannedTransactionId,
                t.Origin,
                m == null ? null : m.RecurrenceId,
                m == null ? null : m.OccurrenceNumber,
                m == null ? null : m.ScheduledDate);

        var planned =
            from p in pendingPlannedTransactions
            join a in db.FinancialAccounts on new { p.FamilyId, Id = p.AccountId } equals new { a.FamilyId, a.Id }
            join c in db.FinancialCategories on new { p.FamilyId, Id = p.CategoryId } equals new { c.FamilyId, c.Id }
            join materialization in db.FinancialRecurrenceMaterializations on p.Id equals materialization.PlannedTransactionId into materializations
            from m in materializations.DefaultIfEmpty()
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
                null,
                FinancialTransactionOrigin.Manual,
                m == null ? null : m.RecurrenceId,
                m == null ? null : m.OccurrenceNumber,
                m == null ? null : m.ScheduledDate);

        var effectiveItems = await effective.ToListAsync(cancellationToken);
        var plannedItems = await planned.ToListAsync(cancellationToken);
        var debtRows = await (from i in db.FinancialDebtInstallments.AsNoTracking()
            join d in db.FinancialDebts.AsNoTracking() on new { i.FamilyId, DebtId = i.DebtId } equals new { d.FamilyId, DebtId = d.Id }
            join a0 in db.FinancialAccounts.AsNoTracking() on new { i.FamilyId, Id = d.ResponsibleAccountId } equals new { a0.FamilyId, Id = (Guid?)a0.Id } into accounts
            from a in accounts.DefaultIfEmpty()
            where i.FamilyId == familyId && d.Status != Domain.Debts.DebtStatus.Archived && i.CancelledAt == null
                && i.DueDate >= filter.From && i.DueDate <= filter.Through
                && (filter.AccountId == null || d.ResponsibleAccountId == filter.AccountId)
                && (filter.Type == null || filter.Type == CategoryType.Expense)
            let paid = db.FinancialDebtPayments.Where(p => p.FamilyId == familyId && p.InstallmentId == i.Id && p.CancelledAt == null).Sum(p => (decimal?)p.Amount) ?? 0m
            let remaining = i.ScheduledAmount - paid
            where remaining > 0
            select new { i, d, a, paid, remaining }).ToListAsync(cancellationToken);
        var debtItems = debtRows.Select(x => new FinancialCalendarItemDto(
            x.i.Id, FinancialCalendarSource.DebtInstallment,
            x.i.DueDate < filter.Today ? FinancialCalendarStatus.Overdue : FinancialCalendarStatus.Pending,
            CategoryType.Expense, x.remaining, x.i.DueDate, $"{x.d.Name} - parcela {x.i.InstallmentNumber}",
            x.a == null ? null : new TransactionReferenceDto(x.a.Id, x.a.Name, x.a.IsActive), null,
            FinancialTransaction.DefaultCurrency, null, FinancialTransactionOrigin.DebtInstallmentPayment,
            null, null, x.i.DueDate)).ToList();
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
        var debtPending = debtRows.Sum(x => x.remaining);
        var debtOverdue = debtRows.Where(x => x.i.DueDate < filter.Today).Sum(x => x.remaining);
        await transaction.CommitAsync(cancellationToken);

        var items = effectiveItems
            .Concat(plannedItems)
            .Concat(debtItems)
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
                pendingTotals.Expense + debtPending,
                pendingTotals.OverdueIncome,
                pendingTotals.OverdueExpense + debtOverdue));
    }

    private sealed record TypeTotals(decimal Income, decimal Expense);
    private sealed record PendingTotals(decimal Income, decimal Expense, decimal OverdueIncome, decimal OverdueExpense);
}

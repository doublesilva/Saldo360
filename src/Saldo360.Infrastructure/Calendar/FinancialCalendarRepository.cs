using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Calendar;
using Saldo360.Application.Transactions;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Calendar;

internal sealed class FinancialCalendarRepository(Saldo360DbContext db) : IFinancialCalendarRepository
{
    public async Task<IReadOnlyList<FinancialCalendarItemDto>> ListAsync(
        Guid familyId,
        FinancialCalendarFilter filter,
        CancellationToken cancellationToken)
    {
        // As duas coleções precisam representar o mesmo instante lógico: uma realização concorrente não pode fazer
        // a previsão sumir da primeira leitura sem que o lançamento apareça na segunda (ou vice-versa).
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        var effective =
            from t in db.FinancialTransactions.AsNoTracking()
            join a in db.FinancialAccounts on new { t.FamilyId, Id = t.AccountId } equals new { a.FamilyId, a.Id }
            join c in db.FinancialCategories on new { t.FamilyId, Id = t.CategoryId } equals new { c.FamilyId, c.Id }
            where t.FamilyId == familyId
                && !t.IsCancelled
                && t.TransactionDate >= filter.From
                && t.TransactionDate <= filter.Through
                && (filter.AccountId == null || t.AccountId == filter.AccountId)
                && (filter.Type == null || t.Type == filter.Type)
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
            from p in db.FinancialPlannedTransactions.AsNoTracking()
            join a in db.FinancialAccounts on new { p.FamilyId, Id = p.AccountId } equals new { a.FamilyId, a.Id }
            join c in db.FinancialCategories on new { p.FamilyId, Id = p.CategoryId } equals new { c.FamilyId, c.Id }
            where p.FamilyId == familyId
                && !p.IsCancelled
                && p.DueDate >= filter.From
                && p.DueDate <= filter.Through
                && (filter.AccountId == null || p.AccountId == filter.AccountId)
                && (filter.Type == null || p.Type == filter.Type)
                && !db.FinancialTransactions.Any(t =>
                    t.FamilyId == p.FamilyId && t.PlannedTransactionId == p.Id && !t.IsCancelled)
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
        await transaction.CommitAsync(cancellationToken);

        return effectiveItems
            .Concat(plannedItems)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Source)
            .ThenBy(item => item.Id)
            .ToList();
    }
}

using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Calendar;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Calendar;

internal sealed class FinancialCalendarBalanceRepository(Saldo360DbContext db)
    : IFinancialCalendarBalanceRepository
{
    public Task<FinancialCalendarBalanceSnapshot?> ReadAsync(
        Guid familyId,
        Guid? accountId,
        DateOnly through,
        DateOnly today,
        CancellationToken cancellationToken) =>
        ReadCoreAsync(familyId, accountId, null, through, today, cancellationToken);

    public Task<FinancialCalendarBalanceSnapshot?> ReadMonthlyAsync(
        Guid familyId,
        Guid? accountId,
        DateOnly from,
        DateOnly through,
        DateOnly today,
        CancellationToken cancellationToken) =>
        ReadCoreAsync(familyId, accountId, from, through, today, cancellationToken);

    private async Task<FinancialCalendarBalanceSnapshot?> ReadCoreAsync(
        Guid familyId,
        Guid? accountId,
        DateOnly? periodFrom,
        DateOnly through,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

        var selectedAccounts = db.FinancialAccounts
            .AsNoTracking()
            .Where(account => account.FamilyId == familyId
                && (accountId == null || account.Id == accountId));
        var accounts = await selectedAccounts
            .Select(account => new FinancialCalendarBalanceAccount(
                account.Id, account.OpeningBalance, account.OpeningBalanceDate))
            .ToListAsync(cancellationToken);
        if (accountId is not null && accounts.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var lastRealizedDate = through < today ? through : today;
        var transactionRows = await (
                from movement in db.FinancialTransactions.AsNoTracking()
                join account in selectedAccounts
                    on new { movement.FamilyId, Id = movement.AccountId }
                    equals new { account.FamilyId, account.Id }
                where !movement.IsCancelled
                    && account.OpeningBalanceDate != null
                    && movement.TransactionDate >= account.OpeningBalanceDate.Value
                    && movement.TransactionDate <= lastRealizedDate
                group movement by movement.TransactionDate into daily
                select new
                {
                    Date = daily.Key,
                    Income = daily.Sum(item => item.Type == CategoryType.Income ? item.Amount : 0m),
                    Expense = daily.Sum(item => item.Type == CategoryType.Expense ? item.Amount : 0m),
                })
            .ToListAsync(cancellationToken);
        var transactionTotals = transactionRows
            .Select(row => new FinancialCalendarDailyTotals(row.Date, row.Income, row.Expense, 0m, 0m))
            .ToList();

        IReadOnlyList<FinancialCalendarDailyTotals> transferTotals = [];
        if (accountId is { } selectedAccountId
            && accounts.Single().OpeningBalanceDate is { } openingDate)
        {
            var transferRows = await db.FinancialTransfers
                .AsNoTracking()
                .Where(item => item.FamilyId == familyId
                    && !item.IsCancelled
                    && item.TransferDate >= openingDate
                    && item.TransferDate <= lastRealizedDate
                    && (item.SourceAccountId == selectedAccountId
                        || item.DestinationAccountId == selectedAccountId))
                .GroupBy(item => item.TransferDate)
                .Select(daily => new
                {
                    Date = daily.Key,
                    In = daily.Sum(item => item.DestinationAccountId == selectedAccountId ? item.Amount : 0m),
                    Out = daily.Sum(item => item.SourceAccountId == selectedAccountId ? item.Amount : 0m),
                })
                .ToListAsync(cancellationToken);
            transferTotals = transferRows
                .Select(row => new FinancialCalendarDailyTotals(row.Date, 0m, 0m, row.In, row.Out))
                .ToList();
        }

        var realized = transactionTotals
            .Concat(transferTotals)
            .GroupBy(row => row.Date)
            .Select(group => new FinancialCalendarDailyTotals(
                group.Key,
                group.Sum(row => row.Income),
                group.Sum(row => row.Expense),
                group.Sum(row => row.TransferIn),
                group.Sum(row => row.TransferOut)))
            .OrderBy(row => row.Date)
            .ToList();

        var pendingQuery = db.FinancialPlannedTransactions
            .AsNoTracking()
            .Where(item => item.FamilyId == familyId
                && !item.IsCancelled
                && item.DueDate <= through
                && !db.FinancialTransactions.Any(transaction =>
                    transaction.FamilyId == item.FamilyId
                    && transaction.PlannedTransactionId == item.Id
                    && !transaction.IsCancelled)
                && !db.FinancialPlannedInvoiceReconciliations.Any(reconciliation =>
                    reconciliation.FamilyId == item.FamilyId
                    && reconciliation.PlannedTransactionId == item.Id
                    && reconciliation.RemovedAt == null));
        if (accountId is { } pendingAccountId)
        {
            pendingQuery = pendingQuery.Where(item => item.AccountId == pendingAccountId);
        }

        var pendingRows = await pendingQuery
            .GroupBy(item => item.DueDate)
            .Select(daily => new
            {
                Date = daily.Key,
                Income = daily.Sum(item => item.Type == CategoryType.Income ? item.Amount : 0m),
                Expense = daily.Sum(item => item.Type == CategoryType.Expense ? item.Amount : 0m),
            })
            .OrderBy(row => row.Date)
            .ToListAsync(cancellationToken);
        var pending = pendingRows
            .Select(row => new FinancialCalendarDailyTotals(row.Date, row.Income, row.Expense, 0m, 0m))
            .ToList();

        var selectedAccountIds = await selectedAccounts.Select(account => account.Id).ToListAsync(cancellationToken);
        var cardObligations = await (from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                                     join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on installment.PurchaseId equals purchase.Id
                                     join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on installment.InvoiceId equals invoice.Id
                                     join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.FamilyId, CardId = invoice.CreditCardId } equals new { card.FamilyId, CardId = card.Id }
                                     where installment.FamilyId == familyId && installment.Status == Saldo360.Domain.CreditCards.CreditCardInstallmentStatus.Active
                                        && purchase.FamilyId == familyId && purchase.Status == Saldo360.Domain.CreditCards.CreditCardPurchaseStatus.Active
                                        && invoice.FamilyId == familyId && invoice.DueDate <= through
                                        && card.DefaultPaymentAccountId != null && selectedAccountIds.Contains(card.DefaultPaymentAccountId.Value)
                                     group installment.Amount by new { invoice.Id, invoice.DueDate } into grouped
                                     select new { grouped.Key.Id, grouped.Key.DueDate, Total = grouped.Sum() }).ToListAsync(cancellationToken);
        var cardPaid = await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(payment => payment.FamilyId == familyId && payment.Status == Saldo360.Domain.CreditCards.FinancialCreditCardInvoicePaymentStatus.Active)
            .GroupBy(payment => payment.InvoiceId).Select(group => new { Id = group.Key, Amount = group.Sum(payment => payment.Amount) }).ToListAsync(cancellationToken);
        var paidByInvoice = cardPaid.ToDictionary(item => item.Id, item => item.Amount);
        var cardPending = cardObligations.Select(item => new FinancialCalendarDailyTotals(item.DueDate, 0m, Math.Max(0m, item.Total - paidByInvoice.GetValueOrDefault(item.Id)), 0m, 0m)).Where(item => item.Expense > 0m).ToList();
        var allCardObligations = await (from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                                        join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on installment.PurchaseId equals purchase.Id
                                        join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on installment.InvoiceId equals invoice.Id
                                        join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.FamilyId, CardId = invoice.CreditCardId } equals new { card.FamilyId, CardId = card.Id }
                                        where installment.FamilyId == familyId && installment.Status == Saldo360.Domain.CreditCards.CreditCardInstallmentStatus.Active
                                           && purchase.FamilyId == familyId && purchase.Status == Saldo360.Domain.CreditCards.CreditCardPurchaseStatus.Active
                                           && invoice.FamilyId == familyId && invoice.DueDate <= through
                                        select card.DefaultPaymentAccountId).AnyAsync(account => account == null || !selectedAccountIds.Contains(account.Value), cancellationToken);

        IReadOnlyList<FinancialCalendarDailyTotals> periodRealized = [];
        if (periodFrom is { } firstPeriodDate)
        {
            var periodRows = await (
                    from movement in db.FinancialTransactions.AsNoTracking()
                    join account in selectedAccounts
                        on new { movement.FamilyId, Id = movement.AccountId }
                        equals new { account.FamilyId, account.Id }
                    where !movement.IsCancelled
                        && movement.TransactionDate >= firstPeriodDate
                        && movement.TransactionDate <= through
                    group movement by movement.TransactionDate into daily
                    select new
                    {
                        Date = daily.Key,
                        Income = daily.Sum(item => item.Type == CategoryType.Income ? item.Amount : 0m),
                        Expense = daily.Sum(item => item.Type == CategoryType.Expense ? item.Amount : 0m),
                    })
                .OrderBy(row => row.Date)
                .ToListAsync(cancellationToken);
            periodRealized = periodRows
                .Select(row => new FinancialCalendarDailyTotals(row.Date, row.Income, row.Expense, 0m, 0m))
                .ToList();
        }

        await transaction.CommitAsync(cancellationToken);
        return new FinancialCalendarBalanceSnapshot(accounts, realized, pending, periodRealized, cardPending, allCardObligations);
    }
}

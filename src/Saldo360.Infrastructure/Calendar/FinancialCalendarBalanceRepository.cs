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
        Guid workspaceId,
        Guid? accountId,
        DateOnly through,
        DateOnly today,
        CancellationToken cancellationToken) =>
        ReadCoreAsync(workspaceId, accountId, null, through, today, cancellationToken);

    public Task<FinancialCalendarBalanceSnapshot?> ReadMonthlyAsync(
        Guid workspaceId,
        Guid? accountId,
        DateOnly from,
        DateOnly through,
        DateOnly today,
        CancellationToken cancellationToken) =>
        ReadCoreAsync(workspaceId, accountId, from, through, today, cancellationToken);

    private async Task<FinancialCalendarBalanceSnapshot?> ReadCoreAsync(
        Guid workspaceId,
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
            .Where(account => account.WorkspaceId == workspaceId
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
                    on new { movement.WorkspaceId, Id = movement.AccountId }
                    equals new { account.WorkspaceId, account.Id }
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

        // Respect each account's own reference date, as T06.3 does. With different opening dates,
        // one side of an older transfer can already be included in its account's opening balance.
        var transfers = db.FinancialTransfers.AsNoTracking().Where(item => item.WorkspaceId == workspaceId
            && !item.IsCancelled && item.TransferDate <= lastRealizedDate);
        var incoming = from item in transfers
                       join account in selectedAccounts on new { item.WorkspaceId, Id = item.DestinationAccountId } equals new { account.WorkspaceId, account.Id }
                       where account.OpeningBalanceDate != null && item.TransferDate >= account.OpeningBalanceDate.Value
                       select new { Date = item.TransferDate, In = item.Amount, Out = 0m };
        var outgoing = from item in transfers
                       join account in selectedAccounts on new { item.WorkspaceId, Id = item.SourceAccountId } equals new { account.WorkspaceId, account.Id }
                       where account.OpeningBalanceDate != null && item.TransferDate >= account.OpeningBalanceDate.Value
                       select new { Date = item.TransferDate, In = 0m, Out = item.Amount };
        var transferRows = await incoming.Concat(outgoing).GroupBy(row => row.Date)
            .Select(daily => new { Date = daily.Key, In = daily.Sum(row => row.In), Out = daily.Sum(row => row.Out) })
            .ToListAsync(cancellationToken);
        var transferTotals = transferRows.Select(row => new FinancialCalendarDailyTotals(row.Date, 0m, 0m,
            accountId is null ? Math.Max(0m, row.In - row.Out) : row.In,
            accountId is null ? Math.Max(0m, row.Out - row.In) : row.Out)).ToList();

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
            .Where(item => item.WorkspaceId == workspaceId
                && !item.IsCancelled
                && item.DueDate <= through
                && !db.FinancialTransactions.Any(transaction =>
                    transaction.WorkspaceId == item.WorkspaceId
                    && transaction.PlannedTransactionId == item.Id
                    && !transaction.IsCancelled)
                && !db.FinancialPlannedInvoiceReconciliations.Any(reconciliation =>
                    reconciliation.WorkspaceId == item.WorkspaceId
                    && reconciliation.PlannedTransactionId == item.Id
                    && reconciliation.RemovedAt == null)
                && !db.FinancialDebtInstallmentReconciliations.Any(reconciliation =>
                    reconciliation.WorkspaceId == item.WorkspaceId
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
        var debtRows = await (from installment in db.FinancialDebtInstallments.AsNoTracking()
                              join debt in db.FinancialDebts.AsNoTracking() on new { installment.WorkspaceId, DebtId = installment.DebtId } equals new { debt.WorkspaceId, DebtId = debt.Id }
                              where installment.WorkspaceId == workspaceId && debt.Status != Saldo360.Domain.Debts.DebtStatus.Archived
                                  && installment.CancelledAt == null && installment.DueDate <= through
                                  && (accountId == null || debt.ResponsibleAccountId == accountId)
                              let paid = db.FinancialDebtPayments.Where(payment => payment.WorkspaceId == workspaceId && payment.InstallmentId == installment.Id && payment.CancelledAt == null).Sum(payment => (decimal?)payment.Amount) ?? 0m
                              let remaining = installment.ScheduledAmount - paid
                              where remaining > 0m
                              select new { installment.DueDate, debt.ResponsibleAccountId, remaining }).ToListAsync(cancellationToken);
        var debtPending = debtRows.GroupBy(row => row.DueDate).Select(group => new FinancialCalendarDailyTotals(group.Key, 0m, group.Sum(row => row.remaining), 0m, 0m)).ToList();
        var hasUnallocatedDebt = debtRows.Any(row => row.ResponsibleAccountId == null || !selectedAccountIds.Contains(row.ResponsibleAccountId.Value));

        var cardObligations = await (from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                                     join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on installment.PurchaseId equals purchase.Id
                                     join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on installment.InvoiceId equals invoice.Id
                                     join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.WorkspaceId, CardId = invoice.CreditCardId } equals new { card.WorkspaceId, CardId = card.Id }
                                     where installment.WorkspaceId == workspaceId && installment.Status == Saldo360.Domain.CreditCards.CreditCardInstallmentStatus.Active
                                        && purchase.WorkspaceId == workspaceId && purchase.Status == Saldo360.Domain.CreditCards.CreditCardPurchaseStatus.Active
                                        && invoice.WorkspaceId == workspaceId && invoice.DueDate <= through
                                        && card.DefaultPaymentAccountId != null && selectedAccountIds.Contains(card.DefaultPaymentAccountId.Value)
                                     group installment.Amount by new { invoice.Id, invoice.DueDate } into grouped
                                     select new { grouped.Key.Id, grouped.Key.DueDate, Total = grouped.Sum() }).ToListAsync(cancellationToken);
        var historicalCardObligations = await (from item in db.FinancialCreditCardImportedInstallments.AsNoTracking()
                                               join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on item.InvoiceId equals invoice.Id
                                               join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.WorkspaceId, CardId = invoice.CreditCardId } equals new { card.WorkspaceId, CardId = card.Id }
                                               where item.WorkspaceId == workspaceId && item.IsHistorical && item.Status == Saldo360.Domain.CreditCards.FinancialCreditCardImportedInstallmentStatus.Active
                                                  && invoice.WorkspaceId == workspaceId && invoice.DueDate <= through
                                                  && card.DefaultPaymentAccountId != null && selectedAccountIds.Contains(card.DefaultPaymentAccountId.Value)
                                               group item.Amount by new { invoice.Id, invoice.DueDate } into grouped
                                               select new { grouped.Key.Id, grouped.Key.DueDate, Total = grouped.Sum() }).ToListAsync(cancellationToken);
        var adjustmentCardObligations = await (from item in db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
                                               join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on item.InvoiceId equals invoice.Id
                                               join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.WorkspaceId, CardId = invoice.CreditCardId } equals new { card.WorkspaceId, CardId = card.Id }
                                               where item.WorkspaceId == workspaceId && item.Status == Saldo360.Domain.CreditCards.FinancialCreditCardInvoiceAdjustmentStatus.Active
                                                  && invoice.WorkspaceId == workspaceId && invoice.DueDate <= through
                                                  && card.DefaultPaymentAccountId != null && selectedAccountIds.Contains(card.DefaultPaymentAccountId.Value)
                                               group item by new { invoice.Id, invoice.DueDate } into grouped
                                               select new { grouped.Key.Id, grouped.Key.DueDate, Total = grouped.Sum(item => item.Nature == Saldo360.Domain.CreditCards.FinancialCreditCardInvoiceAdjustmentNature.Debit ? item.Amount : -item.Amount) }).ToListAsync(cancellationToken);
        cardObligations = cardObligations.Concat(historicalCardObligations).Concat(adjustmentCardObligations)
            .GroupBy(item => new { item.Id, item.DueDate })
            .Select(group => new { group.Key.Id, group.Key.DueDate, Total = group.Sum(item => item.Total) })
            .ToList();
        var cardPaid = await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(payment => payment.WorkspaceId == workspaceId && payment.Status == Saldo360.Domain.CreditCards.FinancialCreditCardInvoicePaymentStatus.Active)
            .GroupBy(payment => payment.InvoiceId).Select(group => new { Id = group.Key, Amount = group.Sum(payment => payment.Amount) }).ToListAsync(cancellationToken);
        var paidByInvoice = cardPaid.ToDictionary(item => item.Id, item => item.Amount);
        var cardPending = cardObligations.Select(item => new FinancialCalendarDailyTotals(item.DueDate, 0m, Math.Max(0m, item.Total - paidByInvoice.GetValueOrDefault(item.Id)), 0m, 0m)).Where(item => item.Expense > 0m).ToList();
        var allCardObligations = await (from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                                        join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on installment.PurchaseId equals purchase.Id
                                        join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on installment.InvoiceId equals invoice.Id
                                        join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.WorkspaceId, CardId = invoice.CreditCardId } equals new { card.WorkspaceId, CardId = card.Id }
                                        where installment.WorkspaceId == workspaceId && installment.Status == Saldo360.Domain.CreditCards.CreditCardInstallmentStatus.Active
                                           && purchase.WorkspaceId == workspaceId && purchase.Status == Saldo360.Domain.CreditCards.CreditCardPurchaseStatus.Active
                                           && invoice.WorkspaceId == workspaceId && invoice.DueDate <= through
                                        select card.DefaultPaymentAccountId).AnyAsync(account => account == null || !selectedAccountIds.Contains(account.Value), cancellationToken);
        var allHistoricalCardObligations = await (from item in db.FinancialCreditCardImportedInstallments.AsNoTracking()
                                                  join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on item.InvoiceId equals invoice.Id
                                                  join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.WorkspaceId, CardId = invoice.CreditCardId } equals new { card.WorkspaceId, CardId = card.Id }
                                                  where item.WorkspaceId == workspaceId && item.IsHistorical && item.Status == Saldo360.Domain.CreditCards.FinancialCreditCardImportedInstallmentStatus.Active
                                                     && invoice.WorkspaceId == workspaceId && invoice.DueDate <= through
                                                  select card.DefaultPaymentAccountId).AnyAsync(account => account == null || !selectedAccountIds.Contains(account.Value), cancellationToken);
        var allAdjustmentCardObligations = await (from item in db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
                                                  join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on item.InvoiceId equals invoice.Id
                                                  join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.WorkspaceId, CardId = invoice.CreditCardId } equals new { card.WorkspaceId, CardId = card.Id }
                                                  where item.WorkspaceId == workspaceId && item.Status == Saldo360.Domain.CreditCards.FinancialCreditCardInvoiceAdjustmentStatus.Active
                                                     && invoice.WorkspaceId == workspaceId && invoice.DueDate <= through
                                                  select card.DefaultPaymentAccountId).AnyAsync(account => account == null || !selectedAccountIds.Contains(account.Value), cancellationToken);
        allCardObligations = allCardObligations || allHistoricalCardObligations || allAdjustmentCardObligations;
        var knownCardObligations = await (from item in db.FinancialCreditCardImportedInstallments.AsNoTracking()
                                         join card in db.FinancialCreditCards.AsNoTracking() on new { item.WorkspaceId, item.CreditCardId } equals new { card.WorkspaceId, CreditCardId = card.Id }
                                         where item.WorkspaceId == workspaceId && item.InvoiceId == null && item.IsHistorical
                                             && item.Status == Saldo360.Domain.CreditCards.FinancialCreditCardImportedInstallmentStatus.Active
                                             && (item.DueDate == null || item.DueDate <= through)
                                         select new { item.Amount, item.DueDate, card.DefaultPaymentAccountId }).ToListAsync(cancellationToken);
        cardPending.AddRange(knownCardObligations.Where(x => x.DueDate != null && x.DefaultPaymentAccountId != null && selectedAccountIds.Contains(x.DefaultPaymentAccountId.Value))
            .Select(x => new FinancialCalendarDailyTotals(x.DueDate!.Value, 0m, x.Amount, 0m, 0m)));
        allCardObligations |= knownCardObligations.Any(x => x.DueDate == null || x.DefaultPaymentAccountId == null || !selectedAccountIds.Contains(x.DefaultPaymentAccountId.Value));

        IReadOnlyList<FinancialCalendarDailyTotals> periodRealized = [];
        IReadOnlyList<FinancialCalendarDailyTotals> cardPayments = [];
        IReadOnlyList<Saldo360.Application.Budgets.FinancialBudgetExecutionSource> budgets = [];
        if (periodFrom is { } firstPeriodDate)
        {
            var periodRows = await (
                    from movement in db.FinancialTransactions.AsNoTracking()
                    join account in selectedAccounts
                        on new { movement.WorkspaceId, Id = movement.AccountId }
                        equals new { account.WorkspaceId, account.Id }
                    where !movement.IsCancelled
                        && movement.TransactionDate >= firstPeriodDate
                        && movement.TransactionDate <= through
                    group movement by movement.TransactionDate into daily
                    select new
                    {
                        Date = daily.Key,
                        Income = daily.Sum(item => item.Type == CategoryType.Income ? item.Amount : 0m),
                        Expense = daily.Sum(item => item.Type == CategoryType.Expense ? item.Amount : 0m),
                        CardPayments = daily.Sum(item => item.Origin == Saldo360.Domain.Transactions.FinancialTransactionOrigin.CreditCardInvoicePayment ? item.Amount : 0m),
                    })
                .OrderBy(row => row.Date)
                .ToListAsync(cancellationToken);
            periodRealized = periodRows
                .Select(row => new FinancialCalendarDailyTotals(row.Date, row.Income, row.Expense, 0m, 0m))
                .ToList();
            cardPayments = periodRows.Select(row => new FinancialCalendarDailyTotals(row.Date, 0m, row.CardPayments, 0m, 0m)).ToList();
            budgets = await Saldo360.Infrastructure.Budgets.FinancialBudgetExecutionRepository.ReadRangeAsync(
                db, workspaceId, firstPeriodDate, through.AddDays(1), null, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new FinancialCalendarBalanceSnapshot(accounts, realized, pending, periodRealized, cardPending, allCardObligations, debtPending, hasUnallocatedDebt, cardPayments, budgets);
    }
}

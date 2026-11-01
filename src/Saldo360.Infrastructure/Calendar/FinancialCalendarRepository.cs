using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Calendar;
using Saldo360.Application.Recurrences;
using Saldo360.Application.Transactions;
using Saldo360.Infrastructure.Recurrences;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Calendar;

internal sealed class FinancialCalendarRepository(Saldo360DbContext db) : IFinancialCalendarRepository
{
    public async Task<FinancialCalendarSnapshot> ListAsync(
        Guid workspaceId,
        FinancialCalendarFilter filter,
        CancellationToken cancellationToken)
    {
        // As duas coleções precisam representar o mesmo instante lógico: uma realização concorrente não pode fazer
        // a previsão sumir da primeira leitura sem que o lançamento apareça na segunda (ou vice-versa).
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

        var effectiveTransactions = db.FinancialTransactions
            .AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId
                && !t.IsCancelled
                && t.TransactionDate >= filter.From
                && t.TransactionDate <= filter.Through
                && (filter.AccountId == null || t.AccountId == filter.AccountId)
                && (filter.Type == null || t.Type == filter.Type));

        // Regra única de pendência (sem vínculo/fatura/dívida/pagamento conciliado e fora de "a revisar"): PlannedObligationQueries.
        var pendingPlannedTransactions = db.PendingPlanned(workspaceId)
            .Where(p => p.DueDate >= filter.From
                && p.DueDate <= filter.Through
                && (filter.AccountId == null || p.AccountId == filter.AccountId)
                && (filter.Type == null || p.Type == filter.Type));


        var effective =
            from t in effectiveTransactions
            join a in db.FinancialAccounts on new { t.WorkspaceId, Id = t.AccountId } equals new { a.WorkspaceId, a.Id }
            join category in db.FinancialCategories on new { t.WorkspaceId, t.CategoryId } equals new { category.WorkspaceId, CategoryId = (Guid?)category.Id } into categories
            from c in categories.DefaultIfEmpty()
            join materialization in db.FinancialRecurrenceMaterializations on new { t.WorkspaceId, PlannedTransactionId = t.PlannedTransactionId }
                equals new { materialization.WorkspaceId, PlannedTransactionId = (Guid?)materialization.PlannedTransactionId } into materializations
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
                c == null ? null : new TransactionReferenceDto(c.Id, c.Name, c.IsActive, db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.Name).FirstOrDefault()),
                t.Currency,
                t.PlannedTransactionId,
                t.Origin,
                m == null ? null : m.RecurrenceId,
                m == null ? null : m.OccurrenceNumber,
                m == null ? null : m.ScheduledDate);

        var planned =
            from p in pendingPlannedTransactions
            join a in db.FinancialAccounts on new { p.WorkspaceId, Id = p.AccountId } equals new { a.WorkspaceId, a.Id }
            join c in db.FinancialCategories on new { p.WorkspaceId, Id = p.CategoryId } equals new { c.WorkspaceId, c.Id }
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
                new TransactionReferenceDto(c.Id, c.Name, c.IsActive, db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.Name).FirstOrDefault()),
                p.Currency,
                null,
                FinancialTransactionOrigin.Manual,
                m == null ? null : m.RecurrenceId,
                m == null ? null : m.OccurrenceNumber,
                m == null ? null : m.ScheduledDate);

        var effectiveItems = await effective.ToListAsync(cancellationToken);
        var plannedItems = await planned.ToListAsync(cancellationToken);
        var debtRows = await (from i in db.FinancialDebtInstallments.AsNoTracking()
            join d in db.FinancialDebts.AsNoTracking() on new { i.WorkspaceId, DebtId = i.DebtId } equals new { d.WorkspaceId, DebtId = d.Id }
            join a0 in db.FinancialAccounts.AsNoTracking() on new { i.WorkspaceId, Id = d.ResponsibleAccountId } equals new { a0.WorkspaceId, Id = (Guid?)a0.Id } into accounts
            from a in accounts.DefaultIfEmpty()
            where i.WorkspaceId == workspaceId && d.Status != Domain.Debts.DebtStatus.Archived && i.CancelledAt == null
                && i.DueDate >= filter.From && i.DueDate <= filter.Through
                && (filter.AccountId == null || d.ResponsibleAccountId == filter.AccountId)
                && (filter.Type == null || filter.Type == CategoryType.Expense)
            let paid = db.FinancialDebtPayments.Where(p => p.WorkspaceId == workspaceId && p.InstallmentId == i.Id && p.CancelledAt == null).Sum(p => (decimal?)p.Amount) ?? 0m
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
        // Regra única dos indicadores: devolução bancária não é receita e reduz a despesa realizada (ver FinancialTransactionKpi).
        var effectiveTotals = await Saldo360.Infrastructure.Transactions.FinancialTransactionKpi.Project(effectiveTransactions)
            .GroupBy(_ => 1)
            .Select(group => new TypeTotals(group.Sum(item => item.Revenue), filter.Type == CategoryType.Income ? 0m : group.Sum(item => item.NetExpense), group.Sum(item => item.Refund)))
            .SingleOrDefaultAsync(cancellationToken) ?? new TypeTotals(0m, 0m, 0m);
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

        // Despesas fixas: ocorrências virtuais (calculadas, sem gravar), "a revisar" e pagamentos vinculados. Uma obrigação aparece UMA vez:
        // vinculada, o lançamento efetivo ganha a situação "Reconciled" (sem criar evento equivalente); a virtual/previsão correspondente some.
        var obligations = (await FixedObligationReader.LoadAsync(db, workspaceId, filter.From.AddMonths(-1), filter.Through.AddMonths(1), filter.Today, cancellationToken))
            .Where(o => (filter.AccountId == null || o.AccountId == filter.AccountId) && (filter.Type == null || o.Type == filter.Type)).ToList();
        var reconciled = obligations.Where(o => o.TransactionId != null).GroupBy(o => o.TransactionId!.Value).ToDictionary(g => g.Key, g => g.First());
        var shown = obligations.Where(o => o.DueDate >= filter.From && o.DueDate <= filter.Through && o.Situation != ObligationSituation.Realized && (o.IsVirtual || o.Situation == ObligationSituation.NeedsReview)).ToList();
        var accountIds = shown.Select(o => o.AccountId).Distinct().ToList();
        var categoryIds = shown.Select(o => o.CategoryId).Distinct().ToList();
        var accountRefs = await db.FinancialAccounts.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => new TransactionReferenceDto(a.Id, a.Name, a.IsActive), cancellationToken);
        var categoryRows = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && categoryIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.IsActive, Parent = db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.Name).FirstOrDefault() }).ToListAsync(cancellationToken);
        var categoryRefs = categoryRows.ToDictionary(c => c.Id, c => new TransactionReferenceDto(c.Id, c.Name, c.IsActive, c.Parent));
        var obligationItems = shown.Select(o => new FinancialCalendarItemDto(
            o.StableId, o.IsVirtual ? FinancialCalendarSource.RecurrenceOccurrence : FinancialCalendarSource.PlannedTransaction,
            o.Situation == ObligationSituation.NeedsReview ? FinancialCalendarStatus.NeedsReview : o.DueDate < filter.Today ? FinancialCalendarStatus.Overdue : FinancialCalendarStatus.Pending,
            o.Type, o.PlannedAmount, o.DueDate, o.Description, accountRefs.GetValueOrDefault(o.AccountId), categoryRefs.GetValueOrDefault(o.CategoryId), "BRL", null,
            FinancialTransactionOrigin.Manual, o.RecurrenceId, o.OccurrenceNumber, o.DueDate, null, o.ReconciliationStatus)).ToList();
        effectiveItems = effectiveItems.Select(i => reconciled.TryGetValue(i.Id, out var o)
            ? i with { ReconciliationStatus = "Reconciled", ActualAmount = i.Amount, ActualDate = i.Date, TransactionId = i.Id } : i).ToList();
        var virtualPending = obligations.Where(o => o.IsVirtual && o.IsPendingCashFlow && o.DueDate >= filter.From && o.DueDate <= filter.Through).ToList();
        pendingTotals = new PendingTotals(
            pendingTotals.Income + virtualPending.Where(o => o.Type == CategoryType.Income).Sum(o => o.PlannedAmount),
            pendingTotals.Expense + virtualPending.Where(o => o.Type == CategoryType.Expense).Sum(o => o.PlannedAmount),
            pendingTotals.OverdueIncome + virtualPending.Where(o => o.Type == CategoryType.Income && o.DueDate < filter.Today).Sum(o => o.PlannedAmount),
            pendingTotals.OverdueExpense + virtualPending.Where(o => o.Type == CategoryType.Expense && o.DueDate < filter.Today).Sum(o => o.PlannedAmount));
        await transaction.CommitAsync(cancellationToken);

        var items = effectiveItems
            .Concat(obligationItems)
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
                pendingTotals.OverdueExpense + debtOverdue)
            { RealizedRefunds = effectiveTotals.Refunds });
    }

    private sealed record TypeTotals(decimal Income, decimal Expense, decimal Refunds);
    private sealed record PendingTotals(decimal Income, decimal Expense, decimal OverdueIncome, decimal OverdueExpense);
}

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
        Guid workspaceId, Guid budgetId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var result = await ReadRangeAsync(db, workspaceId, null, null, budgetId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result.SingleOrDefault();
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
            .Select(item => new { item.Id, item.BudgetId, item.CategoryId, item.PlannedAmount })
            .ToListAsync(cancellationToken);
        var categoryIds = targets.Select(item => item.CategoryId).ToList();

        var realized = db.FinancialTransactions.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId
                && item.CategoryId != null
                && categoryIds.Contains(item.CategoryId.Value)
                && item.Type == CategoryType.Expense
                && !item.IsCancelled
                && item.TransactionDate >= first
                && item.TransactionDate < through)
            .Select(item => new { CategoryId = item.CategoryId!.Value, item.Amount, IsCreditCard = false, Date = item.TransactionDate });

        var committed = await db.FinancialPlannedTransactions.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId
                && categoryIds.Contains(item.CategoryId)
                && item.Type == CategoryType.Expense
                && !item.IsCancelled
                && item.DueDate >= first
                && item.DueDate < through
                && !db.FinancialTransactions.Any(transactionItem =>
                    transactionItem.WorkspaceId == item.WorkspaceId
                    && transactionItem.PlannedTransactionId == item.Id
                    && !transactionItem.IsCancelled)
                && !db.FinancialPlannedInvoiceReconciliations.Any(reconciliation =>
                    reconciliation.WorkspaceId == item.WorkspaceId
                    && reconciliation.PlannedTransactionId == item.Id
                    && reconciliation.RemovedAt == null))
            .GroupBy(item => new { item.CategoryId, item.DueDate.Year, item.DueDate.Month })
            .Select(group => new CategoryTotal(group.Key.CategoryId, group.Key.Year, group.Key.Month, group.Sum(item => item.Amount)))
            .ToListAsync(cancellationToken);

        var creditCardCommitted = from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                                         join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on installment.PurchaseId equals purchase.Id
                                         join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on installment.InvoiceId equals invoice.Id
                                         where installment.WorkspaceId == workspaceId && installment.Status == Saldo360.Domain.CreditCards.CreditCardInstallmentStatus.Active
                                            && purchase.WorkspaceId == workspaceId && purchase.Status == Saldo360.Domain.CreditCards.CreditCardPurchaseStatus.Active
                                            && invoice.WorkspaceId == workspaceId && invoice.DueDate >= first && invoice.DueDate < through
                                            && categoryIds.Contains(purchase.CategoryId)
                                         select new { CategoryId = purchase.CategoryId, Amount = installment.Amount, IsCreditCard = true, Date = invoice.DueDate };
        var historicalCreditCardCommitted = from item in db.FinancialCreditCardImportedInstallments.AsNoTracking()
                                             join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on item.InvoiceId equals invoice.Id
                                             where item.WorkspaceId == workspaceId && item.IsHistorical && item.Status == Saldo360.Domain.CreditCards.FinancialCreditCardImportedInstallmentStatus.Active
                                                && invoice.WorkspaceId == workspaceId && invoice.DueDate >= first && invoice.DueDate < through
                                                && item.CategoryId != null && categoryIds.Contains(item.CategoryId.Value)
                                             select new { CategoryId = item.CategoryId!.Value, Amount = item.Amount, IsCreditCard = true, Date = invoice.DueDate };
        var debtCommitted = await (from installment in db.FinancialDebtInstallments.AsNoTracking()
                                   join debt in db.FinancialDebts.AsNoTracking() on new { installment.WorkspaceId, DebtId = installment.DebtId } equals new { debt.WorkspaceId, DebtId = debt.Id }
                                   where installment.WorkspaceId == workspaceId && installment.CategoryId != null && categoryIds.Contains(installment.CategoryId.Value)
                                      && installment.DueDate >= first && installment.DueDate < through && installment.CancelledAt == null
                                   let paid = db.FinancialDebtPayments.Where(payment => payment.WorkspaceId == workspaceId && payment.InstallmentId == installment.Id && payment.CancelledAt == null).Sum(payment => (decimal?)payment.Amount) ?? 0m
                                   where installment.ScheduledAmount > paid
                                   select new { CategoryId = installment.CategoryId!.Value, Amount = installment.ScheduledAmount - paid, Date = installment.DueDate }).ToListAsync(cancellationToken);
        var debtRealized = await (from payment in db.FinancialDebtPayments.AsNoTracking()
                                  join installment in db.FinancialDebtInstallments.AsNoTracking() on new { payment.WorkspaceId, InstallmentId = payment.InstallmentId } equals new { installment.WorkspaceId, InstallmentId = installment.Id }
                                  where payment.WorkspaceId == workspaceId && payment.CancelledAt == null && installment.CategoryId != null
                                      && categoryIds.Contains(installment.CategoryId.Value) && payment.PaymentDate >= first && payment.PaymentDate < through
                                  select new { CategoryId = installment.CategoryId!.Value, Amount = payment.Amount, Date = payment.PaymentDate }).ToListAsync(cancellationToken);
        var consumptionRows = await realized.Concat(creditCardCommitted).Concat(historicalCreditCardCommitted)
            .GroupBy(item => new { item.CategoryId, item.IsCreditCard, item.Date.Year, item.Date.Month })
            .Select(group => new ConsumptionTotal(group.Key.CategoryId, group.Key.IsCreditCard, group.Key.Year, group.Key.Month, group.Sum(item => item.Amount)))
            .ToListAsync(cancellationToken);

        return budgets.Select(budget => new FinancialBudgetExecutionSource(
            budget.Id,
            budget.Competence,
            targets.Where(item => item.BudgetId == budget.Id).Select(item => new FinancialBudgetExecutionSourceTarget(
                item.Id,
                item.CategoryId,
                item.PlannedAmount,
                consumptionRows.Where(x => x.CategoryId == item.CategoryId && !x.IsCreditCard && x.Year == budget.Competence.Year && x.Month == budget.Competence.Month).Sum(x => x.Amount)
                    + debtRealized.Where(x => x.CategoryId == item.CategoryId && x.Date.Year == budget.Competence.Year && x.Date.Month == budget.Competence.Month).Sum(x => x.Amount),
                committed.Where(x => x.CategoryId == item.CategoryId && x.Year == budget.Competence.Year && x.Month == budget.Competence.Month).Sum(x => x.Amount)
                    + debtCommitted.Where(x => x.CategoryId == item.CategoryId && x.Date.Year == budget.Competence.Year && x.Date.Month == budget.Competence.Month).Sum(x => x.Amount),
                consumptionRows.Where(x => x.CategoryId == item.CategoryId && x.IsCreditCard && x.Year == budget.Competence.Year && x.Month == budget.Competence.Month).Sum(x => x.Amount))).ToList())).ToList();
    }

    private sealed record CategoryTotal(Guid CategoryId, int Year, int Month, decimal Amount);
    private sealed record ConsumptionRow(Guid CategoryId, decimal Amount, bool IsCreditCard);
    private sealed record ConsumptionTotal(Guid CategoryId, bool IsCreditCard, int Year, int Month, decimal Amount);
}


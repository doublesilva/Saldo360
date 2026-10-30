using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialCreditCardPurchaseRepository(Saldo360DbContext db) : IFinancialCreditCardPurchaseRepository
{
    public async Task<T> InWorkspaceTransactionAsync<T>(Guid workspaceId, Func<Task<T>> operation, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id = {workspaceId} FOR UPDATE", ct);
        try
        {
            var result = await operation();
            if (result is CreditCardPurchaseResult { Access: not CreditCardPurchaseAccess.Allowed } || result is false)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear(); // A rejected schedule must not leak staging into another command.
                return result;
            }
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<bool> CanEditAsync(Guid workspaceId, Guid cardId, Guid purchaseId, DateOnly today, CancellationToken ct)
    {
        var ids = await db.FinancialCreditCardInstallments.Where(i => i.WorkspaceId == workspaceId && i.PurchaseId == purchaseId && i.Status == CreditCardInstallmentStatus.Active && i.InvoiceId != null).Select(i => i.InvoiceId!.Value).ToListAsync(ct);
        return !await db.FinancialCreditCardInvoices.AnyAsync(i => ids.Contains(i.Id) && i.WorkspaceId == workspaceId && i.OfficialVersion > 0, ct)
            && !await db.FinancialCreditCardInvoicePayments.AnyAsync(p => ids.Contains(p.InvoiceId) && p.WorkspaceId == workspaceId && p.Status == FinancialCreditCardInvoicePaymentStatus.Active, ct);
    }
    public async Task<bool> IsInOfficialInvoiceAsync(Guid workspaceId, Guid purchaseId, CancellationToken cancellationToken) =>
        await (from i in db.FinancialCreditCardInstallments.AsNoTracking()
               join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on i.InvoiceId equals invoice.Id
               where i.WorkspaceId == workspaceId && i.PurchaseId == purchaseId && invoice.OfficialVersion > 0
               select i.Id).AnyAsync(cancellationToken);

    public async Task RecordUserCategoryAsync(Guid workspaceId, Guid purchaseId, Guid categoryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var posting = await db.PluggyPostings.SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.CreditCardPurchaseId == purchaseId, cancellationToken);
        posting?.SetCategory(categoryId, Saldo360.Domain.Pluggy.PluggyCategorySource.User, now);
    }

    public Task AddAsync(FinancialCreditCardPurchase purchase, CancellationToken cancellationToken) { db.FinancialCreditCardPurchases.Add(purchase); return Task.CompletedTask; }
    public Task<FinancialCreditCardPurchase?> FindAsync(Guid workspaceId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken) => db.FinancialCreditCardPurchases.Include(p => p.Installments).SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.CreditCardId == cardId && p.Id == purchaseId, cancellationToken);
    public async Task<FinancialCreditCardPurchaseDto?> GetAsync(Guid workspaceId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken) => ToDto(await FindAsync(workspaceId, cardId, purchaseId, cancellationToken));
    public async Task<IReadOnlyList<FinancialCreditCardPurchaseDto>> ListAsync(Guid workspaceId, Guid cardId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken)
    {
        var query = db.FinancialCreditCardPurchases.AsNoTracking().Include(p => p.Installments).Where(p => p.WorkspaceId == workspaceId && p.CreditCardId == cardId);
        if (from is { } start) query = query.Where(p => p.PurchaseDate >= start);
        if (through is { } end) query = query.Where(p => p.PurchaseDate <= end);
        return (await query.OrderBy(p => p.PurchaseDate).ThenBy(p => p.CreatedAt).ToListAsync(cancellationToken)).Select(p => ToDto(p)!).ToList();
    }
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
    public Task<CreditCardCalendarConfig?> GetCardCalendarAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken) => db.FinancialCreditCards.Where(c => c.WorkspaceId == workspaceId && c.Id == cardId).Select(c => new CreditCardCalendarConfig(c.ClosingDay, c.DueDay)).SingleOrDefaultAsync(cancellationToken);
    public Task<FinancialCreditCardInvoice?> FindInvoiceAsync(Guid workspaceId, Guid cardId, DateOnly closingDate, CancellationToken cancellationToken) => db.FinancialCreditCardInvoices.SingleOrDefaultAsync(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && i.ClosingDate == closingDate, cancellationToken);
    public Task AddInvoiceAsync(FinancialCreditCardInvoice invoice, CancellationToken cancellationToken) { db.FinancialCreditCardInvoices.Add(invoice); return Task.CompletedTask; }

    public async Task<IReadOnlyList<FinancialCreditCardInvoiceDto>> ListInvoicesAsync(Guid workspaceId, Guid cardId, string from, string through, DateOnly today, CancellationToken cancellationToken)
    {
        var invoices = (await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId).OrderBy(i => i.ClosingDate).ToListAsync(cancellationToken)).Where(i => string.CompareOrdinal(i.Competence, from) >= 0 && string.CompareOrdinal(i.Competence, through) <= 0).ToList();
        return await MapInvoicesAsync(invoices, today, cancellationToken);
    }

    public async Task<FinancialCreditCardInvoiceDto?> GetInvoiceAsync(Guid workspaceId, Guid cardId, Guid invoiceId, DateOnly today, CancellationToken cancellationToken)
    {
        var invoice = await db.FinancialCreditCardInvoices.AsNoTracking().SingleOrDefaultAsync(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && i.Id == invoiceId, cancellationToken);
        return invoice is null ? null : (await MapInvoicesAsync([invoice], today, cancellationToken)).Single();
    }

    public async Task<bool> CanCancelAsync(Guid workspaceId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken)
    {
        var invoiceIds = await db.FinancialCreditCardInstallments.AsNoTracking().Where(i => i.WorkspaceId == workspaceId && i.PurchaseId == purchaseId && i.InvoiceId != null).Select(i => i.InvoiceId!.Value).Distinct().ToListAsync(cancellationToken);
        foreach (var invoiceId in invoiceIds)
        {
            if (await db.FinancialCreditCardInvoices.AnyAsync(i => i.WorkspaceId == workspaceId && i.Id == invoiceId && i.OfficialVersion > 0, cancellationToken)) return false;
            var paid = await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.InvoiceId == invoiceId && p.Status == FinancialCreditCardInvoicePaymentStatus.Active).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
            var otherTotal = await (from i in db.FinancialCreditCardInstallments.AsNoTracking() join p in db.FinancialCreditCardPurchases.AsNoTracking() on i.PurchaseId equals p.Id where i.WorkspaceId == workspaceId && i.InvoiceId == invoiceId && i.PurchaseId != purchaseId && i.Status == CreditCardInstallmentStatus.Active && p.Status == CreditCardPurchaseStatus.Active select i.Amount).SumAsync(cancellationToken);
            otherTotal += await db.FinancialCreditCardImportedInstallments.AsNoTracking().Where(i => i.WorkspaceId == workspaceId && i.InvoiceId == invoiceId && i.IsHistorical && i.Status == FinancialCreditCardImportedInstallmentStatus.Active).SumAsync(i => (decimal?)i.Amount, cancellationToken) ?? 0m;
            otherTotal += await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking().Where(i => i.WorkspaceId == workspaceId && i.InvoiceId == invoiceId && i.Status == FinancialCreditCardInvoiceAdjustmentStatus.Active).SumAsync(i => (decimal?)(i.Nature == FinancialCreditCardInvoiceAdjustmentNature.Debit ? i.Amount : -i.Amount), cancellationToken) ?? 0m;
            if (otherTotal < paid) return false;
        }
        return true;
    }

    public async Task<IReadOnlyList<FinancialCreditCardInvoiceDto>> ProjectInvoicesAsync(Guid workspaceId, Guid cardId, string from, int months, DateOnly today, CancellationToken cancellationToken)
    {
        var start = DateOnly.ParseExact(from, "yyyy-MM");
        var end = start.AddMonths(months - 1);
        return await ListInvoicesAsync(workspaceId, cardId, start.ToString("yyyy-MM"), end.ToString("yyyy-MM"), today, cancellationToken);
    }

    public async Task<FinancialCreditCardLimitDto?> GetLimitAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken)
    {
        var limit = await db.FinancialCreditCards.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && c.Id == cardId).Select(c => c.CreditLimit).SingleOrDefaultAsync(cancellationToken);
        if (!await db.FinancialCreditCards.AnyAsync(c => c.WorkspaceId == workspaceId && c.Id == cardId, cancellationToken)) return null;
        var obligations = await (from i in db.FinancialCreditCardInstallments.AsNoTracking()
                                 join p in db.FinancialCreditCardPurchases.AsNoTracking() on i.PurchaseId equals p.Id
                                 where i.WorkspaceId == workspaceId && i.Status == CreditCardInstallmentStatus.Active && p.WorkspaceId == workspaceId && p.CreditCardId == cardId && p.Status == CreditCardPurchaseStatus.Active
                                 select i.Amount).SumAsync(cancellationToken);
        obligations += await db.FinancialCreditCardImportedInstallments.AsNoTracking()
            .Where(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && i.IsHistorical
                && i.Status == FinancialCreditCardImportedInstallmentStatus.Active)
            .SumAsync(i => (decimal?)i.Amount, cancellationToken) ?? 0m;
        obligations += await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
            .Where(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && i.Status == FinancialCreditCardInvoiceAdjustmentStatus.Active)
            .SumAsync(i => (decimal?)(i.Nature == FinancialCreditCardInvoiceAdjustmentNature.Debit ? i.Amount : -i.Amount), cancellationToken) ?? 0m;
        var paid = await (from payment in db.FinancialCreditCardInvoicePayments.AsNoTracking()
                          join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on payment.InvoiceId equals invoice.Id
                          where payment.WorkspaceId == workspaceId && payment.Status == FinancialCreditCardInvoicePaymentStatus.Active && invoice.WorkspaceId == workspaceId && invoice.CreditCardId == cardId
                          select payment.Amount).SumAsync(cancellationToken);
        var used = obligations - paid;
        return new FinancialCreditCardLimitDto(Money.Format(limit), Money.Format(used), Money.Format(limit - used), Money.Format(Math.Max(0m, used - limit)));
    }

    public async Task<CreditCardPortfolioDto> GetPortfolioAsync(Guid workspaceId, string competence, DateOnly today, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        // Same financial queries as card details; no frontend sums or fabricated invoices.
        var cards = await db.FinancialCreditCards.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).OrderBy(c => c.Name).ToListAsync(ct);
        var items = new List<CreditCardPortfolioItemDto>();
        foreach (var card in cards)
        {
            var limit = (await GetLimitAsync(workspaceId, card.Id, ct))!;
            var invoices = await ListInvoicesAsync(workspaceId, card.Id, competence, competence, today, ct);
            var known = await db.FinancialCreditCardImportedInstallments.AsNoTracking()
                .Where(i => i.WorkspaceId == workspaceId && i.CreditCardId == card.Id && i.IsHistorical && i.InvoiceId == null && i.Status == FinancialCreditCardImportedInstallmentStatus.Active)
                .Select(i => new KnownCardObligationDto(i.Id, i.Description, Money.Format(i.Amount), i.Competence, i.DueDate, i.InstallmentNumber, i.InstallmentCount, i.SourceSystem, i.ExternalReference)).ToListAsync(ct);
            var used = Parse(limit.UsedLimit);
            var percentage = card.CreditLimit <= 0m ? (used > 0m ? 100m : 0m) : Math.Clamp(used / card.CreditLimit * 100m, 0m, 100m);
            var period = known.Where(i => i.Competence?.ToString("yyyy-MM") == competence).ToList();
            var invoiceTotal = invoices.Sum(i => Parse(i.TotalAmount));
            var unlinkedTotal = period.Sum(i => Parse(i.Amount));
            items.Add(new(card.Id, limit.CreditLimit, limit.UsedLimit, limit.AvailableLimit, decimal.Round(percentage, 2, MidpointRounding.AwayFromZero).ToString("F2", System.Globalization.CultureInfo.InvariantCulture), Money.Format(invoiceTotal), known.Count, Money.Format(known.Sum(i => Parse(i.Amount))))
            {
                UnlinkedPeriodAmount = Money.Format(unlinkedTotal), KnownPeriodTotal = Money.Format(invoiceTotal + unlinkedTotal),
                UnlinkedObligations = period.Concat(known.Where(i => i.Competence == null)).ToList(),
                UnassignedCompetenceCount = known.Count(i => i.Competence == null), IsArchived = card.IsArchived,
                IncompleteDetailsCount = period.Count(i => i.DueDate == null || i.Number == null || i.Count == null || i.SourceSystem == null),
                Invoices = invoices, PaidPeriodAmount = Money.Format(invoices.Sum(i => Parse(i.PaidAmount))),
                OutstandingPeriodAmount = Money.Format(invoices.Sum(i => Parse(i.RemainingAmount)) + unlinkedTotal)
            });
        }
        var active = items.Where(i => !i.IsArchived).ToList();
        var result = new CreditCardPortfolioDto(active.Count, Money.Format(active.Sum(i => Parse(i.CreditLimit))), Money.Format(active.Sum(i => Parse(i.InvoiceTotal))), Money.Format(active.Sum(i => Parse(i.AvailableLimit))), items)
        {
            PeriodInvoiceTotal = Money.Format(items.Sum(i => Parse(i.InvoiceTotal))),
            PeriodPaidTotal = Money.Format(items.Sum(i => Parse(i.PaidPeriodAmount))),
            PeriodOutstandingTotal = Money.Format(items.Sum(i => Parse(i.OutstandingPeriodAmount))),
            PeriodUnlinkedTotal = Money.Format(items.Sum(i => Parse(i.UnlinkedPeriodAmount)))
        };
        await tx.CommitAsync(ct);
        return result;
    }

    private static decimal Parse(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private async Task<List<FinancialCreditCardInvoiceDto>> MapInvoicesAsync(List<FinancialCreditCardInvoice> invoices, DateOnly today, CancellationToken cancellationToken)
    {
        var ids = invoices.Select(i => i.Id).ToList();
        var rows = await (from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                          join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on installment.PurchaseId equals purchase.Id
                          where installment.InvoiceId != null && ids.Contains(installment.InvoiceId.Value) && installment.WorkspaceId == purchase.WorkspaceId && installment.Status == CreditCardInstallmentStatus.Active && purchase.Status == CreditCardPurchaseStatus.Active
                          select new { installment.InvoiceId, installment.Id, installment.PurchaseId, purchase.Description, purchase.InstallmentCount, installment.InstallmentNumber, installment.Amount, installment.Status }).ToListAsync(cancellationToken);
        var historicalRows = await db.FinancialCreditCardImportedInstallments.AsNoTracking()
            .Where(item => item.InvoiceId != null && ids.Contains(item.InvoiceId.Value) && item.IsHistorical && item.Status == FinancialCreditCardImportedInstallmentStatus.Active)
            .Select(item => new { item.InvoiceId, item.Id, item.Description, item.InstallmentCount, item.InstallmentNumber, item.Amount })
            .ToListAsync(cancellationToken);
        var adjustments = await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
            .Where(item => ids.Contains(item.InvoiceId) && item.Status == FinancialCreditCardInvoiceAdjustmentStatus.Active)
            .GroupBy(item => item.InvoiceId)
            .Select(group => new { InvoiceId = group.Key, Amount = group.Sum(item => item.Nature == FinancialCreditCardInvoiceAdjustmentNature.Debit ? item.Amount : -item.Amount) })
            .ToListAsync(cancellationToken);
        var payments = await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(p => ids.Contains(p.InvoiceId) && p.Status == FinancialCreditCardInvoicePaymentStatus.Active).GroupBy(p => p.InvoiceId).Select(g => new { InvoiceId = g.Key, Amount = g.Sum(p => p.Amount) }).ToListAsync(cancellationToken);
        return invoices.Select(invoice => {
            var total = rows.Where(r => r.InvoiceId == invoice.Id).Sum(r => r.Amount)
                + historicalRows.Where(r => r.InvoiceId == invoice.Id).Sum(r => r.Amount)
                + adjustments.Where(r => r.InvoiceId == invoice.Id).Select(r => r.Amount).SingleOrDefault();
            var paid = payments.Where(p => p.InvoiceId == invoice.Id).Select(p => p.Amount).SingleOrDefault();
            var remaining = Math.Max(0m, total - paid);
            var paymentStatus = total <= 0m || paid <= 0m ? InvoicePaymentStatus.Unpaid : remaining <= 0m ? InvoicePaymentStatus.Paid : InvoicePaymentStatus.PartiallyPaid;
            var installmentRows = rows.Where(r => r.InvoiceId == invoice.Id)
                .Select(r => new FinancialCreditCardInvoiceInstallmentDto(r.Id, r.PurchaseId, r.Description, r.InstallmentNumber, r.InstallmentCount, Money.Format(r.Amount)))
                .Concat(historicalRows.Where(r => r.InvoiceId == invoice.Id)
                    .Select(r => new FinancialCreditCardInvoiceInstallmentDto(r.Id, Guid.Empty, r.Description, r.InstallmentNumber, r.InstallmentCount, Money.Format(r.Amount))))
                .OrderBy(r => r.InstallmentNumber).ToList();
            return new FinancialCreditCardInvoiceDto(invoice.Id, invoice.CreditCardId, invoice.Competence, invoice.CycleStartDate, invoice.ClosingDate, invoice.DueDate, invoice.GetStatus(today), Money.Format(total), Money.Format(paid), Money.Format(remaining), paymentStatus, installmentRows, installmentRows.Count);
        }).ToList();
    }
    public Task<bool> IsActiveCardAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken) => db.FinancialCreditCards.AnyAsync(c => c.WorkspaceId == workspaceId && c.Id == cardId && !c.IsArchived, cancellationToken);
    public Task<bool> IsExpenseCategoryAsync(Guid workspaceId, Guid categoryId, Guid? currentCategoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories.AnyAsync(c => c.WorkspaceId == workspaceId && c.Id == categoryId && c.Type == CategoryType.Expense
            && (categoryId == currentCategoryId || c.IsActive && (c.ParentCategoryId == null || db.FinancialCategories.Any(p => p.Id == c.ParentCategoryId && p.IsActive))), cancellationToken);

    private static FinancialCreditCardPurchaseDto? ToDto(FinancialCreditCardPurchase? purchase) => purchase is null ? null : new(
        purchase.Id, purchase.CreditCardId, purchase.CategoryId, purchase.Description, purchase.PurchaseDate, Money.Format(purchase.TotalAmount), purchase.InstallmentCount, purchase.Status,
        purchase.Installments.OrderBy(i => i.InstallmentNumber).Select(i => new FinancialCreditCardInstallmentDto(i.Id, i.InstallmentNumber, Money.Format(i.Amount), i.Status)).ToList(), purchase.PreviouslyPaidInstallments, purchase.FirstPendingCompetence, purchase.Nature);
}

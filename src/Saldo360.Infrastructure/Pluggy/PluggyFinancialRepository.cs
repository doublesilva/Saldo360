using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Pluggy;

internal sealed class PluggyFinancialRepository(Saldo360DbContext db) : IPluggyFinancialRepository
{
    public async Task<IReadOnlyList<PluggyAccountLink>> ListLinksAsync(Guid workspaceId, Guid? connectionId, CancellationToken ct) =>
        await db.PluggyAccountLinks.AsNoTracking().Where(l => l.WorkspaceId == workspaceId && (connectionId == null || l.ConnectionId == connectionId))
            .OrderBy(l => l.LinkedAt).ThenBy(l => l.Id).ToListAsync(ct);

    public Task<PluggyAccountLink?> FindLinkAsync(Guid workspaceId, Guid linkId, CancellationToken ct) =>
        db.PluggyAccountLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorkspaceId == workspaceId && l.Id == linkId, ct);

    public Task<PluggyAccountLink?> FindLinkByExternalAsync(Guid workspaceId, Guid connectionId, string pluggyAccountId, CancellationToken ct) =>
        db.PluggyAccountLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorkspaceId == workspaceId && l.ConnectionId == connectionId && l.PluggyAccountId == pluggyAccountId, ct);

    public async Task<PluggyLinkCreation> CreateLinkAsync(Guid workspaceId, Guid userId, PluggyLinkPlan plan, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id = {workspaceId} FOR UPDATE", ct);
        try
        {
            if (await db.PluggyAccountLinks.AnyAsync(l => l.ConnectionId == plan.ConnectionId && l.PluggyAccountId == plan.PluggyAccountId, ct))
                return Rollback(PluggyLinkOutcome.DuplicateExternal);

            Guid? accountId = null, cardId = null;
            if (plan.Kind == PluggyLinkedAccountKind.Bank)
            {
                if (plan.NewAccount is { } details)
                {
                    var created = FinancialAccount.Create(workspaceId, details, userId, now);
                    db.FinancialAccounts.Add(created);
                    accountId = created.Id;
                }
                else
                {
                    var existing = await db.FinancialAccounts.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.Id == plan.ExistingAccountId)
                        .Select(a => new { a.Id, a.IsActive }).SingleOrDefaultAsync(ct);
                    if (existing is null) return Rollback(PluggyLinkOutcome.TargetNotFound);
                    if (!existing.IsActive) return Rollback(PluggyLinkOutcome.TargetInactive);
                    if (await db.PluggyAccountLinks.AnyAsync(l => l.FinancialAccountId == existing.Id, ct)) return Rollback(PluggyLinkOutcome.DuplicateTarget);
                    accountId = existing.Id;
                }
            }
            else if (plan.NewCard is { } cardDetails)
            {
                var created = FinancialCreditCard.Create(workspaceId, cardDetails, userId, now);
                db.FinancialCreditCards.Add(created);
                cardId = created.Id;
            }
            else
            {
                var existing = await db.FinancialCreditCards.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && c.Id == plan.ExistingCardId)
                    .Select(c => new { c.Id, c.IsArchived }).SingleOrDefaultAsync(ct);
                if (existing is null) return Rollback(PluggyLinkOutcome.TargetNotFound);
                if (existing.IsArchived) return Rollback(PluggyLinkOutcome.TargetInactive);
                if (await db.PluggyAccountLinks.AnyAsync(l => l.CreditCardId == existing.Id, ct)) return Rollback(PluggyLinkOutcome.DuplicateTarget);
                cardId = existing.Id;
            }

            var link = PluggyAccountLink.Create(workspaceId, plan.ConnectionId, plan.PluggyAccountId, plan.Kind, accountId, cardId, plan.DisplayName, plan.InstitutionName,
                plan.PluggyType, plan.PluggySubtype, plan.ImportFrom, userId, now);
            link.Observe(plan.Observed);
            db.PluggyAccountLinks.Add(link);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(PluggyLinkOutcome.Created, link);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return new(pg.ConstraintName == "ux_pluggy_account_links_external" ? PluggyLinkOutcome.DuplicateExternal : PluggyLinkOutcome.DuplicateTarget);
        }

        PluggyLinkCreation Rollback(PluggyLinkOutcome outcome)
        {
            db.ChangeTracker.Clear();
            return new(outcome);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, PluggyPostingCounts>> CountPostingsAsync(Guid workspaceId, CancellationToken ct)
    {
        var rows = await db.PluggyPostings.AsNoTracking().Where(p => p.WorkspaceId == workspaceId)
            .GroupBy(p => new { p.AccountLinkId, p.State }).Select(g => new { g.Key.AccountLinkId, g.Key.State, Count = g.Count() }).ToListAsync(ct);
        return rows.GroupBy(r => r.AccountLinkId).ToDictionary(g => g.Key, g => Counts(g.ToDictionary(r => r.State, r => r.Count)));
    }

    private static PluggyPostingCounts Counts(IReadOnlyDictionary<PluggyPostingState, int> by) => new(
        by.GetValueOrDefault(PluggyPostingState.Applied), by.GetValueOrDefault(PluggyPostingState.WaitingPosted), by.GetValueOrDefault(PluggyPostingState.NeedsReview),
        by.GetValueOrDefault(PluggyPostingState.Covered), by.GetValueOrDefault(PluggyPostingState.Ignored));

    public async Task RefreshObservedAsync(Guid workspaceId, Guid connectionId, IReadOnlyDictionary<string, PluggyObservedFigures> figures, PluggyAccountLinkStatus status, CancellationToken ct)
    {
        var links = await db.PluggyAccountLinks.Where(l => l.WorkspaceId == workspaceId && l.ConnectionId == connectionId).ToListAsync(ct);
        foreach (var link in links)
        {
            link.SetStatus(status);
            if (figures.TryGetValue(link.PluggyAccountId, out var observed)) link.Observe(observed);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertBillsAsync(Guid workspaceId, Guid linkId, IReadOnlyList<PluggyBillFact> bills, DateTimeOffset now, CancellationToken ct)
    {
        if (bills.Count == 0) return;
        var ids = bills.Select(b => b.BillId).Distinct().ToList();
        var existing = await db.PluggyCardBills.Where(b => b.AccountLinkId == linkId && ids.Contains(b.PluggyBillId)).ToDictionaryAsync(b => b.PluggyBillId, StringComparer.Ordinal, ct);
        var persisted = new List<(PluggyCardBill Bill, PluggyBillFact Fact)>();
        foreach (var fact in bills.GroupBy(b => b.BillId).Select(g => g.Last()))
        {
            if (existing.TryGetValue(fact.BillId, out var bill)) bill.Observe(fact.ClosingDate, fact.DueDate, fact.Total, fact.Currency, now);
            else { bill = PluggyCardBill.Create(workspaceId, linkId, fact.BillId, fact.ClosingDate, fact.DueDate, fact.Total, fact.Currency, now); db.PluggyCardBills.Add(bill); }
            bill.ObserveDetails(fact.FinanceChargesTotal, fact.MinimumPayment);
            persisted.Add((bill, fact));
        }
        await db.SaveChangesAsync(ct);

        // Pagamentos informados pelo banco: conjunto substituído a cada observação (idempotente, ordem estável por data e valor).
        var billIds = persisted.Select(p => p.Bill.Id).ToList();
        var old = await db.PluggyCardBillPayments.Where(p => billIds.Contains(p.BillId)).ToListAsync(ct);
        foreach (var (bill, fact) in persisted)
        {
            var wanted = fact.Payments.OrderBy(p => p.Date).ThenBy(p => p.Amount).ToList();
            var current = old.Where(p => p.BillId == bill.Id).OrderBy(p => p.Ordinal).ToList();
            var same = current.Count == wanted.Count && current.Zip(wanted).All(x => x.First.Amount == x.Second.Amount && x.First.PaymentDate == x.Second.Date && x.First.PaymentMode == Clip24(x.Second.Mode) && x.First.ValueType == Clip24(x.Second.ValueType));
            if (same) continue;
            db.PluggyCardBillPayments.RemoveRange(current);
            await db.SaveChangesAsync(ct);
            for (var i = 0; i < wanted.Count; i++) db.PluggyCardBillPayments.Add(PluggyCardBillPayment.Create(workspaceId, bill.Id, i, wanted[i].Amount, wanted[i].Date, wanted[i].Mode, wanted[i].ValueType));
        }
        await db.SaveChangesAsync(ct);
    }

    private static string? Clip24(string? value) => value is { Length: > 24 } ? value[..24] : value;

    public async Task<IReadOnlyList<PluggyBillFact>> ListBillsAsync(Guid workspaceId, Guid linkId, CancellationToken ct)
    {
        var rows = await db.PluggyCardBills.AsNoTracking().Where(b => b.WorkspaceId == workspaceId && b.AccountLinkId == linkId).OrderByDescending(b => b.ClosingDate).ToListAsync(ct);
        var ids = rows.Select(b => b.Id).ToList();
        var payments = (await db.PluggyCardBillPayments.AsNoTracking().Where(p => ids.Contains(p.BillId)).ToListAsync(ct)).GroupBy(p => p.BillId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Ordinal).ToList());
        return rows.Select(b => new PluggyBillFact(b.PluggyBillId, b.ClosingDate, b.DueDate, b.TotalAmount, b.Currency)
        {
            FinanceChargesTotal = b.FinanceChargesTotal, MinimumPayment = b.MinimumPayment,
            Payments = payments.GetValueOrDefault(b.Id)?.Select(p => new PluggyBillPaymentFact(p.Amount, p.PaymentDate, p.PaymentMode, p.ValueType)).ToList() ?? [],
        }).ToList();
    }

    public async Task<IReadOnlyList<PluggyBillStateRow>> ListBillStatesAsync(Guid workspaceId, Guid linkId, int take, CancellationToken ct)
    {
        var link = await db.PluggyAccountLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorkspaceId == workspaceId && l.Id == linkId, ct);
        if (link?.CreditCardId is not { } cardId) return [];
        var bills = await db.PluggyCardBills.AsNoTracking().Where(b => b.WorkspaceId == workspaceId && b.AccountLinkId == linkId && b.ClosingDate != null)
            .OrderByDescending(b => b.ClosingDate).Take(take).ToListAsync(ct);
        var ids = bills.Select(b => b.Id).ToList();
        var reported = (await db.PluggyCardBillPayments.AsNoTracking().Where(p => ids.Contains(p.BillId)).ToListAsync(ct)).GroupBy(p => p.BillId).ToDictionary(g => g.Key, g => g.ToList());
        var closings = bills.Select(b => b.ClosingDate!.Value).ToList();
        var invoices = await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && closings.Contains(i.ClosingDate)).ToListAsync(ct);
        var rows = new List<PluggyBillStateRow>();
        foreach (var bill in bills)
        {
            var invoice = invoices.FirstOrDefault(i => i.ClosingDate == bill.ClosingDate);
            var recorded = invoice is null ? 0m : await Saldo360.Infrastructure.CreditCards.FinancialCreditCardInvoiceTotals.PaidAsync(db, workspaceId, invoice.Id, ct);
            var components = invoice is null ? (decimal?)null : await Saldo360.Infrastructure.CreditCards.FinancialCreditCardInvoiceTotals.TotalAsync(db, workspaceId, invoice.Id, ct);
            var fact = new PluggyBillFact(bill.PluggyBillId, bill.ClosingDate, bill.DueDate, bill.TotalAmount, bill.Currency) { FinanceChargesTotal = bill.FinanceChargesTotal, MinimumPayment = bill.MinimumPayment };
            rows.Add(new(fact, bill.Reconciliation, bill.ReconciliationReason, components ?? bill.ComponentsTotal, invoice is not null, recorded, reported.GetValueOrDefault(bill.Id)?.Sum(p => p.Amount) ?? 0m));
        }
        return rows;
    }

    public async Task MarkSyncedAsync(Guid workspaceId, Guid connectionId, string pluggyAccountId, DateTimeOffset now, CancellationToken ct)
    {
        var link = await db.PluggyAccountLinks.SingleOrDefaultAsync(l => l.WorkspaceId == workspaceId && l.ConnectionId == connectionId && l.PluggyAccountId == pluggyAccountId, ct);
        if (link is null) return;
        link.MarkSynced(now);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetConnectionStatusAsync(Guid workspaceId, Guid connectionId, PluggyAccountLinkStatus status, CancellationToken ct)
    {
        var links = await db.PluggyAccountLinks.Where(l => l.WorkspaceId == workspaceId && l.ConnectionId == connectionId).ToListAsync(ct);
        foreach (var link in links) link.SetStatus(status);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PluggyReviewPage> ListReviewAsync(Guid workspaceId, PluggyReviewFilter filter, CancellationToken ct)
    {
        // Fila = o que ainda não foi aplicado (revisão financeira) + as pontas já contabilizadas com dúvida de vínculo (continuam nos totais até a decisão).
        var query = ReviewQuery(workspaceId).Where(r => (r.Posting.State == PluggyPostingState.NeedsReview || r.Posting.LinkReviewReason != null) && (filter.LinkId == null || r.Link.Id == filter.LinkId));
        var total = await query.CountAsync(ct);
        var accountedInReview = await query.CountAsync(r => r.Posting.State != PluggyPostingState.NeedsReview, ct);
        var rows = await query.OrderBy(r => r.Transaction.Date).ThenBy(r => r.Posting.Id).Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToListAsync(ct);
        var counts = await db.PluggyPostings.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && (filter.LinkId == null || p.AccountLinkId == filter.LinkId))
            .GroupBy(p => p.State).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return new(rows.Select(ToItem).ToArray(), total, filter.Page, filter.PageSize, Counts(counts)) { AccountedInReview = accountedInReview };
    }

    public async Task<PluggyReviewTarget?> FindReviewTargetAsync(Guid workspaceId, Guid postingId, CancellationToken ct)
    {
        var row = await ReviewQuery(workspaceId).Where(r => r.Posting.Id == postingId).Select(r => new { r.Posting.Id, r.Posting.State, r.Transaction.Type, r.Link.Kind }).SingleOrDefaultAsync(ct);
        return row is null ? null : new PluggyReviewTarget(row.Id, row.State, row.Type, row.Kind);
    }

    public async Task SaveResolutionAsync(Guid workspaceId, Guid postingId, PluggyResolution resolution, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var posting = await db.PluggyPostings.SingleAsync(p => p.WorkspaceId == workspaceId && p.Id == postingId, ct);
        posting.Resolve(resolution, userId, now);
        if (resolution == PluggyResolution.Ignore) posting.Ignore("UserIgnored", userId, now);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PluggyReviewItemView?> GetReviewItemAsync(Guid workspaceId, Guid postingId, CancellationToken ct)
    {
        var row = await ReviewQuery(workspaceId).Where(r => r.Posting.Id == postingId).SingleOrDefaultAsync(ct);
        return row is null ? null : ToItem(row);
    }

    public async Task<IReadOnlyList<PluggyTransferPoolRow>> ListTransferPoolAsync(Guid workspaceId, CancellationToken ct)
    {
        var rows = await (from posting in db.PluggyPostings.AsNoTracking()
                          join transaction in db.PluggyImportedTransactions.AsNoTracking() on posting.ImportedTransactionId equals transaction.Id
                          join link in db.PluggyAccountLinks.AsNoTracking() on posting.AccountLinkId equals link.Id
                          where posting.WorkspaceId == workspaceId && link.Kind == PluggyLinkedAccountKind.Bank && transaction.Status == PluggyTransactionStatus.Posted
                              && (posting.State == PluggyPostingState.NeedsReview || posting.State == PluggyPostingState.Applied)
                          select new { posting, transaction, link }).ToListAsync(ct);
        return rows.Select(r => new PluggyTransferPoolRow(ImportedFacts.From(r.transaction, r.link), r.posting.Id, r.link.FinancialAccountId, r.link.DisplayName, r.posting.State, r.posting.Kind,
            r.posting.CounterpartPostingId, r.posting.Resolution)).ToList();
    }

    public async Task<IReadOnlySet<(Guid First, Guid Second)>> ListRejectedPairsAsync(Guid workspaceId, CancellationToken ct) =>
        (await db.PluggyTransferRejections.AsNoTracking().Where(r => r.WorkspaceId == workspaceId).Select(r => new { r.FirstPostingId, r.SecondPostingId }).ToListAsync(ct))
        .Select(r => (r.FirstPostingId, r.SecondPostingId)).ToHashSet();

    public async Task<IReadOnlyList<PluggyRefundCandidateView>> ListRefundCandidatesAsync(Guid workspaceId, DateOnly creditDate, decimal creditAmount, CancellationToken ct)
    {
        var since = creditDate.AddDays(-180);
        var rows = await (from t in db.FinancialTransactions.AsNoTracking()
                          join a in db.FinancialAccounts.AsNoTracking() on t.AccountId equals a.Id
                          join c0 in db.FinancialCategories.AsNoTracking() on t.CategoryId equals (Guid?)c0.Id into categories
                          from c in categories.DefaultIfEmpty()
                          where t.WorkspaceId == workspaceId && t.Type == Saldo360.Domain.Categories.CategoryType.Expense && !t.IsCancelled
                              && t.Origin != Saldo360.Domain.Transactions.FinancialTransactionOrigin.CreditCardInvoicePayment && t.Origin != Saldo360.Domain.Transactions.FinancialTransactionOrigin.DebtInstallmentPayment
                              && t.TransactionDate <= creditDate && t.TransactionDate >= since
                          let refunded = db.FinancialBankRefunds.Where(l => l.WorkspaceId == workspaceId && l.OriginalTransactionId == t.Id && l.CancelledAt == null
                              && db.FinancialTransactions.Any(r => r.Id == l.RefundTransactionId && !r.IsCancelled)).Sum(l => (decimal?)l.Amount) ?? 0m
                          where t.Amount - refunded > 0m
                          orderby t.Amount == creditAmount ? 0 : 1, t.TransactionDate descending, t.Id
                          select new { t.Id, t.TransactionDate, t.Amount, t.Description, AccountName = a.Name, CategoryName = c == null ? null : c.Name,
                              ParentName = c == null ? null : db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.Name).FirstOrDefault(), Remaining = t.Amount - refunded })
            .Take(10).ToListAsync(ct);
        static string F(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        return rows.Select(r => new PluggyRefundCandidateView(r.Id, r.TransactionDate, F(r.Amount), r.Description, r.AccountName,
            r.CategoryName is null ? null : r.ParentName is null ? r.CategoryName : r.ParentName + " / " + r.CategoryName, F(r.Remaining), r.Remaining >= creditAmount,
            r.Remaining >= creditAmount ? null : "ExceedsRefundable")).ToList();
    }

    public async Task<IReadOnlyList<MappableCategory>> ListCategoriesAsync(Guid workspaceId, CancellationToken ct) =>
        await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).ToMappable(db).ToListAsync(ct);

    public Task<DateOnly?> EarliestAppliedDateAsync(Guid workspaceId, Guid linkId, CancellationToken ct) =>
        db.PluggyPostings.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.AccountLinkId == linkId && p.State == PluggyPostingState.Applied)
            .MinAsync(p => p.AppliedDate, ct);

    private IQueryable<ReviewRow> ReviewQuery(Guid workspaceId) =>
        from posting in db.PluggyPostings.AsNoTracking()
        join transaction in db.PluggyImportedTransactions.AsNoTracking() on posting.ImportedTransactionId equals transaction.Id
        join link in db.PluggyAccountLinks.AsNoTracking() on posting.AccountLinkId equals link.Id
        where posting.WorkspaceId == workspaceId
        select new ReviewRow { Posting = posting, Transaction = transaction, Link = link };

    private static PluggyReviewItemView ToItem(ReviewRow r)
    {
        var isDebit = string.Equals(r.Transaction.Type, "DEBIT", StringComparison.OrdinalIgnoreCase);
        var isCredit = string.Equals(r.Transaction.Type, "CREDIT", StringComparison.OrdinalIgnoreCase);
        var amount = r.Transaction.Amount is { } value ? decimal.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : null;
        var accounted = r.Posting.State is PluggyPostingState.Applied or PluggyPostingState.Covered;
        var reason = r.Posting.Reason ?? r.Posting.LinkReviewReason;
        var isRefund = r.Posting.Kind == PluggyPostingKind.BankRefund || (reason is "PossibleRefund" or "PossibleRefundByDescription");
        var isLink = !isRefund && (r.Posting.LinkReviewReason is not null || (reason is not null && LinkReasons.Contains(reason)));
        return new(r.Posting.Id, r.Transaction.Id, r.Link.Id, r.Link.DisplayName, r.Link.Kind.ToString(), r.Posting.State.ToString(), reason,
            r.Transaction.Date is { } d ? PluggyFinancialMapper.BusinessDate(d) : null, amount, isDebit ? "Expense" : isCredit ? "Income" : null,
            PluggyFinancialMapper.Describe(r.Transaction.Description, r.Transaction.DescriptionRaw), r.Transaction.Category, r.Transaction.Status.ToString(), r.Transaction.OperationType,
            // Só o que ainda não foi aplicado aceita decisão financeira; uma ponta contabilizada com dúvida de vínculo se resolve pelas ações do vínculo.
            r.Posting.State == PluggyPostingState.NeedsReview ? PluggyReviewService.AllowedResolutions(r.Link.Kind, r.Transaction.Type) : [], r.Posting.Resolution?.ToString())
        {
            Accounted = accounted, Impact = accounted ? "InTotals" : "OutOfTotals", ReviewKind = isRefund ? "Refund" : isLink ? "Link" : "Evidence",
            LinkReviewReason = r.Posting.LinkReviewReason,
            // Vínculos aplicados antes do registro de origem (legado) foram inferidos pela regra automática, sem versão registrada.
            LinkOrigin = r.Posting.LinkOrigin?.ToString() ?? (r.Posting.State == PluggyPostingState.Applied && r.Posting.Kind == PluggyPostingKind.Transfer ? nameof(PluggyLinkOrigin.Inferred) : null),
            LinkRuleVersion = r.Posting.LinkRuleVersion,
            CounterpartPostingId = r.Posting.Kind == PluggyPostingKind.Transfer ? r.Posting.CounterpartPostingId : null,
            CanUndoLink = r.Posting.State == PluggyPostingState.Applied && r.Posting.Kind == PluggyPostingKind.Transfer && r.Posting.CounterpartPostingId is not null,
        };
    }

    /// <summary>Motivos que pertencem à revisão do vínculo entre movimentos (e não da evidência financeira isolada).</summary>
    private static readonly HashSet<string> LinkReasons = new(StringComparer.Ordinal)
        { "AmbiguousTransferCounterpart", "TransferDateMismatch", "TransferWithoutCounterpart", "TransferInvalid" };

    private sealed class ReviewRow
    {
        public required PluggyPosting Posting { get; init; }
        public required PluggyImportedTransaction Transaction { get; init; }
        public required PluggyAccountLink Link { get; init; }
    }
}

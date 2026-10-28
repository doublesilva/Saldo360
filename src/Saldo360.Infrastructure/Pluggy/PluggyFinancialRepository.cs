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
        foreach (var fact in bills.GroupBy(b => b.BillId).Select(g => g.Last()))
        {
            if (existing.TryGetValue(fact.BillId, out var bill)) bill.Observe(fact.ClosingDate, fact.DueDate, fact.Total, fact.Currency, now);
            else db.PluggyCardBills.Add(PluggyCardBill.Create(workspaceId, linkId, fact.BillId, fact.ClosingDate, fact.DueDate, fact.Total, fact.Currency, now));
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PluggyBillFact>> ListBillsAsync(Guid workspaceId, Guid linkId, CancellationToken ct) =>
        await db.PluggyCardBills.AsNoTracking().Where(b => b.WorkspaceId == workspaceId && b.AccountLinkId == linkId)
            .OrderByDescending(b => b.ClosingDate).Select(b => new PluggyBillFact(b.PluggyBillId, b.ClosingDate, b.DueDate, b.TotalAmount, b.Currency)).ToListAsync(ct);

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
        var query = ReviewQuery(workspaceId).Where(r => r.Posting.State == PluggyPostingState.NeedsReview && (filter.LinkId == null || r.Link.Id == filter.LinkId));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.Transaction.Date).ThenBy(r => r.Posting.Id).Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToListAsync(ct);
        var counts = await db.PluggyPostings.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && (filter.LinkId == null || p.AccountLinkId == filter.LinkId))
            .GroupBy(p => p.State).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return new(rows.Select(ToItem).ToArray(), total, filter.Page, filter.PageSize, Counts(counts));
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

    public async Task<IReadOnlyList<MappableCategory>> ListCategoriesAsync(Guid workspaceId, CancellationToken ct) =>
        await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).Select(c => new MappableCategory(c.Id, c.Name, c.Type, c.IsActive)).ToListAsync(ct);

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
        return new(r.Posting.Id, r.Transaction.Id, r.Link.Id, r.Link.DisplayName, r.Link.Kind.ToString(), r.Posting.State.ToString(), r.Posting.Reason,
            r.Transaction.Date is { } d ? PluggyFinancialMapper.BusinessDate(d) : null, amount, isDebit ? "Expense" : isCredit ? "Income" : null,
            PluggyFinancialMapper.Describe(r.Transaction.Description, r.Transaction.DescriptionRaw), r.Transaction.Category, r.Transaction.Status.ToString(), r.Transaction.OperationType,
            PluggyReviewService.AllowedResolutions(r.Link.Kind, r.Transaction.Type), r.Posting.Resolution?.ToString());
    }

    private sealed class ReviewRow
    {
        public required PluggyPosting Posting { get; init; }
        public required PluggyImportedTransaction Transaction { get; init; }
        public required PluggyAccountLink Link { get; init; }
    }
}

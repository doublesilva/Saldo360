using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Accounts.Reconciliation;
using Saldo360.Domain.Pluggy;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Accounts;

/// <summary>
/// Fatos das contas bancárias integradas para a reconciliação de saldo (T09.5): observações de saldo (âncoras, vigente e anteriores), movimentações
/// POSTED importadas com o estado de cada uma no Saldo360 e a contagem de pendentes. Somente leitura e restrito ao orçamento.
/// </summary>
internal sealed class AccountBalanceReconciliationRepository(Saldo360DbContext db) : IAccountBalanceReconciliationRepository
{
    private static readonly DateOnly UnplaceableDate = new(9999, 12, 30);

    public async Task<IReadOnlyList<IntegratedAccountFacts>> LoadAsync(Guid workspaceId, Guid? accountId, CancellationToken ct)
    {
        var links = await (from link in db.PluggyAccountLinks.AsNoTracking()
                           join account in db.FinancialAccounts.AsNoTracking() on link.FinancialAccountId equals account.Id
                           where link.WorkspaceId == workspaceId && account.WorkspaceId == workspaceId && link.Kind == PluggyLinkedAccountKind.Bank
                              && (accountId == null || account.Id == accountId)
                           orderby account.Name
                           select new { Link = link, Account = account }).ToListAsync(ct);
        if (links.Count == 0) return [];
        var accountIds = links.Select(x => x.Account.Id).ToList();
        var linkIds = links.Select(x => x.Link.Id).ToList();
        var connectionIds = links.Select(x => x.Link.ConnectionId).Distinct().ToList();

        var anchors = (await db.FinancialAccountBalanceAnchors.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && accountIds.Contains(a.AccountId)).OrderBy(a => a.ObservedAt).ToListAsync(ct))
            .GroupBy(a => a.AccountId).ToDictionary(g => g.Key, g => g.ToList());
        var transactions = await db.PluggyImportedTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && connectionIds.Contains(t.ConnectionId) && (t.Status == PluggyTransactionStatus.Posted || t.Status == PluggyTransactionStatus.Pending)).ToListAsync(ct);
        var postings = (await db.PluggyPostings.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && linkIds.Contains(p.AccountLinkId))
            .Select(p => new { p.ImportedTransactionId, p.State, p.Reason }).ToListAsync(ct)).ToDictionary(p => p.ImportedTransactionId);

        var result = new List<IntegratedAccountFacts>();
        foreach (var item in links)
        {
            var link = item.Link;
            var mine = transactions.Where(t => t.ConnectionId == link.ConnectionId && t.PluggyAccountId == link.PluggyAccountId).ToList();
            var posted = mine.Where(t => t.Status == PluggyTransactionStatus.Posted).Select(t =>
            {
                postings.TryGetValue(t.Id, out var posting);
                return new BankMovementFact(t.Date is { } date ? PluggyFinancialMapperDate(date) : UnplaceableDate, Signed(t), posting?.State.ToString() ?? "Unprocessed", posting?.Reason);
            }).ToList();
            var pending = mine.Count(t => t.Status == PluggyTransactionStatus.Pending && t.MissingSyncCount == 0);
            var anchorFacts = anchors.GetValueOrDefault(item.Account.Id, []).Select(a => new AnchorFact(a.ObservedAt, a.AsOfDate, a.Amount, a.Kind.ToString(), a.Source, a.Reliability.ToString(),
                a.UnavailableReason, a.IsCurrent, a.DerivedOpeningAmount, a.DerivedOpeningDate)).ToList();
            result.Add(new(item.Account.Id, item.Account.Name, item.Account.Scope.ToString(), item.Account.IsActive, link.Status.ToString(), link.LastSyncedAt, link.ImportFrom,
                link.OverdraftContractedLimit, link.OverdraftUsedLimit, link.PluggySubtype, anchorFacts, posted, pending));
        }
        return result;
    }

    // Mesma data de negócio que o motor de lançamentos usa para a movimentação.
    private static DateOnly PluggyFinancialMapperDate(DateTimeOffset date) => Saldo360.Application.Pluggy.PluggyFinancialMapper.BusinessDate(date);

    /// <summary>Crédito soma, débito subtrai (valor absoluto). Sem tipo, valor ou em outra moeda não há como assinar com segurança.</summary>
    private static decimal? Signed(PluggyImportedTransaction t)
    {
        if (t.Amount is not { } amount) return null;
        if (t.Currency is { Length: > 0 } currency && !string.Equals(currency, "BRL", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(t.Type, "DEBIT", StringComparison.OrdinalIgnoreCase)) return -Math.Abs(amount);
        if (string.Equals(t.Type, "CREDIT", StringComparison.OrdinalIgnoreCase)) return Math.Abs(amount);
        return null;
    }
}

using Saldo360.Application.Pluggy;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Tests.Pluggy;

/// <summary>Cliente Pluggy simulado: respostas configuráveis, sem rede, e registro das chamadas.</summary>
internal sealed class FakePluggyClient : IPluggyClient
{
    public Dictionary<string, PluggyItem> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<PluggyAccount>> AccountsByItem { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<PluggyTransaction>> TransactionsByAccount { get; } = [];
    public Dictionary<string, List<PluggyBill>> BillsByAccount { get; } = [];
    public Dictionary<string, PluggyException> Failures { get; } = [];
    public List<PluggyLoan> Loans { get; } = [];
    public List<PluggyInvestment> Investments { get; } = [];
    public int TransactionPageSize { get; set; } = 2;
    public List<string> Calls { get; } = [];

    public static PluggyItem Item(string id, string? name = "Itaú", string status = "UPDATED", DateTimeOffset? consentExpiresAt = null, params string[] products) =>
        new(id, 601, name, "PERSONAL_BANK", true, status, "SUCCESS", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-20T10:00:00Z"),
            consentExpiresAt, null, null, products);

    public static PluggyAccount Account(string id, string itemId, string type = "BANK", string number = "12345-6789", decimal? balance = 1500.75m) =>
        new(id, itemId, type, type == "BANK" ? "CHECKING_ACCOUNT" : "CREDIT_CARD", "Conta", null, number, balance, "BRL", DateTimeOffset.Parse("2026-10-20T10:00:00Z"),
            type == "BANK" ? new PluggyBankData("0001/12345-6", balance, null, 500m, 0m, null) : null,
            type == "CREDIT" ? new PluggyCreditData("PLATINUM", "MASTERCARD", DateTimeOffset.Parse("2026-10-10Z"), DateTimeOffset.Parse("2026-10-17Z"), 800m, 5000m, 120m, null, "ACTIVE", "MAIN") : null);

    public static PluggyTransaction Transaction(string id, string accountId, string date, decimal amount, string status = "POSTED", PluggyCardMetadata? card = null) =>
        new(id, accountId, DateTimeOffset.Parse(date + "T12:00:00Z"), "Mercado", "MERCADO LTDA", amount, null, "BRL", amount < 0 ? "DEBIT" : "CREDIT", status,
            "Groceries", "07000000", null, "ITAU", null, null, card, null);

    public Task<PluggyItem> GetItemAsync(string itemId, CancellationToken ct)
    {
        Calls.Add($"item:{itemId}");
        Throw($"item:{itemId}");
        return Items.TryGetValue(itemId, out var item) ? Task.FromResult(item) : throw new PluggyException(PluggyFailureKind.NotFound, 404);
    }

    public Task<IReadOnlyList<PluggyAccount>> ListAccountsAsync(string itemId, CancellationToken ct)
    {
        Calls.Add($"accounts:{itemId}");
        Throw($"accounts:{itemId}");
        return Task.FromResult<IReadOnlyList<PluggyAccount>>(AccountsByItem.GetValueOrDefault(itemId) ?? []);
    }

    public Task<PluggyAccount> GetAccountAsync(string accountId, CancellationToken ct)
    {
        Calls.Add($"account:{accountId}");
        var account = AccountsByItem.Values.SelectMany(x => x).FirstOrDefault(a => a.Id == accountId);
        Throw($"account:{accountId}");
        return account is null ? throw new PluggyException(PluggyFailureKind.NotFound, 404) : Task.FromResult(account);
    }

    public Task<PluggyCursorPage<PluggyTransaction>> ListTransactionsAsync(string accountId, DateOnly? from, DateOnly? to, string? after, CancellationToken ct)
    {
        Calls.Add($"transactions:{accountId}:{after}");
        if (Failures.TryGetValue($"transactions:{accountId}", out var failure)) throw failure;
        var all = (TransactionsByAccount.GetValueOrDefault(accountId) ?? [])
            .Where(t => (from is null || DateOnly.FromDateTime(t.Date!.Value.UtcDateTime) >= from) && (to is null || DateOnly.FromDateTime(t.Date!.Value.UtcDateTime) <= to)).ToList();
        var skip = after is null ? 0 : int.Parse(after);
        var page = all.Skip(skip).Take(TransactionPageSize).ToArray();
        return Task.FromResult(new PluggyCursorPage<PluggyTransaction>(page, skip + TransactionPageSize < all.Count ? (skip + TransactionPageSize).ToString() : null));
    }

    public Task<PluggyPage<PluggyBill>> ListBillsAsync(string accountId, int page, int pageSize, CancellationToken ct)
    {
        Calls.Add($"bills:{accountId}:{page}");
        if (Failures.TryGetValue($"bills:{accountId}", out var failure)) throw failure;
        var all = BillsByAccount.GetValueOrDefault(accountId) ?? [];
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return Task.FromResult(new PluggyPage<PluggyBill>(items, all.Count, Math.Max(1, (int)Math.Ceiling(all.Count / (double)pageSize)), page));
    }

    public Task<IReadOnlyList<PluggyLoan>> ListLoansAsync(string itemId, CancellationToken ct)
    {
        Calls.Add($"loans:{itemId}");
        Throw($"loans:{itemId}");
        return Task.FromResult<IReadOnlyList<PluggyLoan>>(Loans);
    }

    public Task<IReadOnlyList<PluggyInvestment>> ListInvestmentsAsync(string itemId, CancellationToken ct)
    {
        Calls.Add($"investments:{itemId}");
        Throw($"investments:{itemId}");
        return Task.FromResult<IReadOnlyList<PluggyInvestment>>(Investments);
    }

    private void Throw(string key) { if (Failures.TryGetValue(key, out var failure)) throw failure; }
}

internal sealed class InMemoryPluggyLinks : IPluggyItemLinkRepository
{
    public List<PluggyItemLink> Links { get; } = [];
    public Task<IReadOnlyList<PluggyItemLink>> ListAsync(Guid workspaceId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PluggyItemLink>>(Links.Where(l => l.WorkspaceId == workspaceId).ToArray());
    public Task<PluggyItemLink?> FindAsync(Guid workspaceId, Guid id, CancellationToken ct) =>
        Task.FromResult(Links.SingleOrDefault(l => l.WorkspaceId == workspaceId && l.Id == id));
    public Task<bool> TryAddAsync(PluggyItemLink link, CancellationToken ct)
    {
        if (Links.Any(l => l.PluggyItemId == link.PluggyItemId)) return Task.FromResult(false);
        Links.Add(link);
        return Task.FromResult(true);
    }
}

internal sealed class FakeWorkspaces(params (Guid Workspace, Guid User)[] owners) : IFinancialWorkspaceRepository
{
    public Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        Task.FromResult(owners.Any(o => o.Workspace == workspaceId && o.User == userId) ? (FinancialWorkspaceRole?)default(FinancialWorkspaceRole) : null);
    public Task<FinancialWorkspace> EnsurePersonalAsync(Guid userId, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
}

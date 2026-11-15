using Saldo360.Application.Accounts;

namespace Saldo360.Infrastructure.Accounts;

/// <summary>Janela scoped e sequencial de listagens completas de contas, isolada por workspace.</summary>
internal sealed class AccountListSharing : IAccountListSharing
{
    private readonly Dictionary<Guid, IReadOnlyList<FinancialAccountDto>> _cache = [];
    private int _depth, _database, _reused;

    public bool Enabled { get; set; } = true;
    public (int Database, int Reused) Reads => (_database, _reused);

    public IDisposable Begin()
    {
        _depth++;
        return new Window(this);
    }

    public void Invalidate() => _cache.Clear();

    internal async Task<IReadOnlyList<FinancialAccountDto>?> ReadAsync(
        Guid workspaceId, Func<Task<IReadOnlyList<FinancialAccountDto>>> load)
    {
        if (!Enabled || _depth == 0) return null;
        if (_cache.TryGetValue(workspaceId, out var shared)) { _reused++; return shared; }
        var accounts = await load();
        _database++;
        _cache[workspaceId] = accounts;
        return accounts;
    }

    private void End()
    {
        if (--_depth == 0) _cache.Clear();
    }

    private sealed class Window(AccountListSharing owner) : IDisposable
    {
        private bool _closed;
        public void Dispose() { if (_closed) return; _closed = true; owner.End(); }
    }
}

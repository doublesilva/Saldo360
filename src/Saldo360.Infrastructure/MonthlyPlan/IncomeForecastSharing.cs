using System.Runtime.CompilerServices;
using Saldo360.Application.MonthlyPlan;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.MonthlyPlan;

/// <summary>Janela scoped e sequencial de previsões, isolada por recorte e fronteira de consistência.</summary>
internal sealed class IncomeForecastSharing : IIncomeForecastSharing
{
    private static readonly ConditionalWeakTable<Saldo360DbContext, IncomeForecastSharing> ByContext = new();
    private readonly Dictionary<(int Boundary, Guid Workspace, DateOnly From, DateOnly Through, DateOnly Today), IncomeForecastView> _cache = [];
    private readonly Stack<int> _boundaries = [];
    private int _depth, _nextBoundary, _database, _reused;

    public IncomeForecastSharing(Saldo360DbContext db) => ByContext.AddOrUpdate(db, this);

    internal static IncomeForecastSharing? For(Saldo360DbContext db) => ByContext.TryGetValue(db, out var sharing) ? sharing : null;

    public bool Enabled { get; set; } = true;
    public (int Database, int Reused) Reads => (_database, _reused);

    public IDisposable Begin()
    {
        _depth++;
        return new Window(this, null);
    }

    public void Invalidate() => _cache.Clear();

    /// <summary>Cada transação recebe uma identidade exclusiva; seus resultados são removidos ao encerrá-la.</summary>
    internal IDisposable BeginConsistencyBoundary()
    {
        var boundary = ++_nextBoundary;
        _boundaries.Push(boundary);
        return new Window(this, boundary);
    }

    internal async Task<IncomeForecastView> ReadAsync(
        Guid workspaceId, DateOnly from, DateOnly through, DateOnly today, Func<Task<IncomeForecastView>> load)
    {
        if (!Enabled || _depth == 0) return await load();
        var key = (_boundaries.TryPeek(out var boundary) ? boundary : 0, workspaceId, from, through, today);
        if (_cache.TryGetValue(key, out var shared)) { _reused++; return shared; }
        var forecast = await load();
        _database++;
        _cache[key] = forecast;
        return forecast;
    }

    private void End(int? boundary)
    {
        if (boundary is { } id)
        {
            if (_boundaries.Pop() != id) throw new InvalidOperationException("As fronteiras da previsão devem ser encerradas na ordem inversa da abertura.");
            foreach (var key in _cache.Keys.Where(key => key.Boundary == id).ToList()) _cache.Remove(key);
            return;
        }
        if (--_depth == 0)
        {
            _cache.Clear();
            _boundaries.Clear();
        }
    }

    private sealed class Window(IncomeForecastSharing owner, int? boundary) : IDisposable
    {
        private bool _closed;
        public void Dispose() { if (_closed) return; _closed = true; owner.End(boundary); }
    }
}

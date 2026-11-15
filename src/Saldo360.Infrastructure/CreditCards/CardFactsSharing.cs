using System.Runtime.CompilerServices;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

/// <summary>
/// Janela de reaproveitamento por requisição (serviço scoped). Os leitores estáticos que só recebem o contexto de dados encontram a mesma janela pelo contexto, então Agenda, saldo e Plano × Realizado
/// compartilham a mesma leitura. Chave: orçamento e data de referência. Só leituras completas (todos os cartões) entram; consultas de um cartão sempre vão ao banco.
/// </summary>
internal sealed class CardFactsSharing : ICardFactsSharing
{
    private static readonly ConditionalWeakTable<Saldo360DbContext, CardFactsSharing> ByContext = new();
    private readonly Dictionary<(Guid Workspace, DateOnly Today), IReadOnlyList<CardFacts>> _cache = [];
    private int _depth, _database, _reused;

    public CardFactsSharing(Saldo360DbContext db) => ByContext.AddOrUpdate(db, this);

    internal static CardFactsSharing? For(Saldo360DbContext db) => ByContext.TryGetValue(db, out var sharing) ? sharing : null;

    public bool Enabled { get; set; } = true;
    public (int Database, int Reused) Reads => (_database, _reused);

    public IDisposable Begin()
    {
        _depth++;
        return new Window(this);
    }

    public void Invalidate() => _cache.Clear();

    internal async Task<IReadOnlyList<CardFacts>> ReadAsync(Guid workspaceId, DateOnly today, Func<Task<IReadOnlyList<CardFacts>>> load)
    {
        if (!Enabled || _depth == 0) return await load();
        if (_cache.TryGetValue((workspaceId, today), out var shared)) { _reused++; return shared; }
        var facts = await load();
        _database++;
        _cache[(workspaceId, today)] = facts;
        return facts;
    }

    private void End()
    {
        if (--_depth == 0) _cache.Clear();
    }

    private sealed class Window(CardFactsSharing owner) : IDisposable
    {
        private bool _closed;
        public void Dispose() { if (_closed) return; _closed = true; owner.End(); }
    }
}

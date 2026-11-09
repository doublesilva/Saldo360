using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Recurrences;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Recurrences;

/// <summary>
/// Concilia obrigações mensais com movimentações Pluggy JÁ importadas. Só grava a linha de vínculo: não cria, altera nem cancela movimentação alguma,
/// e não muda saldo. Idempotente (uma obrigação e uma movimentação já vinculadas ficam de fora); vínculo confirmado nunca é trocado.
/// </summary>
internal static class FixedObligationReconciler
{
    public const int LookbackDays = 95;

    public static async Task<int> ReconcileAsync(Saldo360DbContext db, Guid workspaceId, DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        var from = today.AddDays(-LookbackDays); var through = today.AddDays(35);
        var obligations = (await FixedObligationReader.LoadAsync(db, workspaceId, from, through, today, ct)).Where(o => o.Situation != ObligationSituation.Realized).ToList();
        if (obligations.Count == 0) return 0;
        var linked = db.FinancialObligationSettlements.Where(s => s.WorkspaceId == workspaceId && s.RemovedAt == null).Select(s => s.FinancialTransactionId);
        var candidates = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.Origin == FinancialTransactionOrigin.PluggyImport && t.PlannedTransactionId == null
                && t.TransactionDate >= from.AddDays(-FixedObligationMatcher.DaysAfter) && t.TransactionDate <= through.AddDays(FixedObligationMatcher.DaysBefore) && !linked.Contains(t.Id))
            .Select(t => new SettlementCandidate(t.Id, t.AccountId, t.Type, t.Description, t.Amount, t.TransactionDate)).ToListAsync(ct);
        // Vínculo bancário persistido (PROJ-05): a transação que informou o valor, agora efetivada, quita a obrigação (evidência mais forte que descrição + valor). Sem nova despesa:
        // a movimentação já existe; só a linha de vínculo é gravada.
        var byLink = await LinkedSettlementsAsync(db, workspaceId, obligations, candidates, ct);
        var linkedObligations = byLink.Select(p => p.Obligation.StableId).ToHashSet(); var linkedTransactions = byLink.Select(p => p.Transaction.Id).ToHashSet();
        // Histórico da própria série bancária (movimentações já importadas, vinculadas ou não): só evidência, nunca é alterado.
        var historyFrom = from.AddMonths(-13);
        var history = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.Origin == FinancialTransactionOrigin.PluggyImport && t.TransactionDate >= historyFrom && t.TransactionDate <= through)
            .Select(t => new SettlementCandidate(t.Id, t.AccountId, t.Type, t.Description, t.Amount, t.TransactionDate)).ToListAsync(ct);
        var pairs = byLink.Concat(FixedObligationMatcher.UniquePairs(obligations.Where(o => !linkedObligations.Contains(o.StableId)).ToList(), candidates.Where(c => !linkedTransactions.Contains(c.Id)).ToList(), history)).ToList();
        foreach (var (o, t) in pairs)
            db.FinancialObligationSettlements.Add(FinancialObligationSettlement.Create(workspaceId, o.RecurrenceId, o.Competence, o.PlannedTransactionId, t.Id, SettlementOrigin.Auto, now));
        if (pairs.Count == 0) return 0;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Concorrência (outra passada vinculou antes): descarta as linhas pendentes; a próxima execução reavalia.
            foreach (var entry in db.ChangeTracker.Entries<FinancialObligationSettlement>().Where(e => e.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
            return 0;
        }
        return pairs.Count;
    }

    /// <summary>Obrigações com transação bancária de referência cuja movimentação efetivada (postagem aplicada) existe, está livre e é do mesmo tipo e conta: quitação por vínculo.</summary>
    private static async Task<List<(FixedObligation Obligation, SettlementCandidate Transaction)>> LinkedSettlementsAsync(Saldo360DbContext db, Guid workspaceId,
        IReadOnlyList<FixedObligation> obligations, IReadOnlyList<SettlementCandidate> candidates, CancellationToken ct)
    {
        var withSource = obligations.Where(o => o.SourceTransactionId != null && o.RecurrenceId != null).ToList();
        if (withSource.Count == 0) return [];
        var sources = withSource.Select(o => o.SourceTransactionId!.Value).ToList();
        var posted = await db.PluggyPostings.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && sources.Contains(p.ImportedTransactionId) && p.State == PluggyPostingState.Applied && p.Kind == PluggyPostingKind.BankMovement && p.FinancialTransactionId != null)
            .Select(p => new { p.ImportedTransactionId, TransactionId = p.FinancialTransactionId!.Value }).ToListAsync(ct);
        var byTransaction = candidates.ToDictionary(c => c.Id);
        var result = new List<(FixedObligation, SettlementCandidate)>(); var usedTransactions = new HashSet<Guid>();
        foreach (var o in withSource)
        {
            var link = posted.FirstOrDefault(p => p.ImportedTransactionId == o.SourceTransactionId);
            if (link is null || !byTransaction.TryGetValue(link.TransactionId, out var candidate) || candidate.AccountId != o.AccountId || candidate.Type != o.Type || !usedTransactions.Add(candidate.Id)) continue;
            result.Add((o, candidate));
        }
        return result;
    }
}

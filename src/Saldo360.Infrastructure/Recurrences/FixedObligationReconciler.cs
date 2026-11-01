using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Recurrences;
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
        var pairs = FixedObligationMatcher.UniquePairs(obligations, candidates);
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
}

using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Recurrences;

/// <summary>Regra ÚNICA de "previsão pendente": substitui as cópias que Agenda, saldo projetado, conta, orçamento e plano-realizado mantinham.</summary>
internal static class PlannedObligationQueries
{
    /// <summary>
    /// Pendente = não cancelada, sem lançamento efetivo vinculado, sem substituição por fatura/parcela de dívida, SEM pagamento vinculado
    /// (conciliação de despesa fixa) e fora de "a revisar" histórico. Previsão vinculada já está no realizado: contá-la de novo seria dupla contagem.
    /// </summary>
    public static IQueryable<FinancialPlannedTransaction> PendingPlanned(this Saldo360DbContext db, Guid workspaceId) =>
        db.FinancialPlannedTransactions.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && !p.IsCancelled && !p.NeedsHistoricalReview
            && !db.FinancialTransactions.Any(t => t.WorkspaceId == p.WorkspaceId && t.PlannedTransactionId == p.Id && !t.IsCancelled)
            && !db.FinancialPlannedInvoiceReconciliations.Any(r => r.WorkspaceId == p.WorkspaceId && r.PlannedTransactionId == p.Id && r.RemovedAt == null)
            && !db.FinancialDebtInstallmentReconciliations.Any(r => r.WorkspaceId == p.WorkspaceId && r.PlannedTransactionId == p.Id && r.RemovedAt == null)
            && !db.FinancialObligationSettlements.Any(s => s.WorkspaceId == p.WorkspaceId && s.PlannedTransactionId == p.Id && s.RemovedAt == null));
}

/// <summary>
/// Leitor único das obrigações mensais (despesas fixas). Ocorrências de recorrência são VIRTUAIS (calculadas, nunca gravadas em leitura);
/// materializada, exceção de competência ou pagamento vinculado SUPRIMEM a virtual da mesma obrigação.
/// </summary>
internal static class FixedObligationReader
{
    public static async Task<List<FixedObligation>> LoadAsync(Saldo360DbContext db, Guid workspaceId, DateOnly from, DateOnly through, DateOnly today, CancellationToken ct)
    {
        var result = new List<FixedObligation>();
        var currentMonth = Month(today);
        var settlements = await db.FinancialObligationSettlements.AsNoTracking().Where(s => s.WorkspaceId == workspaceId && s.RemovedAt == null).ToListAsync(ct);
        var txIds = settlements.Select(s => s.FinancialTransactionId).ToList();
        var transactions = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && (txIds.Contains(t.Id) || t.PlannedTransactionId != null))
            .Select(t => new { t.Id, t.Amount, t.TransactionDate, t.PlannedTransactionId }).ToListAsync(ct);
        var txById = transactions.ToDictionary(t => t.Id);
        var txByPlanned = transactions.Where(t => t.PlannedTransactionId != null).GroupBy(t => t.PlannedTransactionId!.Value).ToDictionary(g => g.Key, g => g.First());
        var settleByRecurrence = settlements.Where(s => s.RecurrenceId != null && txById.ContainsKey(s.FinancialTransactionId)).ToDictionary(s => (s.RecurrenceId!.Value, s.Competence));
        var settleByPlanned = settlements.Where(s => s.PlannedTransactionId != null && txById.ContainsKey(s.FinancialTransactionId)).ToDictionary(s => s.PlannedTransactionId!.Value);

        var materializations = await db.FinancialRecurrenceMaterializations.AsNoTracking().Where(m => m.WorkspaceId == workspaceId).ToListAsync(ct);
        var materializedBySeq = materializations.Select(m => (m.RecurrenceId, m.OccurrenceNumber)).ToHashSet();
        var materializationByPlanned = materializations.Where(m => m.PlannedTransactionId != null).GroupBy(m => m.PlannedTransactionId!.Value).ToDictionary(g => g.Key, g => g.First());
        var planned = await db.FinancialPlannedTransactions.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.DueDate >= from && p.DueDate <= through
                && !db.FinancialPlannedInvoiceReconciliations.Any(r => r.WorkspaceId == p.WorkspaceId && r.PlannedTransactionId == p.Id && r.RemovedAt == null)
                && !db.FinancialDebtInstallmentReconciliations.Any(r => r.WorkspaceId == p.WorkspaceId && r.PlannedTransactionId == p.Id && r.RemovedAt == null))
            .ToListAsync(ct);
        var recurrences = await db.FinancialRecurrences.AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId && r.FinancialAccountId != null && r.Origin == RecurrenceOrigin.Manual).ToListAsync(ct);
        var baseAmountByRecurrence = recurrences.ToDictionary(r => r.Id, r => r.Amount);
        // Previsões que representam obrigações fixas: exceção de competência, ocorrência materializada, histórica a revisar ou já vinculada.
        foreach (var p in planned.Where(p => !p.IsCancelled))
        {
            materializationByPlanned.TryGetValue(p.Id, out var mat);
            var related = p.ExceptionRecurrenceId != null || mat != null || p.NeedsHistoricalReview || settleByPlanned.ContainsKey(p.Id);
            if (!related) continue;
            var recurrenceId = p.ExceptionRecurrenceId ?? mat?.RecurrenceId;
            var competence = p.ExceptionCompetence ?? Month(mat?.ScheduledDate ?? p.DueDate);
            Guid? txId = null; decimal? amount = null; DateOnly? date = null;
            FinancialObligationSettlement? settlement = null;
            if (!settleByPlanned.TryGetValue(p.Id, out settlement) && recurrenceId != null) settleByRecurrence.TryGetValue((recurrenceId.Value, competence), out settlement);
            if (settlement != null) { var t = txById[settlement.FinancialTransactionId]; txId = t.Id; amount = t.Amount; date = t.TransactionDate; }
            else if (txByPlanned.TryGetValue(p.Id, out var rt)) { txId = rt.Id; amount = rt.Amount; date = rt.TransactionDate; }
            var situation = txId != null ? ObligationSituation.Realized : p.NeedsHistoricalReview ? ObligationSituation.NeedsReview : ObligationSituation.Planned;
            // Prioridade do valor da competência: real confirmado > ajuste (exceção/previsão) > valor-base da recorrência. Confirmar não é pagar.
            decimal? baseAmount = recurrenceId is { } rid && baseAmountByRecurrence.TryGetValue(rid, out var b) ? b : null;
            var source = p.ConfirmedAmount != null ? ObligationAmountSource.Confirmed
                : p.ExceptionRecurrenceId != null || (baseAmount != null && p.Amount != baseAmount) ? ObligationAmountSource.Adjustment : ObligationAmountSource.Base;
            result.Add(new(workspaceId, recurrenceId, mat?.OccurrenceNumber, competence, p.Id, p.AccountId, p.CategoryId, p.Type, p.Nature, p.Description,
                p.EffectiveAmount, false, p.DueDate, situation, false, amount, date, txId, baseAmount, source, p.Amount));
        }

        // Exceções (inclusive canceladas = mês pulado de propósito) suprimem a ocorrência virtual daquela competência.
        var exceptions = (await db.FinancialPlannedTransactions.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.ExceptionRecurrenceId != null).Select(p => new { p.ExceptionRecurrenceId, p.ExceptionCompetence }).ToListAsync(ct))
            .Select(p => (p.ExceptionRecurrenceId!.Value, p.ExceptionCompetence!.Value)).ToHashSet();

        foreach (var r in recurrences)
        {
            foreach (var o in FinancialRecurrenceCalendar.CalculateOccurrences(r, from, through))
            {
                var competence = Month(o.Date);
                if (materializedBySeq.Contains((r.Id, o.Sequence)) || exceptions.Contains((r.Id, competence))) continue;
                if (settleByRecurrence.TryGetValue((r.Id, competence), out var s))
                {
                    var t = txById[s.FinancialTransactionId];
                    result.Add(new(workspaceId, r.Id, o.Sequence, competence, null, r.FinancialAccountId!.Value, r.FinancialCategoryId, r.Type, r.Nature, r.Description,
                        r.Amount, r.IsEstimated, o.Date, ObligationSituation.Realized, true, t.Amount, t.TransactionDate, t.Id, r.Amount));
                    continue;
                }
                // Competência fechada sem evidência de pagamento: "a revisar", nunca dívida vencida confirmada.
                var situation = competence < currentMonth ? ObligationSituation.NeedsReview : ObligationSituation.Planned;
                result.Add(new(workspaceId, r.Id, o.Sequence, competence, null, r.FinancialAccountId!.Value, r.FinancialCategoryId, r.Type, r.Nature, r.Description,
                    r.Amount, r.IsEstimated, o.Date, situation, true, null, null, null, r.Amount));
            }
        }
        return result.OrderBy(i => i.DueDate).ThenBy(i => i.Description, StringComparer.Ordinal).ToList();
    }

    /// <summary>Virtuais ainda pendentes (fluxo futuro). As previsões comuns vêm de <see cref="PlannedObligationQueries.PendingPlanned"/>.</summary>
    public static async Task<List<FixedObligation>> LoadVirtualPendingAsync(Saldo360DbContext db, Guid workspaceId, DateOnly from, DateOnly through, DateOnly today, CancellationToken ct) =>
        through < from ? [] // leitura "até o fim do mês anterior" (compromissos em aberto antes da competência) não tem janela futura
            : (await LoadAsync(db, workspaceId, from, through, today, ct)).Where(i => i.IsVirtual && i.IsPendingCashFlow).ToList();

    private static DateOnly Month(DateOnly d) => new(d.Year, d.Month, 1);
}

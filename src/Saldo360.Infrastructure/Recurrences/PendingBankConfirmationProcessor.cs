using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Calendar;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Calendar;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Recurrences;

public sealed record PendingBankConfirmationReport(int Confirmed, int Updated, int SourceReplaced, int Reverted, int Ambiguous)
{
    public int Total => Confirmed + Updated + SourceReplaced + Reverted + Ambiguous;
}

/// <summary>
/// Passo pós-importação Pluggy (PROJ-05): o valor informado pelo banco para uma despesa fixa vira o valor confirmado da competência SEM ação do usuário, quando a associação é segura
/// (<see cref="PendingBankMatcher"/>: correspondência segura única ou evidência histórica da própria recorrência). Grava na previsão (reaproveita a PROJ-04), registra origem, transação e
/// auditoria, e é idempotente: reexecutar não muda nada. Nunca sobrescreve confirmação manual nem vínculo manual; débito ambíguo não é associado e tem o diagnóstico registrado.
/// Valor-base da recorrência e demais competências nunca mudam; um débito pendente informa o valor, mas não comprova pagamento (o pagamento é a conciliação da movimentação efetivada).
/// </summary>
internal static class PendingBankConfirmationProcessor
{
    /// <summary>Uma pendente deixa de ser vista pela Pluggy: só é "definitivamente invalidada" após este número de coletas sem aparecer E esta idade, nunca por uma ausência isolada.</summary>
    public const int DefinitiveMissingSyncCount = 3;
    public static readonly TimeSpan DefinitiveMissingAge = TimeSpan.FromHours(48);
    public const int ReplacementDays = 3;
    public const int HorizonMonths = 3;

    private sealed record BankRow(Guid Id, Guid AccountId, decimal Amount, DateOnly Date, string Description, PluggyTransactionStatus Status, int MissingSyncCount, DateTimeOffset? FirstMissingAt);

    public static async Task<PendingBankConfirmationReport> ProcessAsync(Saldo360DbContext db, Guid workspaceId, DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        var own = db.Database.CurrentTransaction is null;
        await using var transaction = own ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            // Uma passada por workspace por vez (sincronizações concorrentes se serializam); idempotente por construção.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({"pending-bank-confirmation:" + workspaceId.ToString("N")}))", ct);
            var report = await RunAsync(db, workspaceId, today, now, ct);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return report;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static async Task<PendingBankConfirmationReport> RunAsync(Saldo360DbContext db, Guid workspaceId, DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        var owner = await db.FinancialWorkspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => (Guid?)w.OwnerUserId).SingleOrDefaultAsync(ct);
        if (owner is null) return new(0, 0, 0, 0, 0);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var through = today.AddMonths(HorizonMonths);
        var all = await FixedObligationReader.LoadAsync(db, workspaceId, monthStart, through, today, ct);
        var eligible = all.Where(o => o.Type == CategoryType.Expense && o.RecurrenceId != null && o.Situation == ObligationSituation.Planned).ToList();
        if (eligible.Count == 0) return new(0, 0, 0, 0, 0);

        var recurrenceIds = eligible.Select(o => o.RecurrenceId!.Value).Distinct().ToList();
        var recurrences = await db.FinancialRecurrences.Where(r => r.WorkspaceId == workspaceId && recurrenceIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var history = await LoadEvidenceAsync(db, workspaceId, monthStart, recurrences, await LoadHistoryAsync(db, workspaceId, recurrenceIds, ct), ct);
        var accountIds = eligible.Select(o => o.AccountId).Distinct().ToList();
        var rows = await LoadBankRowsAsync(db, workspaceId, accountIds, through, ct);
        var rowById = rows.ToDictionary(r => r.Id);
        var pending = (await PendingBankReader.LoadDebitsAsync(db, workspaceId, accountIds, through, ct)).Where(d => d.Amount > 0m).ToList();
        var linkedIds = (await db.FinancialPlannedTransactions.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.AmountSourceTransactionId != null && !p.IsCancelled)
            .Select(p => p.AmountSourceTransactionId!.Value).ToListAsync(ct)).ToHashSet();

        int confirmed = 0, updated = 0, replaced = 0, reverted = 0;
        void Count(CompetenceAmountOutcome outcome, AmountConfirmationEventKind? expected = null)
        {
            if (outcome.Status != CompetenceAmountStatus.Ok || outcome.Result is not { Changed: true }) return;
            switch (expected)
            {
                case AmountConfirmationEventKind.SourceReplaced: replaced++; break;
                case AmountConfirmationEventKind.Updated: updated++; break;
                default: confirmed++; break;
            }
        }

        // A) Mantém as confirmações automáticas existentes: valor alterado, fonte efetivada, trocada, ausente ou definitivamente invalidada.
        foreach (var o in eligible.Where(o => o.ConfirmationSource == nameof(AmountConfirmationSource.PluggyAuto) && o.SourceTransactionId != null))
        {
            var source = o.SourceTransactionId!.Value; var recurrence = recurrences[o.RecurrenceId!.Value];
            if (pending.FirstOrDefault(d => d.Id == source) is { } current)
            {
                if (current.Amount != o.PlannedAmount)
                    Count(await CompetenceAmountWriter.ConfirmAsync(db, recurrence, o.Competence, current.Amount, AmountConfirmationSource.PluggyAuto, null, source, owner.Value, now, ct), AmountConfirmationEventKind.Updated);
                continue;
            }
            rowById.TryGetValue(source, out var row);
            if (row is { Status: PluggyTransactionStatus.Posted }) continue;        // Pending → Posted: o valor confirmado fica; o pagamento é a conciliação da movimentação
            if (row is not null && !DefinitivelyMissing(row, now)) continue;        // ausência temporária ou estado desconhecido: sem evidência para reverter
            var replacement = FindReplacement(rows, o, row, linkedIds);
            if (replacement is not null)
            {
                linkedIds.Add(replacement.Id);
                Count(await CompetenceAmountWriter.ConfirmAsync(db, recurrence, o.Competence, replacement.Amount, AmountConfirmationSource.PluggyAuto, null, replacement.Id, owner.Value, now, ct),
                    replacement.Amount == o.PlannedAmount ? AmountConfirmationEventKind.SourceReplaced : AmountConfirmationEventKind.Updated);
                continue;
            }
            if (await RevertAsync(db, workspaceId, o, source, now, ct)) { reverted++; linkedIds.Remove(source); }
        }

        // B) Novas associações seguras (obrigação nunca confirmada × débito pendente ainda sem dono). Confirmação manual e vínculo manual ficam de fora.
        var unconfirmed = eligible.Where(o => o.ConfirmationSource == null).ToList();
        var free = pending.Where(d => !linkedIds.Contains(d.Id)).ToList();
        var matches = PendingBankMatcher.Match(free, unconfirmed, history);
        foreach (var m in matches.OrderBy(m => m.Obligation.DueDate).ThenBy(m => m.Debit.Id))
        {
            var outcome = await CompetenceAmountWriter.ConfirmAsync(db, recurrences[m.Obligation.RecurrenceId!.Value], m.Obligation.Competence, m.Debit.Amount,
                AmountConfirmationSource.PluggyAuto, null, m.Debit.Id, owner.Value, now, ct);
            if (outcome.Status == CompetenceAmountStatus.Ok) linkedIds.Add(m.Debit.Id);
            Count(outcome);
        }

        // C) Ambiguidade: nada é associado; registra candidatos, evidências e impacto (uma vez por situação) para evoluir as regras.
        var matchedDebits = matches.Select(m => m.Debit.Id).ToHashSet(); var matchedObligations = matches.Select(m => m.Obligation.StableId).ToHashSet();
        var openObligations = unconfirmed.Where(o => !matchedObligations.Contains(o.StableId)).ToList();
        var ambiguous = await RecordAmbiguitiesAsync(db, workspaceId, free.Where(d => !matchedDebits.Contains(d.Id)).ToList(), openObligations, history, now, ct);
        return new(confirmed, updated, replaced, reverted, ambiguous);
    }

    private static bool DefinitivelyMissing(BankRow row, DateTimeOffset now) =>
        row.Status == PluggyTransactionStatus.Pending && row.MissingSyncCount >= DefinitiveMissingSyncCount && row.FirstMissingAt is { } first && now - first >= DefinitiveMissingAge;

    /// <summary>Fonte efetivada/trocada por outro identificador: só com par único (mesma conta, perto da data e do valor confirmado, ainda sem dono).</summary>
    private static BankRow? FindReplacement(IReadOnlyList<BankRow> rows, FixedObligation o, BankRow? old, ISet<Guid> linkedIds)
    {
        var reference = old?.Date ?? o.DueDate;
        var found = rows.Where(r => r.Status == PluggyTransactionStatus.Posted && r.Id != old?.Id && !linkedIds.Contains(r.Id) && r.AccountId == o.AccountId
            && Math.Abs(r.Date.DayNumber - reference.DayNumber) <= ReplacementDays
            && Math.Abs(r.Amount - o.PlannedAmount) <= o.PlannedAmount * PendingBankMatcher.HistoryAmountTolerance).Take(2).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    private static async Task<bool> RevertAsync(Saldo360DbContext db, Guid workspaceId, FixedObligation o, Guid source, DateTimeOffset now, CancellationToken ct)
    {
        if (o.PlannedTransactionId is not { } plannedId) return false;
        var planned = await db.FinancialPlannedTransactions.SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.Id == plannedId, ct);
        var previous = planned?.ConfirmedAmount;
        if (planned is null || !planned.RevertAutomaticConfirmation()) return false;
        db.FinancialAmountConfirmationEvents.Add(FinancialAmountConfirmationEvent.Create(workspaceId, o.RecurrenceId, o.Competence, plannedId, AmountConfirmationEventKind.Reverted,
            AmountConfirmationSource.PluggyAuto, previous, planned.Amount, source, "SourceInvalidated", null, now));
        return true;
    }

    private static async Task<int> RecordAmbiguitiesAsync(Saldo360DbContext db, Guid workspaceId, IReadOnlyList<PendingBankDebit> debits, IReadOnlyList<FixedObligation> obligations,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> history, DateTimeOffset now, CancellationToken ct)
    {
        var diagnoses = debits.Select(d => (Debit: d, Evidence: PendingBankMatcher.Diagnose(d, obligations, history))).Where(x => x.Evidence.Count > 0).ToList();
        if (diagnoses.Count == 0) return 0;
        var ids = diagnoses.Select(x => x.Debit.Id).ToList();
        var known = (await db.FinancialAmountConfirmationEvents.AsNoTracking().Where(e => e.WorkspaceId == workspaceId && e.Kind == AmountConfirmationEventKind.Ambiguous && e.SourceTransactionId != null && ids.Contains(e.SourceTransactionId.Value))
            .Select(e => new { e.SourceTransactionId, e.Details }).ToListAsync(ct)).Select(e => (e.SourceTransactionId!.Value, e.Details)).ToHashSet();
        var recorded = 0;
        foreach (var (debit, evidence) in diagnoses)
        {
            var reason = evidence.All(e => !e.HasHistory) ? "NoHistoryEvidence" : evidence.Count > 1 ? "MultipleCandidates" : "DescriptionIncompatible";
            var details = JsonSerializer.Serialize(new
            {
                reason, impactAmount = debit.Amount,
                debit = new { debit.Id, debit.AccountId, debit.Amount, date = debit.Date.ToString("yyyy-MM-dd"), debit.Description },
                candidates = evidence.Select(e => new { e.ObligationId, e.RecurrenceId, e.Description, e.Amount, dueDate = e.DueDate.ToString("yyyy-MM-dd"), e.DateDeltaDays, e.AmountDeltaPercent, e.SafeMatcherDescription, e.HistoryEvidence, e.HasHistory }),
                missing = reason == "NoHistoryEvidence" ? "Nenhuma competência anterior paga desta recorrência para comparar a descrição bancária." : "Descrição bancária diferente da descrição da obrigação e do histórico.",
            });
            if (known.Contains((debit.Id, details))) continue;
            db.FinancialAmountConfirmationEvents.Add(FinancialAmountConfirmationEvent.Create(workspaceId, evidence.Count == 1 ? evidence[0].RecurrenceId : null, null, null, AmountConfirmationEventKind.Ambiguous,
                null, null, debit.Amount, debit.Id, reason, details, now));
            recorded++;
        }
        return recorded;
    }

    /// <summary>
    /// Histórico completo de cada recorrência: descrições das competências anteriores pagas e conciliadas + (PROJ-05) a movimentação efetivada do mês anterior que cobriu a obrigação mesmo
    /// sem ter sido conciliada (par único por conta, data e valor). Só recorrências sem conciliação do mês anterior precisam dessa segunda fonte.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> LoadEvidenceAsync(Saldo360DbContext db, Guid workspaceId, DateOnly monthStart,
        IReadOnlyDictionary<Guid, FinancialRecurrence> recurrences, IReadOnlyDictionary<Guid, IReadOnlyList<string>> settled, CancellationToken ct)
    {
        var previousStart = monthStart.AddMonths(-1); var previousEnd = monthStart.AddDays(-1);
        var settledPrevious = (await db.FinancialObligationSettlements.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.RemovedAt == null && s.RecurrenceId != null && s.Competence == previousStart).Select(s => s.RecurrenceId!.Value).ToListAsync(ct)).ToHashSet();
        var occurrences = new List<PriorOccurrence>();
        foreach (var r in recurrences.Values.Where(r => r.FinancialAccountId != null && !settledPrevious.Contains(r.Id)))
        {
            var found = FinancialRecurrenceCalendar.CalculateOccurrences(r, previousStart, previousEnd);
            if (found.Count == 1) occurrences.Add(new(r.Id, r.FinancialAccountId!.Value, found[0].Date, r.Amount));
        }
        if (occurrences.Count == 0) return settled;
        var from = occurrences.Min(o => o.Due).AddDays(-PendingBankMatcher.HistoryDays); var to = occurrences.Max(o => o.Due).AddDays(PendingBankMatcher.HistoryDays);
        var linked = db.FinancialObligationSettlements.Where(s => s.WorkspaceId == workspaceId && s.RemovedAt == null).Select(s => s.FinancialTransactionId);
        var movements = (await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.Origin == FinancialTransactionOrigin.PluggyImport && t.Type == CategoryType.Expense && t.PlannedTransactionId == null
                && t.TransactionDate >= from && t.TransactionDate <= to && !linked.Contains(t.Id))
            .Select(t => new PostedMovement(t.Id, t.AccountId, t.TransactionDate, t.Amount, t.Description)).ToListAsync(ct));
        var extra = PriorPeriodEvidence.Select(occurrences, movements);
        if (extra.Count == 0) return settled;
        var merged = settled.ToDictionary(p => p.Key, p => p.Value);
        foreach (var (recurrenceId, description) in extra)
            merged[recurrenceId] = merged.TryGetValue(recurrenceId, out var current) ? current.Append(description).Distinct().ToList() : [description];
        return merged;
    }

    /// <summary>Descrições bancárias das competências anteriores JÁ PAGAS de cada recorrência: o que o banco escreveu quando essa obrigação foi efetivamente quitada.</summary>
    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> LoadHistoryAsync(Saldo360DbContext db, Guid workspaceId, IReadOnlyCollection<Guid> recurrenceIds, CancellationToken ct)
    {
        var rows = await (from s in db.FinancialObligationSettlements.AsNoTracking()
                          join t in db.FinancialTransactions.AsNoTracking() on s.FinancialTransactionId equals t.Id
                          where s.WorkspaceId == workspaceId && s.RemovedAt == null && s.RecurrenceId != null && recurrenceIds.Contains(s.RecurrenceId.Value) && !t.IsCancelled
                          select new { RecurrenceId = s.RecurrenceId!.Value, t.Description }).ToListAsync(ct);
        return rows.GroupBy(r => r.RecurrenceId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Description).Distinct().ToList());
    }

    private static async Task<List<BankRow>> LoadBankRowsAsync(Saldo360DbContext db, Guid workspaceId, IReadOnlyCollection<Guid> accountIds, DateOnly through, CancellationToken ct)
    {
        var rows = await (from t in db.PluggyImportedTransactions.AsNoTracking()
                          join l in db.PluggyAccountLinks.AsNoTracking() on new { t.ConnectionId, t.PluggyAccountId } equals new { l.ConnectionId, l.PluggyAccountId }
                          where t.WorkspaceId == workspaceId && l.WorkspaceId == workspaceId && l.Kind == PluggyLinkedAccountKind.Bank && l.FinancialAccountId != null && accountIds.Contains(l.FinancialAccountId.Value)
                             && (t.Currency == null || t.Currency == "BRL") && t.Amount != null && t.Date != null
                          select new { t.Id, t.Type, t.Amount, t.Date, t.Description, t.DescriptionRaw, t.Status, t.MissingSyncCount, t.FirstMissingAt, AccountId = l.FinancialAccountId!.Value, l.ImportFrom }).ToListAsync(ct);
        return rows.Where(r => string.Equals(r.Type, "DEBIT", StringComparison.OrdinalIgnoreCase))
            .Select(r => new { r, Date = Saldo360.Application.Pluggy.PluggyFinancialMapper.BusinessDate(r.Date!.Value) })
            .Where(x => x.r.ImportFrom is not { } from || x.Date >= from)
            .Select(x => new BankRow(x.r.Id, x.r.AccountId, Math.Abs(x.r.Amount!.Value), x.Date, $"{x.r.Description} {x.r.DescriptionRaw}".Trim(), x.r.Status, x.r.MissingSyncCount, x.r.FirstMissingAt)).ToList();
    }
}

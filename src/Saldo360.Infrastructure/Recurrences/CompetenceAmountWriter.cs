using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Recurrences;

/// <summary>
/// Única escrita do valor confirmado de UMA competência de despesa fixa (PROJ-04 manual; PROJ-05 Pluggy automática e vínculo manual). Reaproveita a exceção de competência:
/// a previsão da competência (materializada ou exceção) recebe o valor; sem ela, cria-se a exceção. Nunca altera o valor-base da recorrência nem outras competências, e confirmar
/// não é pagar. Idempotente (mesmo valor, origem e transação = nada muda, nenhuma auditoria nova), nunca sobrescreve confirmação manual por sincronização e registra cada mudança.
/// </summary>
internal static class CompetenceAmountWriter
{
    public static async Task<CompetenceAmountOutcome> ConfirmInTransactionAsync(Saldo360DbContext db, FinancialRecurrence recurrence, DateOnly competence, decimal amount,
        AmountConfirmationSource source, Guid? userId, Guid? bankTransactionId, Guid createdByUserId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var outcome = await ConfirmAsync(db, recurrence, competence, amount, source, userId, bankTransactionId, createdByUserId, now, ct);
            if (outcome.Status == CompetenceAmountStatus.Ok) { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
            else { await transaction.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); }
            return outcome;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Aplica a confirmação dentro da transação do chamador (que salva): trava a recorrência, valida e grava a previsão e a auditoria.</summary>
    public static async Task<CompetenceAmountOutcome> ConfirmAsync(Saldo360DbContext db, FinancialRecurrence recurrence, DateOnly competence, decimal amount,
        AmountConfirmationSource source, Guid? userId, Guid? bankTransactionId, Guid createdByUserId, DateTimeOffset now, CancellationToken ct)
    {
        // O banco guarda valores com 4 casas (numeric(19,4)): zeros à direita não contam; centavos fracionados nunca são arredondados.
        amount = Normalize(amount);
        if (amount.Scale > 2) return Rejected(CompetenceAmountStatus.InvalidCompetence, "O valor tem mais de duas casas decimais.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM financial_recurrences WHERE id = {recurrence.Id} AND workspace_id = {recurrence.WorkspaceId} FOR UPDATE", ct);
        await db.Entry(recurrence).ReloadAsync(ct);
        if (recurrence.Status != RecurrenceStatus.Active) return Rejected(CompetenceAmountStatus.Conflict, "A recorrência não está ativa.");
        if (recurrence.Origin != RecurrenceOrigin.Manual || recurrence.FinancialAccountId is not { } accountId)
            return Rejected(CompetenceAmountStatus.Conflict, "Somente despesas fixas de conta cadastradas manualmente aceitam valor por competência.");
        var first = new DateOnly(competence.Year, competence.Month, 1);
        var occurrences = FinancialRecurrenceCalendar.CalculateOccurrences(recurrence, first, first.AddMonths(1).AddDays(-1));
        if (occurrences.Count != 1) return Rejected(CompetenceAmountStatus.InvalidCompetence, occurrences.Count == 0 ? "A recorrência não tem ocorrência nesta competência." : "A competência tem mais de uma ocorrência; use uma recorrência mensal.");
        var occurrence = occurrences[0];

        var planned = await (from m in db.FinancialRecurrenceMaterializations
                             join p in db.FinancialPlannedTransactions on m.PlannedTransactionId equals p.Id
                             where m.WorkspaceId == recurrence.WorkspaceId && m.RecurrenceId == recurrence.Id && m.OccurrenceNumber == occurrence.Sequence
                             select p).SingleOrDefaultAsync(ct)
            ?? await db.FinancialPlannedTransactions.SingleOrDefaultAsync(p => p.WorkspaceId == recurrence.WorkspaceId && p.ExceptionRecurrenceId == recurrence.Id
                && p.ExceptionCompetence == first && !p.IsCancelled, ct);
        var paid = await db.FinancialObligationSettlements.AnyAsync(s => s.WorkspaceId == recurrence.WorkspaceId && s.RecurrenceId == recurrence.Id && s.Competence == first && s.RemovedAt == null, ct);
        if (paid) return Rejected(CompetenceAmountStatus.Conflict, "A competência já foi paga; o valor pago prevalece.");
        if (planned is not null && source == AmountConfirmationSource.PluggyAuto && planned.IsManuallyConfirmed)
            return Rejected(CompetenceAmountStatus.Conflict, "Confirmação manual é protegida: a sincronização não a sobrescreve.");
        if (bankTransactionId is { } txId
            && await db.FinancialPlannedTransactions.AnyAsync(p => p.WorkspaceId == recurrence.WorkspaceId && p.AmountSourceTransactionId == txId && !p.IsCancelled && (planned == null || p.Id != planned.Id), ct))
            return Rejected(CompetenceAmountStatus.Conflict, "A transação bancária já está vinculada a outra obrigação.");

        var created = false;
        if (planned is null)
        {
            var skipped = await db.FinancialPlannedTransactions.AnyAsync(p => p.WorkspaceId == recurrence.WorkspaceId && p.ExceptionRecurrenceId == recurrence.Id && p.ExceptionCompetence == first, ct);
            if (skipped) return Rejected(CompetenceAmountStatus.Conflict, "A competência foi cancelada (mês pulado).");
            // Competência fechada sem previsão é "a revisar": criar exceção aqui a transformaria em dívida vencida.
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (first < new DateOnly(today.Year, today.Month, 1)) return Rejected(CompetenceAmountStatus.InvalidCompetence, "Competência fechada: o histórico não é alterado.");
            var details = PlannedTransactionDetails.TryCreate(accountId, recurrence.FinancialCategoryId, recurrence.Type.ToString(), recurrence.Description, recurrence.Amount, occurrence.Date, out var errors, recurrence.Nature.ToString());
            if (details is null) return Rejected(CompetenceAmountStatus.Conflict, string.Join(" ", errors.SelectMany(x => x.Value)));
            planned = FinancialPlannedTransaction.Create(recurrence.WorkspaceId, details, createdByUserId, now);
            planned.MarkAsCompetenceException(recurrence.Id, first);
            db.FinancialPlannedTransactions.Add(planned);
            created = true;
        }
        else if (planned.IsCancelled || !await db.PendingPlanned(recurrence.WorkspaceId).AnyAsync(p => p.Id == planned.Id, ct))
            return Rejected(CompetenceAmountStatus.Conflict, "A obrigação da competência já foi paga, substituída ou está em revisão.");

        var previous = planned.ConfirmedAmount; var previousSource = planned.ConfirmationSource ?? (previous is null ? null : AmountConfirmationSource.Manual); var previousTx = planned.AmountSourceTransactionId;
        var unchanged = previous == amount && previousSource == source && previousTx == bankTransactionId;
        if (!unchanged)
        {
            if (source == AmountConfirmationSource.Manual) planned.ConfirmCompetenceAmount(amount, userId!.Value, now);
            else planned.ConfirmFromBank(amount, userId, now, source, bankTransactionId!.Value);
            var kind = previous is null ? AmountConfirmationEventKind.Confirmed
                : previous == amount && previousSource == source ? AmountConfirmationEventKind.SourceReplaced : AmountConfirmationEventKind.Updated;
            db.FinancialAmountConfirmationEvents.Add(FinancialAmountConfirmationEvent.Create(recurrence.WorkspaceId, recurrence.Id, first, planned.Id, kind, source, previous, amount, bankTransactionId,
                source switch { AmountConfirmationSource.PluggyAuto => "PluggyAutoMatch", AmountConfirmationSource.ManualLink => "ManualBankLink", _ => "ManualConfirmation" }, null, now));
        }
        return new(CompetenceAmountStatus.Ok, null, new(recurrence.Id, first, planned.Id, planned.DueDate, recurrence.Amount, planned.Amount, amount, planned.AmountConfirmedAt ?? now, created, source.ToString(), !unchanged));
    }

    public static async Task<CompetenceAmountOutcome> LinkBankTransactionAsync(Saldo360DbContext db, FinancialRecurrence recurrence, DateOnly competence, Guid pluggyTransactionId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        if (recurrence.FinancialAccountId is not { } accountId) return Rejected(CompetenceAmountStatus.Conflict, "Somente despesas fixas de conta aceitam vínculo bancário.");
        var tx = (await LoadBankDebitsAsync(db, recurrence.WorkspaceId, accountId, ct)).SingleOrDefault(t => t.Id == pluggyTransactionId);
        if (tx is null) return Rejected(CompetenceAmountStatus.InvalidCompetence, "A transação não é um débito da conta desta despesa.");
        return await ConfirmInTransactionAsync(db, recurrence, competence, tx.Amount, AmountConfirmationSource.ManualLink, userId, tx.Id, userId, now, ct);
    }

    public static async Task<IReadOnlyList<BankTransactionCandidate>> BankCandidatesAsync(Saldo360DbContext db, FinancialRecurrence recurrence, DateOnly competence, CancellationToken ct)
    {
        if (recurrence.FinancialAccountId is not { } accountId) return [];
        var first = new DateOnly(competence.Year, competence.Month, 1);
        var occurrences = FinancialRecurrenceCalendar.CalculateOccurrences(recurrence, first, first.AddMonths(1).AddDays(-1));
        if (occurrences.Count != 1) return [];
        var due = occurrences[0].Date;
        var taken = await db.FinancialPlannedTransactions.AsNoTracking().Where(p => p.WorkspaceId == recurrence.WorkspaceId && p.AmountSourceTransactionId != null && !p.IsCancelled).Select(p => p.AmountSourceTransactionId!.Value).ToHashSetAsync(ct);
        var rows = (await LoadBankDebitsAsync(db, recurrence.WorkspaceId, accountId, ct)).Where(t => !taken.Contains(t.Id)).ToList();
        return rows.Where(t => t.Date >= due.AddDays(-FixedObligationMatcher.DaysBefore) && t.Date <= due.AddDays(FixedObligationMatcher.DaysAfter) && Math.Abs(t.Amount - recurrence.Amount) <= recurrence.Amount * 0.5m)
            .OrderBy(t => Math.Abs(t.Date.DayNumber - due.DayNumber)).ThenBy(t => Math.Abs(t.Amount - recurrence.Amount))
            .Select(t => new BankTransactionCandidate(t.Id, t.Date, t.Amount, t.Description, t.Status)).ToList();
    }

    private sealed record BankDebit(Guid Id, DateOnly Date, decimal Amount, string Description, string Status);

    /// <summary>Débitos BRL (pendentes ou efetivados) das contas bancárias integradas vinculadas à conta informada.</summary>
    private static async Task<List<BankDebit>> LoadBankDebitsAsync(Saldo360DbContext db, Guid workspaceId, Guid accountId, CancellationToken ct)
    {
        var rows = await (from t in db.PluggyImportedTransactions.AsNoTracking()
                          join l in db.PluggyAccountLinks.AsNoTracking() on new { t.ConnectionId, t.PluggyAccountId } equals new { l.ConnectionId, l.PluggyAccountId }
                          where t.WorkspaceId == workspaceId && l.WorkspaceId == workspaceId && l.Kind == PluggyLinkedAccountKind.Bank && l.FinancialAccountId == accountId
                             && (t.Status == PluggyTransactionStatus.Pending || t.Status == PluggyTransactionStatus.Posted)
                             && (t.Currency == null || t.Currency == "BRL") && t.Amount != null && t.Date != null
                          select new { t.Id, t.Type, t.Date, t.Amount, t.Description, t.DescriptionRaw, t.Status }).ToListAsync(ct);
        return rows.Where(t => string.Equals(t.Type, "DEBIT", StringComparison.OrdinalIgnoreCase))
            .Select(t => new BankDebit(t.Id, Saldo360.Application.Pluggy.PluggyFinancialMapper.BusinessDate(t.Date!.Value), Math.Abs(t.Amount!.Value), $"{t.Description} {t.DescriptionRaw}".Trim(), t.Status.ToString())).ToList();
    }


    private static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    private static CompetenceAmountOutcome Rejected(CompetenceAmountStatus status, string message) => new(status, message, null);
}

using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Recurrences;
using Saldo360.Application.Planning;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Recurrences;

internal sealed class FinancialRecurrenceMaterializationRepository(Saldo360DbContext db) : IFinancialRecurrenceMaterializationRepository
{
    public Task<bool> HasAnyAsync(Guid workspaceId, Guid recurrenceId, CancellationToken ct) => db.FinancialRecurrenceMaterializations.AnyAsync(x => x.WorkspaceId == workspaceId && x.RecurrenceId == recurrenceId, ct);

    public async Task<MaterializationResultDto> MaterializeAsync(FinancialRecurrence recurrence, IReadOnlyList<RecurrenceOccurrenceDto> occurrences, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM financial_recurrences WHERE id = {recurrence.Id} AND workspace_id = {recurrence.WorkspaceId} FOR UPDATE", ct);
            // Re-read under lock: a concurrent archive/edit must not materialize a stale definition.
            await db.Entry(recurrence).ReloadAsync(ct);
            if (recurrence.Status != RecurrenceStatus.Active) throw new InvalidOperationException("Recorrência inativa.");
            if (occurrences.Count > 0) occurrences = FinancialRecurrenceCalendar.CalculateOccurrences(recurrence, occurrences.Min(x => x.Date), occurrences.Max(x => x.Date));
            if (recurrence.FinancialCreditCardId is { } cardId)
            {
                var result = await MaterializeCardAsync(recurrence, cardId, occurrences, userId, now, ct);
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return result;
            }
            var numbers = occurrences.Select(x => x.Sequence).ToArray();
            var existing = await db.FinancialRecurrenceMaterializations
                .Where(x => x.WorkspaceId == recurrence.WorkspaceId && x.RecurrenceId == recurrence.Id && numbers.Contains(x.OccurrenceNumber))
                .ToDictionaryAsync(x => x.OccurrenceNumber, ct);
            var existingPlannedIds = existing.Values.Select(x => x.PlannedTransactionId).ToArray();
            var existingPlanned = await db.FinancialPlannedTransactions
                .Where(x => existingPlannedIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, ct);
            var realizedPlannedIds = await db.FinancialTransactions.AsNoTracking()
                .Where(x => existingPlannedIds.Contains(x.PlannedTransactionId!.Value) && !x.IsCancelled)
                .Select(x => x.PlannedTransactionId!.Value)
                .ToHashSetAsync(ct);
            var items = new List<MaterializationItemDto>(occurrences.Count);
            var created = 0;
            foreach (var occurrence in occurrences)
            {
                if (existing.ContainsKey(occurrence.Sequence))
                {
                    var materialization = existing[occurrence.Sequence];
                    var existingTransaction = existingPlanned[materialization.PlannedTransactionId!.Value];
                    var status = existingTransaction.IsCancelled
                        ? PlannedTransactionStatus.Cancelled
                        : realizedPlannedIds.Contains(existingTransaction.Id) ? PlannedTransactionStatus.Realized : PlannedTransactionStatus.Pending;
                    items.Add(new(occurrence.Sequence, materialization.ScheduledDate, existingTransaction.Id, existingTransaction.DueDate, existingTransaction.Amount, status, "Existing"));
                    continue;
                }

                var details = PlannedTransactionDetails.TryCreate(recurrence.FinancialAccountId!.Value, recurrence.FinancialCategoryId, recurrence.Type.ToString(), recurrence.Description, recurrence.Amount, occurrence.Date, out var errors, recurrence.Nature.ToString());
                if (details is null) throw new InvalidOperationException($"Não foi possível materializar a ocorrência {occurrence.Sequence}: {string.Join(" ", errors.SelectMany(x => x.Value))}");
                var planned = FinancialPlannedTransaction.Create(recurrence.WorkspaceId, details, userId, now);
                db.FinancialPlannedTransactions.Add(planned);
                db.FinancialRecurrenceMaterializations.Add(FinancialRecurrenceMaterialization.Create(recurrence.WorkspaceId, recurrence.Id, occurrence.Sequence, occurrence.Date, planned.Id, now));
                items.Add(new(occurrence.Sequence, occurrence.Date, planned.Id, occurrence.Date, occurrence.Amount, PlannedTransactionStatus.Pending, "Created"));
                created++;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(recurrence.Id, created, occurrences.Count - created, items);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<MaterializationResultDto> MaterializeCardAsync(FinancialRecurrence r, Guid cardId, IReadOnlyList<RecurrenceOccurrenceDto> occurrences, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var card = await db.FinancialCreditCards.SingleAsync(x => x.WorkspaceId == r.WorkspaceId && x.Id == cardId && !x.IsArchived, ct);
        var materialized = await db.FinancialRecurrenceMaterializations.Where(x => x.WorkspaceId == r.WorkspaceId && x.RecurrenceId == r.Id).ToDictionaryAsync(x => x.OccurrenceNumber, ct);
        var invoices = await db.FinancialCreditCardInvoices.Where(x => x.WorkspaceId == r.WorkspaceId && x.CreditCardId == cardId).ToListAsync(ct);
        var items = new List<MaterializationItemDto>();
        var created = 0;
        foreach (var o in occurrences)
        {
            if (materialized.TryGetValue(o.Sequence, out var previous))
            {
                var purchase = await db.FinancialCreditCardPurchases.SingleAsync(x => x.WorkspaceId == r.WorkspaceId && x.Id == previous.CreditCardPurchaseId, ct);
                items.Add(new(o.Sequence, previous.ScheduledDate, null, null, purchase.TotalAmount, null, "Existing", purchase.Id));
                continue;
            }
            // Occurrence is the scheduled recurring charge date, not a bank due date or an invoice payment.
            var charge = FinancialCreditCardPurchase.Create(r.WorkspaceId, cardId, r.FinancialCategoryId, r.Description, o.Date, o.Amount, 1, userId, now, r.Nature);
            var cycle = CreditCardInvoiceCalendarCalculator.ForInstallment(o.Date, 1, card.ClosingDay, card.DueDay);
            var invoice = invoices.SingleOrDefault(x => x.ClosingDate == cycle.ClosingDate);
            if (invoice is null)
            {
                invoice = FinancialCreditCardInvoice.Create(r.WorkspaceId, cardId, cycle.Competence, cycle.CycleStartDate, cycle.ClosingDate, cycle.DueDate, now);
                db.FinancialCreditCardInvoices.Add(invoice); invoices.Add(invoice);
            }
            charge.Installments.Single().AssignInvoice(invoice.Id);
            db.FinancialCreditCardPurchases.Add(charge);
            db.FinancialRecurrenceMaterializations.Add(FinancialRecurrenceMaterialization.CreateCard(r.WorkspaceId, r.Id, o.Sequence, o.Date, charge.Id, now));
            items.Add(new(o.Sequence, o.Date, null, null, o.Amount, null, "Created", charge.Id)); created++;
        }
        return new(r.Id, created, occurrences.Count - created, items);
    }

    public async Task<IReadOnlyList<MaterializationQueryItem>> ListAsync(Guid workspaceId, Guid recurrenceId, DateOnly fromDate, DateOnly throughDate, CancellationToken ct)
    {
        var bank = await (from materialization in db.FinancialRecurrenceMaterializations.AsNoTracking()
               join planned in db.FinancialPlannedTransactions.AsNoTracking() on materialization.PlannedTransactionId equals planned.Id
               join realization in db.FinancialTransactions.AsNoTracking().Where(x => !x.IsCancelled)
                   on planned.Id equals realization.PlannedTransactionId into realizations
               from realization in realizations.DefaultIfEmpty()
               where materialization.WorkspaceId == workspaceId && materialization.RecurrenceId == recurrenceId && materialization.ScheduledDate >= fromDate && materialization.ScheduledDate <= throughDate
               orderby materialization.OccurrenceNumber
               select new MaterializationQueryItem(materialization.OccurrenceNumber, materialization.ScheduledDate, planned.Id, planned.DueDate, planned.Amount,
                   planned.IsCancelled ? PlannedTransactionStatus.Cancelled : realization != null ? PlannedTransactionStatus.Realized : PlannedTransactionStatus.Pending)).ToListAsync(ct);
        var cards = await (from m in db.FinancialRecurrenceMaterializations.AsNoTracking()
                           join p in db.FinancialCreditCardPurchases.AsNoTracking() on m.CreditCardPurchaseId equals p.Id
                           where m.WorkspaceId == workspaceId && p.WorkspaceId == workspaceId && m.RecurrenceId == recurrenceId && m.ScheduledDate >= fromDate && m.ScheduledDate <= throughDate
                           select new MaterializationQueryItem(m.OccurrenceNumber, m.ScheduledDate, null, null, p.TotalAmount, null, p.Id)).ToListAsync(ct);
        return bank.Concat(cards).OrderBy(x => x.OccurrenceNumber).ToList();
    }
}

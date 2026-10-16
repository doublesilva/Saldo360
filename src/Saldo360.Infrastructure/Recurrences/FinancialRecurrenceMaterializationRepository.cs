using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Recurrences;
using Saldo360.Application.Planning;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Recurrences;

internal sealed class FinancialRecurrenceMaterializationRepository(Saldo360DbContext db) : IFinancialRecurrenceMaterializationRepository
{
    public Task<bool> HasAnyAsync(Guid familyId, Guid recurrenceId, CancellationToken ct) => db.FinancialRecurrenceMaterializations.AnyAsync(x => x.FamilyId == familyId && x.RecurrenceId == recurrenceId, ct);

    public async Task<MaterializationResultDto> MaterializeAsync(FinancialRecurrence recurrence, IReadOnlyList<RecurrenceOccurrenceDto> occurrences, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM financial_recurrences WHERE id = {recurrence.Id} AND family_id = {recurrence.FamilyId} FOR UPDATE", ct);
            var numbers = occurrences.Select(x => x.Sequence).ToArray();
            var existing = await db.FinancialRecurrenceMaterializations
                .Where(x => x.FamilyId == recurrence.FamilyId && x.RecurrenceId == recurrence.Id && numbers.Contains(x.OccurrenceNumber))
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
                    var existingTransaction = existingPlanned[materialization.PlannedTransactionId];
                    var status = existingTransaction.IsCancelled
                        ? PlannedTransactionStatus.Cancelled
                        : realizedPlannedIds.Contains(existingTransaction.Id) ? PlannedTransactionStatus.Realized : PlannedTransactionStatus.Pending;
                    items.Add(new(occurrence.Sequence, materialization.ScheduledDate, existingTransaction.Id, existingTransaction.DueDate, existingTransaction.Amount, status, "Existing"));
                    continue;
                }

                var details = PlannedTransactionDetails.TryCreate(recurrence.FinancialAccountId, recurrence.FinancialCategoryId, recurrence.Type.ToString(), recurrence.Description, recurrence.Amount, occurrence.Date, out var errors);
                if (details is null) throw new InvalidOperationException($"Não foi possível materializar a ocorrência {occurrence.Sequence}: {string.Join(" ", errors.SelectMany(x => x.Value))}");
                var planned = FinancialPlannedTransaction.Create(recurrence.FamilyId, details, userId, now);
                db.FinancialPlannedTransactions.Add(planned);
                db.FinancialRecurrenceMaterializations.Add(FinancialRecurrenceMaterialization.Create(recurrence.FamilyId, recurrence.Id, occurrence.Sequence, occurrence.Date, planned.Id, now));
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

    public async Task<IReadOnlyList<MaterializationQueryItem>> ListAsync(Guid familyId, Guid recurrenceId, DateOnly fromDate, DateOnly throughDate, CancellationToken ct) =>
        await (from materialization in db.FinancialRecurrenceMaterializations.AsNoTracking()
               join planned in db.FinancialPlannedTransactions.AsNoTracking() on materialization.PlannedTransactionId equals planned.Id
               join realization in db.FinancialTransactions.AsNoTracking().Where(x => !x.IsCancelled)
                   on planned.Id equals realization.PlannedTransactionId into realizations
               from realization in realizations.DefaultIfEmpty()
               where materialization.FamilyId == familyId && materialization.RecurrenceId == recurrenceId && materialization.ScheduledDate >= fromDate && materialization.ScheduledDate <= throughDate
               orderby materialization.OccurrenceNumber
               select new MaterializationQueryItem(materialization.OccurrenceNumber, materialization.ScheduledDate, planned.Id, planned.DueDate, planned.Amount,
                   planned.IsCancelled ? PlannedTransactionStatus.Cancelled : realization != null ? PlannedTransactionStatus.Realized : PlannedTransactionStatus.Pending)).ToListAsync(ct);
}

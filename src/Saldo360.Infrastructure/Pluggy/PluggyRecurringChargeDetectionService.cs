using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Recurrences;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Pluggy;

internal static class PluggyRecurringChargeDetectionService
{
    public static async Task ApplyAsync(Saldo360DbContext db, Guid workspaceId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await (from posting in db.PluggyPostings.AsNoTracking()
                          join transaction in db.PluggyImportedTransactions.AsNoTracking() on posting.ImportedTransactionId equals transaction.Id
                          join link in db.PluggyAccountLinks.AsNoTracking() on posting.AccountLinkId equals link.Id
                          join card in db.FinancialCreditCards.AsNoTracking() on link.CreditCardId equals card.Id
                          join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on posting.CreditCardPurchaseId equals purchase.Id
                          where posting.WorkspaceId == workspaceId && posting.State == PluggyPostingState.Applied && posting.Kind == PluggyPostingKind.CardPurchase
                              && transaction.Status == PluggyTransactionStatus.Posted && transaction.MissingSyncCount == 0
                              && (transaction.TotalInstallments == null || transaction.TotalInstallments <= 1) && link.CreditCardId != null
                              && purchase.Status == Saldo360.Domain.CreditCards.CreditCardPurchaseStatus.Active
                          select new { CardId = link.CreditCardId!.Value, purchase.CategoryId, purchase.Nature, purchase.Description,
                              transaction.MerchantName, transaction.MerchantCategory, posting.AppliedDate, posting.AppliedAmount,
                              SourceId = transaction.Id.ToString(), card.ClosingDay, card.DueDay }).ToListAsync(ct);
        var facts = rows.Where(x => x.AppliedDate is not null && x.AppliedAmount is > 0m).Select(x => new PluggyRecurringChargeObservation(
            x.CardId, x.CategoryId, x.Description, x.MerchantName, x.MerchantCategory, x.AppliedDate!.Value, x.AppliedAmount!.Value,
            x.ClosingDay, x.DueDay, x.SourceId)).ToList();
        var detections = PluggyRecurringChargeDetector.Detect(facts).ToList();
        if (detections.Count == 0) return;
        var recurrences = await db.FinancialRecurrences.Where(x => x.WorkspaceId == workspaceId).ToListAsync(ct);
        foreach (var detection in detections)
        {
            var existing = recurrences.SingleOrDefault(x => x.ExternalKey == detection.ExternalKey);
            if (existing is not null)
            {
                existing.ObservePluggy(detection.ProjectedAmount, detection.IsEstimated, detection.EvidenceCount, detection.LastDate, now);
                continue;
            }
            // Uma recorrência mensal já cadastrada pelo usuário prevalece; a sincronização não cria uma segunda definição.
            if (recurrences.Any(x => x.FinancialCreditCardId == detection.CardId && x.Frequency == RecurrenceFrequency.Monthly
                && PluggyFinancialMapper.Normalize(x.Description) == PluggyFinancialMapper.Normalize(detection.Description))) continue;
            var nature = rows.Last(x => x.CardId == detection.CardId && x.CategoryId == detection.CategoryId
                && x.AppliedDate == detection.LastDate).Nature;
            var details = new RecurrenceDetails(null, detection.CategoryId, CategoryType.Expense, detection.Description,
                detection.ProjectedAmount, detection.IsEstimated, RecurrenceFrequency.Monthly, 1, detection.FirstDate, null, null,
                detection.LastDate.Day, detection.CardId, nature);
            var recurrence = FinancialRecurrence.CreatePluggyDetected(workspaceId, details, detection.ExternalKey, detection.EvidenceCount, now);
            db.FinancialRecurrences.Add(recurrence); recurrences.Add(recurrence);
        }
        await db.SaveChangesAsync(ct);
    }
}

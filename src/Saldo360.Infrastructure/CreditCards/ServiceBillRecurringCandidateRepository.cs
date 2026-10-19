using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class ServiceBillRecurringCandidateRepository(Saldo360DbContext db) : IServiceBillRecurringCandidateRepository
{
    public async Task<IReadOnlyList<ServiceBillRecurringCandidateData>> ListAsync(Guid familyId, string competence, string documentType,
        string? accountReferenceHash, string? serviceReferenceHash, CancellationToken cancellationToken)
    {
        var start = DateOnly.ParseExact(competence + "-01", "yyyy-MM-dd"); var through = start.AddMonths(1).AddDays(-1);
        return await (from materialization in db.FinancialRecurrenceMaterializations.AsNoTracking()
            join planned in db.FinancialPlannedTransactions.AsNoTracking() on new { materialization.FamilyId, Id = materialization.PlannedTransactionId } equals new { planned.FamilyId, planned.Id }
            join recurrence in db.FinancialRecurrences.AsNoTracking() on new { materialization.FamilyId, Id = materialization.RecurrenceId } equals new { recurrence.FamilyId, recurrence.Id }
            join category in db.FinancialCategories.AsNoTracking() on new { planned.FamilyId, Id = planned.CategoryId } equals new { category.FamilyId, category.Id }
            where materialization.FamilyId == familyId && planned.Type == CategoryType.Expense && !planned.IsCancelled
                && planned.DueDate >= start && planned.DueDate <= through
                && !db.FinancialTransactions.Any(transaction => transaction.FamilyId == familyId && transaction.PlannedTransactionId == planned.Id && !transaction.IsCancelled)
            select new ServiceBillRecurringCandidateData(planned.Id, recurrence.Id, planned.Description, category.Name, planned.DueDate, planned.Amount,
                accountReferenceHash != null && db.ServiceAccountBindings.Any(binding => binding.FamilyId == familyId
                    && binding.RecurringTemplateId == recurrence.Id && binding.DocumentType == documentType
                    && binding.AccountReferenceHash == accountReferenceHash),
                serviceReferenceHash != null && db.ServiceAccountBindings.Any(binding => binding.FamilyId == familyId
                    && binding.RecurringTemplateId == recurrence.Id && binding.DocumentType == documentType
                    && binding.ServiceReferenceHash == serviceReferenceHash))).ToListAsync(cancellationToken);
    }
}

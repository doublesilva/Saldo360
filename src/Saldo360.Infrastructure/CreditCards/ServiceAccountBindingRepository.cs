using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class ServiceAccountBindingRepository(Saldo360DbContext db) : IServiceAccountBindingRepository
{
    public async Task<ServiceAccountBindingStoreResult> GetAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken cancellationToken)
    {
        var recurrenceId = await db.FinancialDocumentInboxItems.Where(x => x.WorkspaceId == workspaceId && x.Id == documentId
            && db.FinancialWorkspaces.Any(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal))
            .Select(x => x.ServiceBillRecurrenceId).SingleOrDefaultAsync(cancellationToken);
        if (recurrenceId is null) return new(ServiceAccountBindingStoreStatus.Invalid);
        var binding = await db.ServiceAccountBindings.AsNoTracking().OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RecurringTemplateId == recurrenceId, cancellationToken);
        return binding is null ? new(ServiceAccountBindingStoreStatus.Invalid) : new(ServiceAccountBindingStoreStatus.AlreadyExists,
            binding.Id, binding.AccountReferenceMasked, binding.ServiceReferenceMasked);
    }
    public async Task<ServiceAccountBindingStoreResult> CreateAsync(Guid workspaceId, Guid documentId, Guid userId, string documentType,
        string issuerNormalized, string? accountHash, string? accountMasked, string? serviceHash, string? serviceMasked,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var item = await db.FinancialDocumentInboxItems.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == documentId
            && db.FinancialWorkspaces.Any(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal), cancellationToken);
        if (item?.ServiceBillRecurrenceId is not { } recurrenceId || item.ServiceBillOccurrenceId is null)
            return new(ServiceAccountBindingStoreStatus.Invalid);
        var matches = await db.ServiceAccountBindings.Where(x => x.WorkspaceId == workspaceId && x.DocumentType == documentType
            && ((accountHash != null && x.AccountReferenceHash == accountHash) || (serviceHash != null && x.ServiceReferenceHash == serviceHash)))
            .ToListAsync(cancellationToken);
        if (matches.Any(x => x.RecurringTemplateId != recurrenceId)) return new(ServiceAccountBindingStoreStatus.Conflict);
        var exact = matches.FirstOrDefault(x => x.RecurringTemplateId == recurrenceId
            && x.AccountReferenceHash == accountHash && x.ServiceReferenceHash == serviceHash);
        if (exact is not null) return new(ServiceAccountBindingStoreStatus.AlreadyExists, exact.Id,
            exact.AccountReferenceMasked, exact.ServiceReferenceMasked);
        var binding = ServiceAccountBinding.Create(workspaceId, recurrenceId, documentType, issuerNormalized,
            accountHash, accountMasked, serviceHash, serviceMasked, now);
        db.ServiceAccountBindings.Add(binding);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return new(ServiceAccountBindingStoreStatus.Conflict); }
        return new(ServiceAccountBindingStoreStatus.Created, binding.Id, accountMasked, serviceMasked);
    }
}

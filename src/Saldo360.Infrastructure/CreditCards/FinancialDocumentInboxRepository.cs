using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;
using Npgsql;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialDocumentInboxRepository(Saldo360DbContext db) : IFinancialDocumentInboxRepository
{
    public Task<bool> CanAccessFamilyAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        db.FamilyMembers.AsNoTracking().AnyAsync(member => member.FamilyId == familyId && member.UserId == userId, cancellationToken);
    public Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid familyId, string hash, CancellationToken cancellationToken) =>
        db.FinancialDocumentInboxItems.SingleOrDefaultAsync(item => item.FamilyId == familyId && item.ContentHash == hash, cancellationToken);
    public Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid familyId, FinancialDocumentSource source, string externalId, CancellationToken cancellationToken) =>
        db.FinancialDocumentInboxItems.SingleOrDefaultAsync(item => item.FamilyId == familyId && item.Source == source && item.ExternalSourceId == externalId, cancellationToken);
    public Task<FinancialDocumentInboxItem?> GetAsync(Guid familyId, Guid id, Guid userId, CancellationToken cancellationToken) =>
        db.FinancialDocumentInboxItems.SingleOrDefaultAsync(item => item.FamilyId == familyId && item.Id == id
            && db.FamilyMembers.Any(member => member.FamilyId == familyId && member.UserId == userId), cancellationToken);
    public async Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        await db.FinancialDocumentInboxItems.AsNoTracking().Where(item => item.FamilyId == familyId
            && db.FamilyMembers.Any(member => member.FamilyId == familyId && member.UserId == userId))
            .OrderByDescending(item => item.ReceivedAt).Take(200).ToListAsync(cancellationToken);
    public async Task<bool> TryAddAsync(FinancialDocumentInboxItem item, CancellationToken cancellationToken)
    {
        await db.FinancialDocumentInboxItems.AddAsync(item, cancellationToken);
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(item).State = EntityState.Detached;
            return false;
        }
    }
    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

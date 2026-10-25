using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;
using Npgsql;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialDocumentInboxRepository(Saldo360DbContext db) : IFinancialDocumentInboxRepository
{
    public Task<bool> CanAccessWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        db.FinancialWorkspaces.AsNoTracking().AnyAsync(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal, cancellationToken);
    public Task<bool> WorkspaceExistsAsync(Guid workspaceId, CancellationToken cancellationToken) => db.FinancialWorkspaces.AsNoTracking().AnyAsync(workspace => workspace.Id == workspaceId && workspace.IsActive && workspace.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal, cancellationToken);
    public Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid workspaceId, string hash, CancellationToken cancellationToken) =>
        db.FinancialDocumentInboxItems.SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.ContentHash == hash, cancellationToken);
    public Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid workspaceId, FinancialDocumentSource source, string externalId, CancellationToken cancellationToken) =>
        db.FinancialDocumentInboxItems.SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.Source == source && item.ExternalSourceId == externalId, cancellationToken);
    public Task<FinancialDocumentInboxItem?> GetAsync(Guid workspaceId, Guid id, Guid userId, CancellationToken cancellationToken) =>
        db.FinancialDocumentInboxItems.SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.Id == id
            && db.FinancialWorkspaces.Any(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal), cancellationToken);
    public async Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        await db.FinancialDocumentInboxItems.AsNoTracking().Where(item => item.WorkspaceId == workspaceId
            && db.FinancialWorkspaces.Any(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal))
            .OrderByDescending(item => item.ReceivedAt).Take(200).ToListAsync(cancellationToken);
    public async Task<FinancialDocumentInboxItem?> GetForAnalysisAsync(Guid workspaceId, Guid id, Guid userId, CancellationToken ct)
    {
        var item = await GetAsync(workspaceId, id, userId, ct);
        if (item is not null) await db.Entry(item).ReloadAsync(ct);
        return item;
    }
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

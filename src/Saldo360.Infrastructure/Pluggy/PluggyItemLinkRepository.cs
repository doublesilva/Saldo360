using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Pluggy;

internal sealed class PluggyItemLinkRepository(Saldo360DbContext db) : IPluggyItemLinkRepository
{
    public async Task<IReadOnlyList<PluggyItemLink>> ListAsync(Guid workspaceId, CancellationToken ct) =>
        await db.PluggyItemLinks.AsNoTracking().Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);

    public Task<PluggyItemLink?> FindAsync(Guid workspaceId, Guid id, CancellationToken ct) =>
        db.PluggyItemLinks.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public async Task<bool> TryAddAsync(PluggyItemLink link, CancellationToken ct)
    {
        db.PluggyItemLinks.Add(link);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(link).State = EntityState.Detached;
            return false;
        }
    }
}

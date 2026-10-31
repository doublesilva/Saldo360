using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Dashboard;
using Saldo360.Domain.Accounts;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Dashboard;

internal sealed class OverdraftRecoveryRepository(Saldo360DbContext db) : IOverdraftRecoveryRepository
{
    public Task<OverdraftRecoverySnapshot?> GetAsync(Guid workspaceId, Guid accountId, DateOnly competence, CancellationToken ct) =>
        db.OverdraftRecoverySnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.AccountId == accountId
            && x.Competence == new DateOnly(competence.Year, competence.Month, 1), ct);

    public async Task<OverdraftRecoverySnapshot> AddIfAbsentAsync(OverdraftRecoverySnapshot snapshot, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id = {snapshot.WorkspaceId} FOR UPDATE", ct);
        var existing = await db.OverdraftRecoverySnapshots.SingleOrDefaultAsync(x => x.WorkspaceId == snapshot.WorkspaceId && x.AccountId == snapshot.AccountId && x.Competence == snapshot.Competence, ct);
        if (existing is null) { db.Add(snapshot); await db.SaveChangesAsync(ct); existing = snapshot; }
        await transaction.CommitAsync(ct);
        return existing;
    }
}

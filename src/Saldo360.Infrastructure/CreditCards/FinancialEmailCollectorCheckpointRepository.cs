using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialEmailCollectorCheckpointRepository(Saldo360DbContext db) : IFinancialEmailCollectorCheckpointRepository
{
    public async Task<FinancialEmailCollectorCheckpoint> GetOrCreateAsync(Guid familyId, string profileName, CancellationToken cancellationToken)
    {
        var checkpoint = await db.FinancialEmailCollectorCheckpoints.SingleOrDefaultAsync(item => item.FamilyId == familyId && item.ProfileName == profileName, cancellationToken);
        if (checkpoint is not null) return checkpoint;
        checkpoint = FinancialEmailCollectorCheckpoint.Create(familyId, profileName);
        db.FinancialEmailCollectorCheckpoints.Add(checkpoint);
        return checkpoint;
    }
    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
    public async Task<IReadOnlyList<FinancialEmailCollectorCheckpoint>> ListAsync(IReadOnlyCollection<Guid> familyIds, CancellationToken cancellationToken) =>
        await db.FinancialEmailCollectorCheckpoints.AsNoTracking().Where(item => familyIds.Contains(item.FamilyId)).OrderBy(item => item.ProfileName).ToListAsync(cancellationToken);
}

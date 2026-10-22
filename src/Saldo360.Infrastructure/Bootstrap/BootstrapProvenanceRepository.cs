using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Bootstrap;
using Saldo360.Domain.Bootstrap;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Bootstrap;

internal sealed class BootstrapProvenanceRepository(Saldo360DbContext db) : IBootstrapProvenanceRepository
{
    public Task<FinancialBootstrapProvenance?> FindAsync(Guid workspaceId, BootstrapSource source, CancellationToken ct) =>
        db.FinancialBootstrapProvenance.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId
            && x.SourceSystem == source.SourceSystem && x.SourceDocument == source.SourceDocument && x.ExternalReference == source.ExternalReference, ct);
    public void Stage(FinancialBootstrapProvenance provenance) => db.FinancialBootstrapProvenance.Add(provenance);
}

using Saldo360.Domain.Bootstrap;

namespace Saldo360.Application.Bootstrap;

/// <summary>Stage in the same unit of work/transaction as the domain service write. Never commit a source marker alone.</summary>
public interface IBootstrapProvenanceRepository
{
    Task<FinancialBootstrapProvenance?> FindAsync(Guid workspaceId, BootstrapSource source, CancellationToken ct);
    void Stage(FinancialBootstrapProvenance provenance);
}

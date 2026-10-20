using Saldo360.Domain.Workspaces;

namespace Saldo360.Application.Workspaces;

public interface IFinancialWorkspaceRepository
{
    Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid workspaceId, Guid userId, CancellationToken ct);
    Task<FinancialWorkspace> EnsurePersonalAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
}

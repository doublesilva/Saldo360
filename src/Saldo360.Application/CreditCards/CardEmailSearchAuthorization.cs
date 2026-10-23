using Saldo360.Application.Workspaces;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Application.CreditCards;

/// <summary>
/// The application's shared OAuth mailbox is explicitly authorized for one Personal only.
/// This restriction applies to on-demand card search, independently of scheduler configuration.
/// </summary>
public sealed class CardEmailSearchAuthorization(IFinancialWorkspaceRepository workspaces)
{
    public static readonly Guid AuthorizedPersonalId = Guid.Parse("01a163b9-4b06-74f8-ac02-05bdc7cfb14e");

    public async Task<bool> CanSearchAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        if (workspaceId != AuthorizedPersonalId || userId == Guid.Empty) return false;
        // Repository validates persisted ownership, active Personal and ApplicationUser.Id.
        return await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) == FinancialWorkspaceRole.Owner;
    }
}

using Saldo360.Application.CreditCards;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Tests.Application;

public sealed class CardEmailSearchAuthorizationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequiresPersistedOwnerOfApprovedPersonal(bool owner)
    {
        var repository = new WorkspaceRepository(owner);
        var authorization = new CardEmailSearchAuthorization(repository);
        Assert.Equal(owner, await authorization.CanSearchAsync(CardEmailSearchAuthorization.AuthorizedPersonalId, Guid.NewGuid(), default));
        Assert.Equal(1, repository.Reads);
    }

    [Fact]
    public async Task OtherWorkspacesAndAnonymousUsersFailBeforeAnyMailboxAccess()
    {
        var repository = new WorkspaceRepository(true);
        var authorization = new CardEmailSearchAuthorization(repository);
        Assert.False(await authorization.CanSearchAsync(Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.False(await authorization.CanSearchAsync(CardEmailSearchAuthorization.AuthorizedPersonalId, Guid.Empty, default));
        Assert.Equal(0, repository.Reads);
    }

    private sealed class WorkspaceRepository(bool owner) : IFinancialWorkspaceRepository
    {
        public int Reads { get; private set; }
        public Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid workspaceId, Guid userId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<FinancialWorkspaceRole?>(owner ? FinancialWorkspaceRole.Owner : null);
        }
        public Task<FinancialWorkspace> EnsurePersonalAsync(Guid userId, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException("Authorization must not provision a workspace.");
    }
}

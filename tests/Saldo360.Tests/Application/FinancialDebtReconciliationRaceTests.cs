using Saldo360.Application.Debts;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Tests.Application;

[Trait("Category", "REGRESSION")]
public sealed class FinancialDebtReconciliationRaceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinkCommittedBetweenLookupAndEligibility_IsIdempotentOnlyForExactPair(bool samePair)
    {
        var link = new DebtInstallmentReconciliationDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        var repo = new RacingRepository(link, samePair);
        var service = new FinancialDebtInstallmentReconciliationService(new Owner(), repo, TimeProvider.System);

        var result = await service.CreateAsync(link.WorkspaceId, Guid.NewGuid(), new(link.DebtInstallmentId, link.PlannedTransactionId), default);

        Assert.Equal(samePair ? DebtReconciliationAccess.Allowed : DebtReconciliationAccess.Conflict, result.Access);
        Assert.Equal(samePair ? link : null, result.Reconciliation);
        Assert.Equal(2, repo.Lookups);
        Assert.False(repo.CreateCalled);
    }

    private sealed class Owner : IFinancialWorkspaceRepository
    {
        public Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid workspaceId, Guid userId, CancellationToken ct) => Task.FromResult<FinancialWorkspaceRole?>(FinancialWorkspaceRole.Owner);
        public Task<FinancialWorkspace> EnsurePersonalAsync(Guid userId, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RacingRepository(DebtInstallmentReconciliationDto link, bool samePair) : IFinancialDebtInstallmentReconciliationRepository
    {
        public int Lookups { get; private set; }
        public bool CreateCalled { get; private set; }
        private bool committed;

        public Task<DebtInstallmentReconciliationDto?> FindActiveAsync(Guid workspaceId, Guid installmentId, Guid plannedId, CancellationToken ct)
        {
            Assert.Equal(link.WorkspaceId, workspaceId);
            Assert.Equal(link.DebtInstallmentId, installmentId);
            Assert.Equal(link.PlannedTransactionId, plannedId);
            Lookups++;
            return Task.FromResult(committed && samePair ? link : null);
        }

        public Task<(bool InstallmentExists, bool PlannedExists, bool PlannedEligible, bool InstallmentEligible, bool AccountCompatible, bool AlreadyLinked)> EligibilityAsync(Guid workspaceId, Guid installmentId, Guid plannedId, CancellationToken ct)
        {
            Assert.Equal(1, Lookups);
            committed = true; // Deterministic interleaving, no timing/sleep dependency.
            return Task.FromResult((true, true, true, true, true, true));
        }

        public Task<DebtInstallmentReconciliationDto?> CreateAsync(Guid workspaceId, Guid installmentId, Guid plannedId, Guid userId, DateTimeOffset now, CancellationToken ct)
        {
            CreateCalled = true;
            throw new InvalidOperationException("An existing link must not be created again.");
        }
        public Task<IReadOnlyList<DebtInstallmentReconciliationDto>> ListAsync(Guid workspaceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(Guid workspaceId, Guid id, Guid userId, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }
}

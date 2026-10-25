using Saldo360.Application.Workspaces;
using Saldo360.Domain.Workspaces;
using Saldo360.Application.Calendar;
using Saldo360.Application.Families;
using Saldo360.Domain.Families;

namespace Saldo360.Tests.Application;

[Trait("Category", "REGRESSION")]
public sealed class FinancialSixMonthPlanningTests
{
    [Fact]
    public async Task UsesOneSaoPauloDateAndOneSnapshot_WithChainedBalancesAndNoCardDoubleCounting()
    {
        var clock = new Clock(); var repository = new Repository();
        var service = new FinancialCalendarMonthlyProjectionService(new Member(), repository, clock);
        var result = await service.GetAsync(Guid.NewGuid(), Guid.NewGuid(), new(new(2026, 12, 1), 6, null), default);
        var projection = Assert.IsType<FinancialCalendarMonthlyProjection>(result.Projection);
        Assert.Equal(1, clock.Reads); Assert.Equal(1, repository.Reads);
        Assert.Equal(new DateOnly(2026, 10, 19), repository.Today);
        Assert.Equal(6, projection.Months.Count); Assert.Equal(new DateOnly(2027, 5, 1), projection.Through);
        Assert.Equal(990m, projection.Months[0].ClosingBalance);
        Assert.Equal(990m, projection.Months[1].OpeningBalance);
        Assert.Equal(1090m, projection.Months[1].ClosingBalance);
        Assert.Equal(150m, projection.TotalIncome); Assert.Equal(60m, projection.TotalExpense);
        Assert.Equal(10m, projection.Months[0].CreditCardOutstanding);
        Assert.Null(projection.Months[0].FixedExpenses);
        Assert.Equal("NotDefined", projection.Months[0].BudgetStatus);
    }

    private sealed class Clock : TimeProvider
    {
        public int Reads;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 20, 2, 59, 59, TimeSpan.Zero).AddDays(Reads++);
    }
    private sealed class Repository : IFinancialCalendarBalanceRepository
    {
        public int Reads; public DateOnly Today;
        public Task<FinancialCalendarBalanceSnapshot?> ReadMonthlyAsync(Guid family, Guid? account, DateOnly from, DateOnly through, DateOnly today, CancellationToken ct)
        {
            Reads++; Today = today;
            return Task.FromResult<FinancialCalendarBalanceSnapshot?>(new([new(Guid.NewGuid(), 1000m, new(2026, 10, 1))], [],
                [new(new(2026, 12, 10), 50m, 30m, 0m, 0m), new(new(2027, 1, 10), 100m, 0m, 0m, 0m)],
                CreditCardObligations: [new(new(2026, 12, 10), 0m, 10m, 0m, 0m)],
                DebtObligations: [new(new(2026, 12, 10), 0m, 20m, 0m, 0m)]));
        }
        public Task<FinancialCalendarBalanceSnapshot?> ReadAsync(Guid family, Guid? account, DateOnly through, DateOnly today, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Member : IFinancialWorkspaceRepository
    {
        public Task<FinancialWorkspace> EnsurePersonalAsync(Guid user, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid family, Guid user, CancellationToken ct) => Task.FromResult<FinancialWorkspaceRole?>(FinancialWorkspaceRole.Owner);
        public Task AddAsync(Family family, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FamilySummary>> ListForUserAsync(Guid user, CancellationToken ct) => throw new NotSupportedException();
        public Task<FamilyDetails?> FindForMemberAsync(Guid family, Guid user, CancellationToken ct) => throw new NotSupportedException();
    }
}

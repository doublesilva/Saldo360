using Saldo360.Application.Workspaces;
using Saldo360.Domain.Workspaces;
using Saldo360.Application.Accounts;
using Saldo360.Application.Calendar;
using Saldo360.Application.Families;
using Saldo360.Domain.Families;

namespace Saldo360.Tests.Application;

public sealed class FinancialCalendarDailyBalanceTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static FinancialCalendarDailyTotals Row(DateOnly date, decimal income = 0m, decimal expense = 0m) => new(date, income, expense, 0m, 0m);

    [Fact]
    public void HistoricalPendingIsFlaggedWithoutChangingActualClosing()
    {
        var yesterday = Today.AddDays(-1);
        var snapshot = new FinancialCalendarBalanceSnapshot([new(Guid.NewGuid(), 1000.01m, Today.AddDays(-10))], [], [Row(yesterday, expense: 9999.99m)]);
        var result = FinancialCalendarBalanceService.CalculateBalances(new(yesterday, yesterday, null), Today, snapshot);
        var day = Assert.Single(result.Days);
        Assert.Equal(1000.01m, day.ClosingBalance); Assert.Equal(0m, day.Expense);
        Assert.Equal(FinancialCalendarBalanceKind.Realized, day.Kind); Assert.True(day.HasOverduePending);
        Assert.Null(result.Risk.EndingProjectedBalance);
    }

    [Fact]
    public void MissingOpeningAndMultipleObligationsOnSameDateReturnUnavailableInsteadOfThrowing()
    {
        var yesterday = Today.AddDays(-1);
        var snapshot = new FinancialCalendarBalanceSnapshot([new(Guid.NewGuid(), null, null)], [],
            [Row(yesterday, expense: 10.01m), Row(Today, expense: 2m)],
            CreditCardObligations: [Row(yesterday, expense: 30m), Row(yesterday, expense: 20m)],
            DebtObligations: [Row(yesterday, expense: 40m)]);
        var result = FinancialCalendarBalanceService.CalculateBalances(new(yesterday, Today.AddDays(1), null), Today, snapshot);
        Assert.Equal(BalanceUnavailableReason.OpeningBalanceNotConfigured, result.UnavailableReason);
        Assert.Null(result.OpeningBalance); Assert.Null(result.Risk.MinimumProjectedBalance);
        Assert.All(result.Days, day => { Assert.Null(day.ClosingBalance); Assert.Null(day.Income); Assert.Null(day.Expense); Assert.True(day.HasOverduePending); });
    }

    [Fact]
    public void HistoricalIsActualAndOverdueAffectsProjectionOnceEvenWhenPeriodStartsInFuture()
    {
        var snapshot = new FinancialCalendarBalanceSnapshot([new(Guid.NewGuid(), 1000m, Today.AddDays(-10))],
            [Row(Today.AddDays(-2), 100.10m), Row(Today.AddDays(-1), expense: 20.05m), Row(Today, 50.20m, 60.10m)],
            [Row(Today.AddDays(-3), expense: 30.05m), Row(Today, expense: 40.05m), Row(Today.AddDays(2), 70.25m)]);
        var result = FinancialCalendarBalanceService.CalculateBalances(new(Today.AddDays(-2), Today.AddDays(2), null), Today, snapshot);
        Assert.Equal(1100.10m, result.Days[0].ClosingBalance); Assert.Equal(FinancialCalendarBalanceKind.Realized, result.Days[0].Kind);
        Assert.Equal(1080.05m, result.Days[1].ClosingBalance);
        Assert.Equal(100.15m, result.Days[2].Expense); // Overdue remains separate, not a realized transaction of today.
        Assert.Equal(1000.05m, result.Days[2].ClosingBalance); Assert.True(result.Days[2].HasOverduePending);
        Assert.Equal(1000.05m, result.Days[3].ClosingBalance); Assert.Equal(1070.30m, result.Days[4].ClosingBalance);
        var futureOnly = FinancialCalendarBalanceService.CalculateBalances(new(Today.AddDays(1), Today.AddDays(2), null), Today, snapshot);
        Assert.Equal(1000.05m, futureOnly.OpeningBalance); Assert.Equal(result.Risk.EndingProjectedBalance, futureOnly.Risk.EndingProjectedBalance);
        Assert.Equal(FinancialBalanceCalculator.Project(1070.15m, new(0m, 30.05m, 70.25m, 40.05m)), result.Risk.EndingProjectedBalance);
    }

    [Fact]
    public async Task ReadsTodayOnceInSaoPauloAndLoadsOnlyOneSnapshot()
    {
        // UTC 20/10 before 03:00 is still 19/10 in São Paulo. A second read would cross a day.
        var clock = new CountingClock(); var repository = new SnapshotRepository();
        var service = new FinancialCalendarBalanceService(new MemberFamily(), repository, clock);
        var result = await service.GetAsync(Guid.NewGuid(), Guid.NewGuid(), new(new(2026, 10, 19), new(2026, 10, 21), null), default);
        Assert.NotNull(result.Balances); Assert.Equal(1, clock.Reads); Assert.Equal(1, repository.Reads);
        Assert.Equal(new DateOnly(2026, 10, 19), repository.Today);
        Assert.All(result.Balances.Days, day => Assert.Equal(FinancialCalendarBalanceKind.Projected, day.Kind));
    }

    private sealed class CountingClock : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 20, 2, 59, 59, TimeSpan.Zero).AddDays(Reads++);
    }
    private sealed class SnapshotRepository : IFinancialCalendarBalanceRepository
    {
        public int Reads { get; private set; } public DateOnly Today { get; private set; }
        public Task<FinancialCalendarBalanceSnapshot?> ReadAsync(Guid workspaceId, Guid? accountId, DateOnly through, DateOnly today, CancellationToken ct)
        { Reads++; Today = today; return Task.FromResult<FinancialCalendarBalanceSnapshot?>(new([new(Guid.NewGuid(), 0m, today.AddDays(-1))], [], [])); }
        public Task<FinancialCalendarBalanceSnapshot?> ReadMonthlyAsync(Guid workspaceId, Guid? accountId, DateOnly from, DateOnly through, DateOnly today, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class MemberFamily : IFinancialWorkspaceRepository
    {
        public Task<FinancialWorkspace> EnsurePersonalAsync(Guid user, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid workspaceId, Guid userId, CancellationToken ct) => Task.FromResult<FinancialWorkspaceRole?>(FinancialWorkspaceRole.Owner);
        public Task AddAsync(Family family, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FamilySummary>> ListForUserAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<FamilyDetails?> FindForMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct) => throw new NotSupportedException();
    }
}

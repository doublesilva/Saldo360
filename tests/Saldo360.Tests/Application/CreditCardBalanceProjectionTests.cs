using Saldo360.Application.Calendar;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
public sealed class CreditCardBalanceProjectionTests
{
    [Fact]
    public void Outstanding_card_obligation_reduces_projected_balance_once()
    {
        var account = new FinancialCalendarBalanceAccount(Guid.NewGuid(), 1000m, new DateOnly(2026, 1, 1));
        var snapshot = new FinancialCalendarBalanceSnapshot(
            [account], [], [], null,
            [new FinancialCalendarDailyTotals(new DateOnly(2026, 10, 20), 0m, 250m, 0m, 0m)], false);
        var result = FinancialCalendarBalanceService.CalculateBalances(
            new FinancialCalendarBalanceQuery(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 31), account.Id),
            new DateOnly(2026, 10, 15), snapshot);
        Assert.Equal(750m, result.Risk.EndingProjectedBalance);
        Assert.Equal(250m, result.CreditCardOutstanding);
        Assert.True(result.CreditCardProjectionComplete);
    }

    [Fact]
    public void Unallocated_obligation_marks_projection_incomplete_without_arbitrary_account_assignment()
    {
        var account = new FinancialCalendarBalanceAccount(Guid.NewGuid(), 100m, new DateOnly(2026, 1, 1));
        var snapshot = new FinancialCalendarBalanceSnapshot(
            [account], [], [], null,
            [new FinancialCalendarDailyTotals(new DateOnly(2026, 10, 20), 0m, 50m, 0m, 0m)], true);
        var result = FinancialCalendarBalanceService.CalculateBalances(
            new FinancialCalendarBalanceQuery(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 31), account.Id),
            new DateOnly(2026, 10, 15), snapshot);
        Assert.False(result.CreditCardProjectionComplete);
    }
}

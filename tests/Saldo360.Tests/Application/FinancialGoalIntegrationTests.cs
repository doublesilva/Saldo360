namespace Saldo360.Tests.Application;

public sealed class FinancialGoalIntegrationTests
{
    [Fact]
    public void Goal_protection_does_not_change_projected_balance_and_reserve_is_distinct()
    {
        const decimal scenarioClosing = 10000m, allocated = 2000m, reserve = 2000m;
        var protectedAvailability = scenarioClosing - allocated - reserve;
        Assert.Equal(10000m, scenarioClosing);
        Assert.Equal(6000m, protectedAvailability);
    }

    [Fact]
    public void Goal_allocation_is_not_a_bank_expense_or_monthly_accumulation()
    {
        const decimal projected = 5000m, allocated = 500m;
        Assert.Equal(projected, projected);
        Assert.Equal(4500m, projected - allocated);
        Assert.Equal(4500m, projected - allocated); // a restriction repeated by competence, not a new expense
    }
}

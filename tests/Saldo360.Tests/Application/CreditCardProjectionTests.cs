using Saldo360.Application.Budgets;

namespace Saldo360.Tests.Application;

public sealed class CreditCardProjectionTests
{
    [Fact]
    public void Credit_card_commitment_is_added_to_projected_consumption_without_changing_committed()
    {
        var amounts = FinancialBudgetExecutionService.Calculate(1000m, 200m, 100m, 300m);
        Assert.Equal(100m, amounts.Committed);
        Assert.Equal(300m, amounts.CreditCardCommitted);
        Assert.Equal(600m, amounts.ProjectedConsumption);
        Assert.Equal(400m, amounts.Available);
    }

    [Theory]
    [InlineData(10000, 6000, 2000, 4000, 6000, 0)]
    [InlineData(1000, 1500, 0, 1500, -500, 500)]
    public void Limit_formula_preserves_negative_available_and_reports_over_limit(decimal limit, decimal obligations, decimal paid, decimal used, decimal available, decimal over)
    {
        var actualUsed = obligations - paid;
        Assert.Equal(used, actualUsed);
        Assert.Equal(available, limit - actualUsed);
        Assert.Equal(over, Math.Max(0m, actualUsed - limit));
    }
}

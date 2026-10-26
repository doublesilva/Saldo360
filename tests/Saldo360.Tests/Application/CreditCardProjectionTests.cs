using Saldo360.Application.Budgets;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
[Trait("Area", "Planning")]
[Trait("Area", "Budgets")]
[Trait("Profile", "FAST")]
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

}

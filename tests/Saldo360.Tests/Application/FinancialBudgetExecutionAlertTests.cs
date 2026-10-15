using Saldo360.Application.Budgets;

namespace Saldo360.Tests.Application;

public sealed class FinancialBudgetExecutionAlertTests
{
    [Theory]
    [InlineData(1000d, 799.99d, FinancialBudgetAlertStatus.WithinBudget)]
    [InlineData(1000d, 800d, FinancialBudgetAlertStatus.ApproachingLimit)]
    [InlineData(1000d, 999.999d, FinancialBudgetAlertStatus.ApproachingLimit)]
    [InlineData(1000d, 1000d, FinancialBudgetAlertStatus.LimitReached)]
    [InlineData(1000d, 1000.01d, FinancialBudgetAlertStatus.Exceeded)]
    [InlineData(0d, 0d, FinancialBudgetAlertStatus.WithinBudget)]
    [InlineData(0d, 0.01d, FinancialBudgetAlertStatus.Exceeded)]
    public void ClassifyUsesRawProjectedConsumption(
        decimal planned, decimal projected, FinancialBudgetAlertStatus expected) =>
        Assert.Equal(expected, FinancialBudgetExecutionService.Classify(planned, projected));

    [Fact]
    public void ClassificationDoesNotUseRoundedPercentage()
    {
        // 79.999% permanece dentro, mesmo que a apresentação arredonde para 80.00%.
        Assert.Equal(FinancialBudgetAlertStatus.WithinBudget,
            FinancialBudgetExecutionService.Classify(1000m, 799.99m));
    }

    [Fact]
    public void MessagesAreStableAndNonIntrusive()
    {
        Assert.Equal("O consumo projetado está próximo do limite.",
            FinancialBudgetExecutionService.Message(FinancialBudgetAlertStatus.ApproachingLimit));
        Assert.Equal("O consumo projetado ultrapassou o orçamento.",
            FinancialBudgetExecutionService.Message(FinancialBudgetAlertStatus.Exceeded));
    }
}

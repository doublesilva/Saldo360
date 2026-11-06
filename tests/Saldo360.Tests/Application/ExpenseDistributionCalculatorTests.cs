using Saldo360.Application.Budgets;
using Saldo360.Application.Categories;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.Application;

/// <summary>Dashboard Folio: distribuição das despesas realizadas do mês por categoria principal, conferida com "Despesas do mês" (cálculo puro).</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Profile", "FAST")]
public sealed class ExpenseDistributionCalculatorTests
{
    private static readonly Guid Housing = Guid.NewGuid(), Rent = Guid.NewGuid(), Food = Guid.NewGuid(), PfAccount = Guid.NewGuid(), PjAccount = Guid.NewGuid(), Card = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 11, 10);

    private static readonly Dictionary<Guid, FinancialCategoryDto> Categories = new()
    {
        [Housing] = new(Housing, "Moradia", CategoryType.Expense, true, DateTimeOffset.UnixEpoch),
        [Rent] = new(Rent, "Aluguel", CategoryType.Expense, true, DateTimeOffset.UnixEpoch, Housing, "Moradia"),
        [Food] = new(Food, "Alimentação", CategoryType.Expense, true, DateTimeOffset.UnixEpoch),
    };
    private static readonly Dictionary<Guid, Guid> Payers = new() { [Card] = PfAccount };

    private static BudgetFact Bank(decimal amount, Guid? category, Guid? account = null, BudgetFactState state = BudgetFactState.Realized, BudgetFactOrigin origin = BudgetFactOrigin.Transaction) =>
        new(origin, Guid.NewGuid(), Day, amount, state, category, ExpenseNature.Variable, "Despesa", AccountId: account ?? PfAccount);
    private static BudgetFact CardFact(decimal amount, Guid? category, BudgetFactOrigin origin = BudgetFactOrigin.CardInstallment) =>
        new(origin, Guid.NewGuid(), Day, amount, BudgetFactState.CardLaunched, category, ExpenseNature.Variable, "Compra", Card, "Nubank", InvoiceCompetence: "2026-11");

    private static ExpenseDistributionDto Build(IReadOnlyList<BudgetFact> facts, string? reference, Guid? account = null) =>
        ExpenseDistributionCalculator.Build("2026-11", facts, Categories, Payers, account, reference);

    [Fact]
    public void GroupsSubcategoriesUnderTheMainCategoryKeepsUncategorizedAndOrdersByAmountWithServerPercentages()
    {
        var result = Build([Bank(1000m, Rent), Bank(500m, Housing), CardFact(300m, Food), Bank(200m, null)], "2000.00");

        Assert.Equal("Complete", result.State); Assert.Empty(result.Reasons);
        Assert.Equal("2000.00", result.Total); Assert.Equal("2000.00", result.Reference);
        Assert.Equal(["Moradia", "Alimentação", ExpenseDistributionCalculator.Uncategorized], result.Categories.Select(c => c.Name));
        Assert.Equal(Housing, result.Categories[0].CategoryId); Assert.Null(result.Categories[2].CategoryId);
        Assert.Equal(["1500.00", "300.00", "200.00"], result.Categories.Select(c => c.Amount));
        Assert.Equal(["75.0", "15.0", "10.0"], result.Categories.Select(c => c.Percentage));
    }

    [Fact]
    public void CountsOnlyTheRealizedConsumptionAndNetsRefundsAndCardCredits()
    {
        var result = Build([
            Bank(1000m, Rent), Bank(-100m, Rent, origin: BudgetFactOrigin.BankRefund), CardFact(300m, Food), CardFact(-50m, Food, BudgetFactOrigin.CardAdjustment),
            Bank(999m, Food, state: BudgetFactState.Pending, origin: BudgetFactOrigin.PlannedTransaction), CardFact(400m, Food, BudgetFactOrigin.CardKnownInstallment),
        ], "1150.00");

        Assert.Equal("Complete", result.State);
        Assert.Equal(["900.00", "250.00"], result.Categories.Select(c => c.Amount)); // previsão pendente e parcela histórica conhecida não são realizado
    }

    [Fact]
    public void ATotalThatDoesNotMatchTheExpensesIsPartialAndNothingIsInventedToCloseIt()
    {
        var result = Build([Bank(1000m, Rent)], "1200.00");
        Assert.Equal("Partial", result.State); Assert.Equal([ExpenseDistributionCalculator.Reasons.TotalDiffersFromExpenses], result.Reasons);
        Assert.Equal("1000.00", result.Total); Assert.Single(result.Categories); Assert.Equal("100.0", result.Categories[0].Percentage);

        var noReference = Build([Bank(1000m, Rent)], null);
        Assert.Equal("Partial", noReference.State); Assert.Equal([ExpenseDistributionCalculator.Reasons.ReferenceUnavailable], noReference.Reasons);
    }

    [Fact]
    public void ARefundLargerThanTheCategoryPurchasesKeepsTheValuesButDropsThePercentages()
    {
        var result = Build([Bank(1000m, Rent), Bank(-300m, Food, origin: BudgetFactOrigin.BankRefund)], "700.00");
        Assert.Equal("Complete", result.State); Assert.Equal([ExpenseDistributionCalculator.Reasons.PercentagesNotRepresentative], result.Reasons);
        Assert.Equal(["1000.00", "-300.00"], result.Categories.Select(c => c.Amount));
        Assert.All(result.Categories, c => Assert.Null(c.Percentage));
    }

    [Fact]
    public void AFullyRefundedCategoryDoesNotOccupySpace()
    {
        var result = Build([Bank(1000m, Rent), Bank(80m, Food), Bank(-80m, Food, origin: BudgetFactOrigin.BankRefund)], "1000.00");
        Assert.Equal(["Moradia"], result.Categories.Select(c => c.Name)); Assert.Empty(result.Reasons);
    }

    [Fact]
    public void ASingleAccountCountsItsOwnRecordsAndTheCardsItPays()
    {
        IReadOnlyList<BudgetFact> facts = [Bank(1000m, Rent, PfAccount), Bank(700m, Food, PjAccount), CardFact(300m, Food)];
        Assert.Equal(["1000.00", "300.00"], Build(facts, "1300.00", PfAccount).Categories.Select(c => c.Amount));
        Assert.Equal(["700.00"], Build(facts, "700.00", PjAccount).Categories.Select(c => c.Amount));
        Assert.Equal("2000.00", Build(facts, "2000.00").Total);
    }

    [Fact]
    public void AMonthWithoutRealizedExpensesIsEmptyAndComplete()
    {
        var result = Build([Bank(500m, Food, state: BudgetFactState.Pending, origin: BudgetFactOrigin.PlannedTransaction)], "0.00");
        Assert.Equal("Complete", result.State); Assert.Equal("0.00", result.Total); Assert.Empty(result.Categories); Assert.Empty(result.Reasons);
    }

    [Fact]
    public void ARemovedCategoryIsNamedAsSuchInsteadOfDisappearing()
    {
        var result = Build([Bank(50m, Guid.NewGuid())], "50.00");
        Assert.Equal(ExpenseDistributionCalculator.RemovedCategory, result.Categories.Single().Name);
    }
}

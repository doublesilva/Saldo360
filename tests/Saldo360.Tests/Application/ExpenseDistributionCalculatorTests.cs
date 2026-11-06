using System.Globalization;
using Saldo360.Application.Budgets;
using Saldo360.Application.Categories;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.Application;

/// <summary>Dashboard Folio: distribuição das despesas realizadas do mês por categoria principal, conferida com "Despesas do mês" por bloco (cálculo puro).</summary>
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

    private static ExpenseReference Ref(decimal total, decimal? bank = null, decimal? cards = null) => new(total, bank ?? total, cards ?? 0m);
    private static ExpenseDistributionDto Build(IReadOnlyList<BudgetFact> facts, ExpenseReference? reference, Guid? account = null, int? top = null) =>
        ExpenseDistributionCalculator.Build("2026-11", facts, Categories, Payers, account, reference, top);

    [Fact]
    public void GroupsSubcategoriesUnderTheMainCategoryKeepsUncategorizedAndOrdersByAmountWithServerPercentages()
    {
        var result = Build([Bank(1000m, Rent), Bank(500m, Housing), CardFact(300m, Food), Bank(200m, null)], Ref(2000m, 1700m, 300m));

        Assert.Equal("Complete", result.State); Assert.Empty(result.Reasons);
        Assert.Equal("2000.00", result.Total); Assert.Equal("2000.00", result.Reference); Assert.Equal("0.00", result.Adjustments);
        Assert.Equal(["Moradia", "Alimentação", ExpenseDistributionCalculator.Uncategorized], result.Categories.Select(c => c.Name));
        Assert.Equal(Housing, result.Categories[0].CategoryId); Assert.Null(result.Categories[2].CategoryId);
        Assert.Equal(["1500.00", "300.00", "200.00"], result.Categories.Select(c => c.Amount));
        Assert.Equal(["75.0", "15.0", "10.0"], result.Categories.Select(c => c.Percentage));
    }

    [Fact]
    public void CountsOnlyTheRealizedConsumptionAndShowsRefundsAndCardCreditsSeparatelyKeepingTheNetTotal()
    {
        var result = Build([
            Bank(1000m, Rent), Bank(-100m, Rent, origin: BudgetFactOrigin.BankRefund), CardFact(300m, Food), CardFact(-50m, Food, BudgetFactOrigin.CardAdjustment),
            Bank(999m, Food, state: BudgetFactState.Pending, origin: BudgetFactOrigin.PlannedTransaction), CardFact(400m, Food, BudgetFactOrigin.CardKnownInstallment),
        ], Ref(1150m, 900m, 250m));

        Assert.Equal("Complete", result.State);
        Assert.Equal("1150.00", result.Total); Assert.Equal("1300.00", result.Gross); Assert.Equal("-150.00", result.Adjustments); // gasto − devoluções e estornos = líquido
        Assert.Equal(["900.00", "250.00"], result.Categories.Select(c => c.Amount)); // líquido preservado por linha
        Assert.Equal(["1000.00", "300.00"], result.Categories.Select(c => c.Gross)); // a barra usa só os gastos
        Assert.Equal(["76.9", "23.1"], result.Categories.Select(c => c.Percentage));
    }

    [Fact]
    public void ATotalThatDoesNotMatchIsPartialWithTheBucketThatExplainsItAndNothingIsInventedToCloseIt()
    {
        // Indicador: banco 800 (abate uma devolução sem vínculo) e cartões 700 (fatura fechada pelo total do banco). Distribuição: banco 1000, cartões 300.
        var result = Build([Bank(1000m, Rent), CardFact(300m, Food)], Ref(1500m, 800m, 700m));
        Assert.Equal("Partial", result.State);
        Assert.Equal([ExpenseDistributionCalculator.Reasons.TotalDiffersFromExpenses, ExpenseDistributionCalculator.Reasons.BankBasisDiffers, ExpenseDistributionCalculator.Reasons.CardBasisDiffers], result.Reasons);
        Assert.Equal("1300.00", result.Total); Assert.Equal("1500.00", result.Reference); Assert.Equal(2, result.Categories.Count);
        Assert.Equal([("Bank", "1000.00", "800.00", "200.00"), ("Cards", "300.00", "700.00", "-400.00")], result.Reconciliation!.Select(r => (r.Bucket, r.Distributed, r.Reference, r.Difference)));

        var noReference = Build([Bank(1000m, Rent)], null);
        Assert.Equal("Partial", noReference.State); Assert.Equal([ExpenseDistributionCalculator.Reasons.ReferenceUnavailable], noReference.Reasons); Assert.Null(noReference.Reconciliation);
    }

    [Fact]
    public void ARefundLargerThanTheCategoryPurchasesKeepsTheBarBuiltOnlyFromSpendingAndTheNetValues()
    {
        var result = Build([Bank(1000m, Rent), Bank(100m, Food), Bank(-300m, Food, origin: BudgetFactOrigin.BankRefund)], Ref(800m));
        Assert.Equal("Complete", result.State); Assert.Empty(result.Reasons);
        Assert.Equal("800.00", result.Total); Assert.Equal("-300.00", result.Adjustments);
        Assert.Equal(["1000.00", "-200.00"], result.Categories.Select(c => c.Amount)); // a categoria com mais devolução que compra mantém o líquido negativo
        Assert.Equal(["90.9", "9.1"], result.Categories.Select(c => c.Percentage)); // mas a barra é de gastos positivos e fecha em 100,0
        Assert.Equal(1000, result.Categories.Sum(c => (int)(decimal.Parse(c.Percentage!, CultureInfo.InvariantCulture) * 10)));
    }

    [Fact]
    public void AFullyRefundedMonthHasNoSpendingRowsButKeepsTheNetAndTheAdjustments()
    {
        var result = Build([Bank(-80m, Food, origin: BudgetFactOrigin.BankRefund)], Ref(-80m));
        Assert.Empty(result.Categories); Assert.Equal("-80.00", result.Total); Assert.Equal("-80.00", result.Adjustments); Assert.Equal("0.00", result.Gross);
        var cancelled = Build([Bank(1000m, Rent), Bank(80m, Food), Bank(-80m, Food, origin: BudgetFactOrigin.BankRefund)], Ref(1000m));
        Assert.Equal(["Moradia", "Alimentação"], cancelled.Categories.Select(c => c.Name)); Assert.Equal("0.00", cancelled.Categories[1].Amount);
    }

    [Fact]
    public void ASingleAccountCountsItsOwnRecordsAndTheCardsItPays()
    {
        IReadOnlyList<BudgetFact> facts = [Bank(1000m, Rent, PfAccount), Bank(700m, Food, PjAccount), CardFact(300m, Food)];
        Assert.Equal(["1000.00", "300.00"], Build(facts, Ref(1300m, 1000m, 300m), PfAccount).Categories.Select(c => c.Amount));
        Assert.Equal(["700.00"], Build(facts, Ref(700m), PjAccount).Categories.Select(c => c.Amount));
        Assert.Equal("2000.00", Build(facts, Ref(2000m, 1700m, 300m)).Total);
    }

    [Fact]
    public void AMonthWithoutRealizedExpensesIsEmptyAndComplete()
    {
        var result = Build([Bank(500m, Food, state: BudgetFactState.Pending, origin: BudgetFactOrigin.PlannedTransaction)], Ref(0m));
        Assert.Equal("Complete", result.State); Assert.Equal("0.00", result.Total); Assert.Empty(result.Categories); Assert.Empty(result.Reasons);
    }

    [Fact]
    public void TheOverviewShowsTheFiveLargestGroupsTheRestAsOtherCategoriesAndUncategorizedSeparatelyAndLast()
    {
        var extra = Enumerable.Range(1, 7).Select(i => new FinancialCategoryDto(Guid.NewGuid(), $"Cat {i}", CategoryType.Expense, true, DateTimeOffset.UnixEpoch)).ToList();
        var categories = new Dictionary<Guid, FinancialCategoryDto>(Categories); foreach (var c in extra) categories[c.Id] = c;
        // Cat 1..7 = 700, 600, ..., 100; "Sem categoria" = 650 (maior que várias, mas fica separada e por último).
        IReadOnlyList<BudgetFact> facts = [.. extra.Select((c, i) => Bank(700m - i * 100m, c.Id)), Bank(650m, null)];

        var result = ExpenseDistributionCalculator.Build("2026-11", facts, categories, Payers, null, Ref(3450m), ExpenseDistributionCalculator.OverviewTop);

        Assert.Equal(["Cat 1", "Cat 2", "Cat 3", "Cat 4", "Cat 5", ExpenseDistributionCalculator.OtherCategories, ExpenseDistributionCalculator.Uncategorized], result.Categories.Select(c => c.Name));
        Assert.Equal(["Category", "Category", "Category", "Category", "Category", "Others", "Uncategorized"], result.Categories.Select(c => c.Kind));
        var others = result.Categories[5];
        Assert.Equal("300.00", others.Amount); Assert.Equal(2, others.CategoryCount); Assert.Null(others.CategoryId); // agrupamento só de apresentação
        Assert.Equal(7, result.CategoryCount); Assert.Equal("3450.00", result.Total);
        Assert.Equal(1000, result.Categories.Sum(c => (int)(decimal.Parse(c.Percentage!, CultureInfo.InvariantCulture) * 10))); // soma exata de 100,0

        var full = ExpenseDistributionCalculator.Build("2026-11", facts, categories, Payers, null, Ref(3450m));
        Assert.Equal(8, full.Categories.Count); Assert.DoesNotContain(full.Categories, c => c.Kind == "Others"); Assert.Equal("Uncategorized", full.Categories[^1].Kind);
    }

    [Fact]
    public void PercentagesUseTheLargestRemainderSoTheRowsAddUpToExactlyOneHundred()
    {
        Assert.Equal(["33.4", "33.3", "33.3"], ExpenseDistributionCalculator.Shares([100m, 100m, 100m], 300m));
        Assert.Equal(["66.7", "33.3"], ExpenseDistributionCalculator.Shares([200m, 100m], 300m));
        Assert.Equal(["14.3", "14.3", "14.3", "14.3", "14.3", "14.3", "14.2"], ExpenseDistributionCalculator.Shares([.. Enumerable.Repeat(10m, 7)], 70m));
    }

    [Fact]
    public void TiesAreOrderedDeterministicallyByNameAndThenByIdentifier()
    {
        Assert.Equal(["Alimentação", "Moradia"], Build([Bank(300m, Food), Bank(300m, Housing)], Ref(600m)).Categories.Select(c => c.Name));
        Assert.Equal(["Alimentação", "Moradia"], Build([Bank(300m, Housing), Bank(300m, Food)], Ref(600m)).Categories.Select(c => c.Name));
    }

    [Fact]
    public void ARemovedCategoryIsNamedAsSuchInsteadOfDisappearing()
    {
        Assert.Equal(ExpenseDistributionCalculator.RemovedCategory, Build([Bank(50m, Guid.NewGuid())], Ref(50m)).Categories.Single().Name);
    }
}

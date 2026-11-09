using Saldo360.Application.Budgets;
using Saldo360.Application.Categories;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.Application;

/// <summary>Previsão de despesas do orçamento PF por categoria: efetivado + comprometido por categoria principal, conciliada com a referência por componente (cálculo puro).</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Profile", "FAST")]
public sealed class BudgetCategoryForecastCalculatorTests
{
    private static readonly Guid Housing = Guid.NewGuid(), Rent = Guid.NewGuid(), Food = Guid.NewGuid(), Pf = Guid.NewGuid(), Pj = Guid.NewGuid(), PfCard = Guid.NewGuid(), PjCard = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly IReadOnlySet<Guid> PfIds = new HashSet<Guid> { Pf };
    private static readonly Dictionary<Guid, Guid> Payers = new() { [PfCard] = Pf, [PjCard] = Pj };
    private static readonly Dictionary<Guid, FinancialCategoryDto> Categories = new()
    {
        [Housing] = new(Housing, "Moradia", CategoryType.Expense, true, DateTimeOffset.UnixEpoch),
        [Rent] = new(Rent, "Aluguel", CategoryType.Expense, true, DateTimeOffset.UnixEpoch, Housing, "Moradia"),
        [Food] = new(Food, "Alimentação", CategoryType.Expense, true, DateTimeOffset.UnixEpoch),
    };

    private static BudgetFact Bank(decimal amount, Guid? category, Guid? account = null, BudgetFactState state = BudgetFactState.Realized, ExpenseNature nature = ExpenseNature.Variable,
        BudgetFactOrigin origin = BudgetFactOrigin.Transaction) =>
        new(origin, Guid.NewGuid(), Day, amount, state, category, nature, "Despesa", AccountId: account ?? Pf);
    private static BudgetFact CardFact(decimal amount, Guid? category, Guid card, BudgetFactOrigin origin = BudgetFactOrigin.CardInstallment) =>
        new(origin, Guid.NewGuid(), Day, amount, BudgetFactState.CardLaunched, category, ExpenseNature.Variable, "Compra", card, "Cartão", InvoiceCompetence: "2026-11");

    private static BudgetCategoriesDto Build(IReadOnlyList<BudgetFact> facts, BudgetReference? reference, int top = 5) => BudgetCategoryForecastCalculator.Build("2026-11", facts, Categories, Payers, PfIds, reference, top);

    [Fact]
    public void GroupsByMainCategoryWithSubcategoriesAndSplitsEffectiveFromCommittedKeepingEveryComponentApart()
    {
        var result = Build([Bank(1000m, Rent), Bank(500m, Housing, state: BudgetFactState.Pending), CardFact(300m, Food, PfCard), CardFact(200m, Food, PfCard, BudgetFactOrigin.CardKnownInstallment), Bank(100m, null)],
            new(1400m, 700m, 1500m, 12500m));

        Assert.Equal("Complete", result.State); Assert.Empty(result.Reasons);
        Assert.Equal(["Moradia", "Alimentação", "Sem categoria"], result.Categories.Select(c => c.Name));
        var housing = result.Categories[0];
        Assert.Equal(("1000.00", "500.00", "1500.00"), (housing.Realized, housing.Committed, housing.Projected));
        Assert.Equal(["Aluguel", "Moradia"], housing.Children.Select(c => c.Name));
        Assert.Equal(("300.00", "200.00", "500.00"), (result.Categories[1].Realized, result.Categories[1].Committed, result.Categories[1].Projected));
        // Cada perspectiva soma o total previsto da categoria (nunca entre si): pagamento e natureza.
        Assert.Equal(("0.00", "1500.00"), (housing.Sources.Single(x => x.Key == "Cards").Amount, housing.Sources.Single(x => x.Key == "Bank").Amount));
        Assert.Equal("1500.00", housing.Sources.Single(x => x.Key == "Variable").Amount);
        Assert.Equal("500.00", result.Categories[1].Sources.Single(x => x.Key == "Cards").Amount); // alimentação: só cartão (lançado + parcelas conhecidas)
        // Concilia com a referência: categorias + componentes não alocados = total previsto de referência.
        Assert.Equal(("0.00", "0.00", "1500.00", "3600.00"), (result.UnallocatedRealized, result.UnallocatedCommitted, result.UnallocatedEstimated, result.Projected));
    }

    [Fact]
    public void WhatTheFactsDoNotDiscriminateStaysAsAnExplicitComponentAndMakesTheResultPartial()
    {
        // Referência: realizado 1.500 (fatura fechada pelo total do banco) e comprometido 300 (dívida sem conta); os fatos categorizados somam 1.000 e 0.
        var result = Build([Bank(1000m, Rent)], new(1500m, 300m, 0m, null));
        Assert.Equal("Partial", result.State);
        Assert.Equal([BudgetCategoryForecastCalculator.Reasons.RealizedNotDiscriminated, BudgetCategoryForecastCalculator.Reasons.CommittedNotDiscriminated], result.Reasons);
        Assert.Equal(("500.00", "300.00"), (result.UnallocatedRealized, result.UnallocatedCommitted));
        Assert.Equal("1800.00", result.Projected); Assert.Null(result.Target); // a soma preserva a referência: nada foi forçado para dentro de uma categoria
        Assert.Equal("1000.00", result.Categories.Single().Projected);
    }

    [Fact]
    public void PjSpendingAndPjCardsStayOutAndFactsWithoutAnAccountAreNotAttributed()
    {
        var debtWithoutAccount = new BudgetFact(BudgetFactOrigin.DebtInstallment, Guid.NewGuid(), Day, 250m, BudgetFactState.Pending, Food, ExpenseNature.Fixed, "Parcela");
        var result = Build([Bank(1000m, Rent), Bank(700m, Food, Pj), CardFact(900m, Food, PjCard), debtWithoutAccount], new(1000m, 250m, 0m, null));
        Assert.Equal(["Moradia"], result.Categories.Select(c => c.Name));
        Assert.Equal("250.00", result.UnallocatedCommitted); Assert.Equal("Partial", result.State); // a dívida sem conta aparece como diferença, não como despesa PF
    }

    [Fact]
    public void TheOverviewListsTheFiveLargestOthersAndUncategorizedWhileAllCategoriesKeepsEveryLine()
    {
        var extra = Enumerable.Range(1, 7).Select(i => new FinancialCategoryDto(Guid.NewGuid(), $"Cat {i}", CategoryType.Expense, true, DateTimeOffset.UnixEpoch)).ToList();
        var categories = new Dictionary<Guid, FinancialCategoryDto>(Categories); foreach (var c in extra) categories[c.Id] = c;
        IReadOnlyList<BudgetFact> facts = [.. extra.Select((c, i) => Bank(700m - i * 100m, c.Id)), Bank(650m, null)];
        var result = BudgetCategoryForecastCalculator.Build("2026-11", facts, categories, Payers, PfIds, new(3450m, 0m, 0m, null));

        Assert.Equal(["Cat 1", "Cat 2", "Cat 3", "Cat 4", "Cat 5", "Outras categorias", "Sem categoria"], result.Categories.Select(c => c.Name));
        Assert.Equal(["300.00", "650.00"], [result.Categories[5].Projected, result.Categories[6].Projected]); Assert.Equal(2, result.Categories[5].CategoryCount);
        Assert.Equal(8, result.AllCategories.Count); Assert.Equal(7, result.CategoryCount); Assert.Equal("Complete", result.State);
    }

    [Fact]
    public void RefundsKeepTheNetValueAndAnUnavailableReferenceIsFlaggedWithoutInventingTotals()
    {
        var result = Build([Bank(1000m, Rent), Bank(-300m, Food, origin: BudgetFactOrigin.BankRefund)], null);
        Assert.Equal(["1000.00", "-300.00"], result.Categories.Select(c => c.Projected));
        Assert.Equal([BudgetCategoryForecastCalculator.Reasons.ReferenceUnavailable], result.Reasons); Assert.Equal("Partial", result.State); Assert.Equal("700.00", result.Projected);
    }

    [Fact]
    public void ProjectedInstallmentsAndSubscriptionsWithASeriesCategoryLeaveTheUncategorizedEstimateWithoutChangingTheTotal()
    {
        var facts = new[] { Bank(1000m, Rent), Bank(500m, Housing, state: BudgetFactState.Pending), CardFact(300m, Food, PfCard), CardFact(200m, Food, PfCard, BudgetFactOrigin.CardKnownInstallment), Bank(100m, null) };
        var reference = new BudgetReference(1400m, 800m, 1500m, 12500m);   // comprometido já inclui as 100 de parcela futura conhecida
        var before = BudgetCategoryForecastCalculator.Build("2026-11", facts, Categories, Payers, PfIds, reference);

        var after = BudgetCategoryForecastCalculator.Build("2026-11", facts, Categories, Payers, PfIds, reference, projectedCards:
        [
            new(PfCard, Food, 100m, Committed: true),       // parcela futura conhecida: comprometido
            new(PfCard, Food, 50m, Committed: false),       // parcela estimada da série: estimado
            new(PjCard, Food, 999m, Committed: false),      // cartão pago por conta PJ: fora do orçamento PF
        ]);

        var food = after.Categories.Single(c => c.Name == "Alimentação");
        Assert.Equal(("300.00", "300.00", "50.00", "650.00"), (food.Realized, food.Committed, food.Estimated, food.Projected));
        Assert.Equal(before.Projected, after.Projected);                                                    // o total projetado só foi redistribuído entre categorias
        Assert.Equal(("1500.00", "1450.00"), (before.UnallocatedEstimated, after.UnallocatedEstimated));    // 50 saíram de "Estimativa sem categoria"
        Assert.Equal(("100.00", "0.00"), (before.UnallocatedCommitted, after.UnallocatedCommitted));        // a parcela futura conhecida (100) deixou de ser "comprometido não discriminado"
    }
}

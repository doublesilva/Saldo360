using Saldo360.Application.Dashboard.Overview;
using Saldo360.Application.Dashboard.PlanActual;

namespace Saldo360.Tests.Application;

/// <summary>T11.2: consumo do orçamento em dois grupos exclusivos, pressões ranqueadas e recomendações determinísticas (cálculo puro).</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Profile", "FAST")]
public sealed class BudgetControlCalculatorTests
{
    private static BudgetControlInput Input(decimal? total = 12500m, decimal? card = 5000m, bool attributable = true, decimal cardsRealized = 2000m, decimal cardsCommitted = 500m, decimal cardsEstimated = 300m,
        decimal food = 0m, decimal outsideRealized = 3000m, decimal outsideCommitted = 1000m, string[]? cardReasons = null, string[]? outsideReasons = null, decimal charges = 0m,
        BudgetOverTarget[]? over = null, decimal overdueExpenses = 0m, decimal overdueIncome = 0m, int uncategorized = 0, bool incomeEstimate = false, decimal? overdraftUsed = null, bool hasCards = true,
        string period = "Current") =>
        new("2026-11", period, attributable ? "all" : "account", attributable, total, card, hasCards, cardsRealized, cardsCommitted, cardsEstimated, food, cardReasons ?? [], outsideRealized, outsideCommitted,
            outsideReasons ?? [], charges, over ?? [], overdueExpenses, overdueIncome, uncategorized, incomeEstimate, overdraftUsed);

    // ---- Consumo ---------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ConfiguredBudgetSplitsIntoTwoExclusiveGroupsWhoseTargetsAddUpToTheTotal()
    {
        var result = BudgetControlCalculator.Build(Input());
        Assert.Equal("5000.00", result.Cards.Target); Assert.Equal("7500.00", result.OutsideCards.Target); Assert.Equal("12500.00", result.Total.Target);
        Assert.Equal("2800.00", result.Cards.Projected); Assert.Equal("4000.00", result.OutsideCards.Projected);
        Assert.Equal("6800.00", result.Total.Projected); // soma dos grupos, nunca somada de novo com categorias/fixas
        Assert.Equal("5000.00", result.Total.Realized); Assert.Equal("1500.00", result.Total.Committed); Assert.Equal("300.00", result.Total.Estimated);
        Assert.Equal("5700.00", result.Total.Margin); Assert.Equal("0.00", result.Total.Excess); Assert.Equal("Complete", result.State);
        Assert.Equal("Complete", result.Cards.State); Assert.Equal("2200.00", result.Cards.Margin); Assert.Equal("3500.00", result.OutsideCards.Margin);
    }

    [Fact]
    public void FoodProvisionIsEstimatedComplementOfTheCardsAndCountsInsideTheirTarget()
    {
        var result = BudgetControlCalculator.Build(Input(food: 700m));
        Assert.Equal("1000.00", result.Cards.Estimated); Assert.Equal("3500.00", result.Cards.Projected); Assert.Equal("1500.00", result.Cards.Margin);
        Assert.Equal("12500.00", result.Total.Target); // a meta de cartões continua dentro do total: o fora dos cartões segue 7.500
        Assert.Equal("7500.00", result.OutsideCards.Target);
    }

    [Fact]
    public void BudgetNotConfiguredShowsConsumptionWithoutInventingTargetsAndAsksToConfigureIt()
    {
        var result = BudgetControlCalculator.Build(Input(total: null, card: null));
        Assert.Equal("TargetNotDefined", result.State); Assert.False(result.Total.TargetDefined);
        Assert.Null(result.Total.Target); Assert.Null(result.Cards.Target); Assert.Null(result.OutsideCards.Target);
        Assert.Null(result.Total.Margin); Assert.Null(result.Total.Excess); Assert.Equal("NoTarget", result.Total.Status);
        Assert.Equal("6800.00", result.Total.Projected); Assert.Equal("TargetNotDefined", result.Cards.State);
        Assert.Contains(result.Recommendations, r => r.Code == "BudgetTotalNotDefined" && r.Action == "OpenBudget");
    }

    [Fact]
    public void CardTargetMissingWithTotalDefinedLeavesBothGroupsWithoutTargetAndRecommendsTheCardTarget()
    {
        var result = BudgetControlCalculator.Build(Input(card: null));
        Assert.Null(result.OutsideCards.Target); Assert.Null(result.Cards.Target); Assert.Equal("12500.00", result.Total.Target);
        Assert.Contains(result.Recommendations, r => r.Code == "CardTargetNotDefined");
    }

    [Fact]
    public void SingleAccountNeverShowsTheGlobalTargetsAsTheAccountsOwn()
    {
        var result = BudgetControlCalculator.Build(Input(attributable: false, over: [new("Category", "Mercado", Guid.NewGuid(), 200m, 800m, false)], food: 700m));
        Assert.False(result.TargetsAttributable); Assert.Equal("TargetNotAttributable", result.Cards.State); Assert.Equal("TargetNotAttributable", result.OutsideCards.State);
        Assert.Null(result.Total.Target); Assert.Null(result.Cards.Target); Assert.Null(result.OutsideCards.Target);
        Assert.Contains("TargetsGlobalOnly", result.Notes);
        Assert.DoesNotContain(result.Pressures, p => p.Kind == "CategoryOverTarget"); // meta global não vira pressão de uma conta
        Assert.Equal("300.00", result.Cards.Estimated); // a provisão alimentar (global) não é atribuída à conta
        Assert.DoesNotContain(result.Recommendations, r => r.Code == "BudgetTotalNotDefined");
    }

    [Fact]
    public void SpendingAboveTheTargetIsPreservedAndFlaggedNeverClipped()
    {
        var result = BudgetControlCalculator.Build(Input(cardsRealized: 5200m, cardsCommitted: 0m, cardsEstimated: 0m));
        Assert.Equal("5200.00", result.Cards.Realized); Assert.Equal("200.00", result.Cards.Excess); Assert.Equal("200.00", result.Cards.ExcessRealized);
        Assert.Equal("Exceeded", result.Cards.Status); Assert.Equal("0.00", result.Cards.Margin);
        var pressure = Assert.Single(result.Pressures, p => p.Kind == "CardsOverTarget");
        Assert.Equal("200.00", pressure.Amount); Assert.Equal("5000.00", pressure.ReferenceAmount); Assert.Equal("Confirmed", pressure.Quality);
        Assert.Contains(result.Recommendations, r => r.Code == "CardsOverTarget" && r.Action == "OpenCards");
    }

    [Fact]
    public void ProjectedExcessThatIsOnlyCommittedIsProjectedAndOneThatNeedsAnEstimateIsEstimated()
    {
        var committed = BudgetControlCalculator.Build(Input(cardsRealized: 4000m, cardsCommitted: 1500m, cardsEstimated: 0m));
        Assert.Equal("Projected", committed.Pressures.Single(p => p.Kind == "CardsOverTarget").Quality);
        var estimated = BudgetControlCalculator.Build(Input(cardsRealized: 4000m, cardsCommitted: 500m, cardsEstimated: 800m));
        Assert.Equal("Estimated", estimated.Pressures.Single(p => p.Kind == "CardsOverTarget").Quality);
    }

    [Fact]
    public void IncompleteSourcesMakeTheGroupPartialAndThePressurePreliminary()
    {
        var result = BudgetControlCalculator.Build(Input(cardsRealized: 5600m, cardReasons: ["CardCoverageIncomplete"]));
        Assert.Equal("Partial", result.Cards.State); Assert.Equal("Partial", result.State);
        Assert.Equal("Preliminary", result.Pressures.Single(p => p.Kind == "CardsOverTarget").Quality);
        Assert.Contains(result.Recommendations, r => r.Code == "CardCoverageIncomplete");
    }

    [Fact]
    public void NoCardsRegisteredIsReportedAsSuchAndTheOutsideGroupStillWorks()
    {
        var result = BudgetControlCalculator.Build(Input(hasCards: false, cardsRealized: 0m, cardsCommitted: 0m, cardsEstimated: 0m));
        Assert.Equal("NoCards", result.Cards.State); Assert.Equal("Complete", result.OutsideCards.State);
    }

    [Fact]
    public void TotalBelowTheCardTargetIsNotedInsteadOfProducingANegativeOutsideTarget()
    {
        var result = BudgetControlCalculator.Build(Input(total: 4000m, card: 5000m));
        Assert.Equal("0.00", result.OutsideCards.Target); Assert.Contains("TotalBelowCardTarget", result.Notes);
    }

    [Fact]
    public void CategoryAndFixedTargetsAreComplementaryViewsAndNeverAddedToTheTotal()
    {
        var baseline = BudgetControlCalculator.Build(Input());
        var withOverlap = BudgetControlCalculator.Build(Input(over: [new("Fixed", "Despesas fixas", null, 300m, 2000m, false), new("Category", "Mercado", Guid.NewGuid(), 200m, 800m, false)]));
        Assert.Equal(baseline.Total.Projected, withOverlap.Total.Projected); Assert.Equal(baseline.Total.Realized, withOverlap.Total.Realized);
        Assert.Equal(["FixedExpensesOverTarget", "CategoryOverTarget"], withOverlap.Pressures.Select(p => p.Kind));
    }

    // ---- Pressões --------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void PressuresAreRankedByVerifiableAmountThenFixedPriorityAndCappedAtFive()
    {
        var result = BudgetControlCalculator.Build(Input(cardsRealized: 5400m, cardsCommitted: 0m, cardsEstimated: 0m, outsideRealized: 8000m, outsideCommitted: 0m, charges: 100m, overdueExpenses: 900m, overdueIncome: 400m,
            overdraftUsed: 1000m, over: [new("Category", "Lazer", null, 250m, 500m, false), new("Category", "Mercado", null, 250m, 800m, false), new("Fixed", "Despesas fixas", null, 150m, 2000m, false)]));
        Assert.Equal(BudgetControlCalculator.MaxPressures, result.Pressures.Count);
        // 900 vencidas · 500 fora dos cartões · 400 cartões e 400 de receita não recebida (empate: cartões têm prioridade) · 250 (Lazer antes de Mercado). Fixas (150), encargos (100) e o não estimável ficam fora.
        Assert.Equal([1, 2, 3, 4, 5], result.Pressures.Select(p => p.Rank));
        Assert.Equal(["OverdueExpenses", "OutsideCardsOverTarget", "CardsOverTarget", "IncomeNotReceived", "CategoryOverTarget"], result.Pressures.Select(p => p.Kind));
        Assert.Equal("Lazer", result.Pressures[4].Subject);
        var again = BudgetControlCalculator.Build(Input(cardsRealized: 5400m, cardsCommitted: 0m, cardsEstimated: 0m, outsideRealized: 8000m, outsideCommitted: 0m, charges: 100m, overdueExpenses: 900m, overdueIncome: 400m,
            overdraftUsed: 1000m, over: [new("Category", "Mercado", null, 250m, 800m, false), new("Category", "Lazer", null, 250m, 500m, false), new("Fixed", "Despesas fixas", null, 150m, 2000m, false)]));
        Assert.Equal(result.Pressures.Select(p => (p.Kind, p.Subject)), again.Pressures.Select(p => (p.Kind, p.Subject))); // ordem de entrada não altera o ranking
    }

    [Fact]
    public void OverdraftChargesAreReportedAsNotEstimableWithNoAmountAndNeverInventedOrZero()
    {
        var result = BudgetControlCalculator.Build(Input(overdraftUsed: 1000m));
        var pressure = Assert.Single(result.Pressures);
        Assert.Equal("OverdraftChargesNotEstimable", pressure.Kind); Assert.Null(pressure.Amount); Assert.Equal("NotEstimable", pressure.Impact); Assert.Equal("NotEstimable", pressure.Quality);
        Assert.Empty(BudgetControlCalculator.Build(Input(overdraftUsed: null)).Pressures);
        Assert.Empty(BudgetControlCalculator.Build(Input(overdraftUsed: 1000m, period: "Closed")).Pressures);
    }

    [Fact]
    public void EachPressureCarriesReferenceImpactQualityAndAnAction()
    {
        var result = BudgetControlCalculator.Build(Input(overdueExpenses: 300m, charges: 80m));
        var overdue = result.Pressures.Single(p => p.Kind == "OverdueExpenses");
        Assert.Equal("Cash", overdue.Impact); Assert.Equal("OpenAgenda", overdue.Action); Assert.Equal("Confirmed", overdue.Quality);
        var charge = result.Pressures.Single(p => p.Kind == "CardFinanceCharges");
        Assert.Equal("BudgetAndCash", charge.Impact); Assert.Equal("OpenCards", charge.Action); Assert.Equal("80.00", charge.Amount);
    }

    // ---- Recomendações ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RecommendationsAreDeterministicAndEachOffersADestination()
    {
        var result = BudgetControlCalculator.Build(Input(cardsRealized: 5600m, cardsCommitted: 0m, cardsEstimated: 0m, outsideRealized: 8000m, outsideCommitted: 0m, uncategorized: 4, incomeEstimate: true, overdueExpenses: 50m));
        Assert.Equal(["CardsOverTarget", "OutsideCardsOverTarget", "ProjectedOverTotal", "UncategorizedExpenses", "IncomeEstimateUnconfirmed", "OverdueExpenses"], result.Recommendations.Select(r => r.Code));
        Assert.All(result.Recommendations, r => Assert.StartsWith("Open", r.Action));
        Assert.Empty(BudgetControlCalculator.Build(Input()).Recommendations);
    }

    // ---- Integridade (fatura paga, fixa no cartão) ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void InvoicePaymentDoesNotRaiseConsumptionAgainAndFixedExpenseOnTheCardCountsOnlyOnceThroughTheInvoice()
    {
        // O BudgetControl deriva "fora dos cartões" como consumo total − consumo do cartão. O consumo já exclui o pagamento de fatura (caixa) e conta o cartão pela fatura.
        var slice = new CardSlice(Guid.NewGuid(), "Nubank", "PF", Guid.NewGuid(), Confirmed: 300m, PendingKnown: 100m, Estimated: 50m, Charges: 0m);
        // Banco: 1.000 de saídas, dos quais 400 são pagamento de fatura. Pendentes: 700 (200 de fatura a pagar + 500 de despesas).
        var consumption = PlanActualCalculator.Consumption(realizedExpense: 1000m, cardPayments: 400m, pendingExpense: 700m, cardOutstanding: 200m, [slice], pendingBankExpense: 0m);
        Assert.Equal(600m, consumption.BankExpenses); // sem o pagamento da fatura
        var outsideRealized = consumption.Realized - slice.Confirmed;
        var outsideCommitted = consumption.Pending - slice.PendingKnown - slice.Estimated;
        Assert.Equal(600m, outsideRealized); // o pagamento de 400 não entra duas vezes (a compra já está na fatura)
        Assert.Equal(500m, outsideCommitted); // a fatura a pagar (200) não é despesa pendente fora dos cartões
        var result = BudgetControlCalculator.Build(Input(cardsRealized: slice.Confirmed, cardsCommitted: slice.PendingKnown, cardsEstimated: slice.Estimated, outsideRealized: outsideRealized, outsideCommitted: outsideCommitted));
        Assert.Equal("1550.00", result.Total.Projected); // 300 + 100 + 50 + 600 + 500
    }
}

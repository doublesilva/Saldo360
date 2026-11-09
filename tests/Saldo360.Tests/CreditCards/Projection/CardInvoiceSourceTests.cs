using Saldo360.Application.Budgets;
using Saldo360.Application.Categories;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.CreditCards.Projection;

/// <summary>
/// T13.6: hierarquia de fontes do valor da fatura (total oficial fechado &gt; último total corrente válido da fatura aberta &gt; projeção local). O total do banco SUBSTITUI a composição local, nunca se soma a
/// ela; a diferença não individualizada aparece (positiva ou negativa) e não vira lançamento. Hoje = 29/10/2026; fechamento dia 8, vencimento dia 15 (fatura aberta = competência 2026-11).
/// </summary>
[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
public sealed class CardInvoiceSourceTests
{
    private static readonly DateOnly Today = CardProjectionEngineTests.Today;
    private static readonly DateOnly Closing = new(2026, 11, 8);
    private static readonly DateOnly Due = new(2026, 11, 15);
    private static readonly DateTimeOffset Fresh = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static PluggyBillFact OpenBill(decimal? total, DateTimeOffset? observed = null, string? currency = "BRL", string id = "open-1", DateOnly? due = null, DateOnly? closing = null) =>
        new(id, closing ?? Closing, due ?? Due, total, currency) { ObservedAt = observed ?? Fresh };

    private static CardFacts Facts(IReadOnlyList<PluggyBillFact>? extraBills = null, IReadOnlyList<CardInvoiceFact>? invoices = null, IReadOnlyList<PendingPurchaseFact>? pending = null,
        IReadOnlyList<InstallmentObservation>? installments = null, CardInfo? card = null) =>
        new(card ?? CardProjectionEngineTests.Card(), [.. CardProjectionEngineTests.StableBills(), .. extraBills ?? []], invoices ?? [], pending ?? [], installments ?? [], [], []);

    private static CardInvoiceProjection November(CardFacts facts) => CardProjectionEngine.Compute(facts, Today).Invoices.Single(i => i.Competence == "2026-11");

    private static IReadOnlyList<PendingPurchaseFact> LocalPurchases(decimal amount = 100m) => [new PendingPurchaseFact("p1", "Loja", amount, new DateOnly(2026, 10, 25))];

    // ---- 1, 2 e 9: fatura fechada ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1000, 600, 400)]    // total bancário maior que as compras individualizadas
    [InlineData(500, 600, -100)]    // menor (ex.: crédito/estorno que o banco já abateu)
    [InlineData(600, 600, 0)]       // igual
    public void AClosedInvoiceAlwaysUsesTheOfficialBankTotalAndShowsTheSignedDifferenceInsteadOfCompensatingIt(decimal bank, decimal local, decimal difference)
    {
        var closed = CardProjectionEngineTests.Closed(new DateOnly(2026, 10, 8), bank, local);

        var invoice = CardProjectionEngine.Compute(Facts(invoices: [closed]), Today).Invoices.Single(i => i.Competence == "2026-10");

        Assert.Equal(("BankClosed", bank, local, difference), (invoice.Source, invoice.Obligation, invoice.LocalTotal, invoice.Undistributed));
        Assert.Equal(bank, invoice.BankTotal);
    }

    // ---- 3, 4, 5, 6: fatura aberta ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AnOpenInvoiceUsesTheLatestValidBankTotalInsteadOfTheLocalCompositionAndTheTwoAreNeverAdded()
    {
        var invoice = November(Facts([OpenBill(350m)], pending: LocalPurchases(100m)));

        Assert.Equal(("BankOpen", 350m, 100m, 250m), (invoice.Source, invoice.Obligation, invoice.LocalTotal, invoice.Undistributed));
        Assert.Equal((350m, "BankOpenBill", Fresh), (invoice.BankTotal, invoice.BankTotalSource, invoice.BankObservedAt));
        Assert.Equal(100m, invoice.ProjectedTotal);   // a projeção local continua disponível, mas não é a referência
    }

    [Fact]
    public void ANewerObservationReplacesTheValueWithoutDuplicatingAnythingAndAnOlderOneNeverOverridesIt()
    {
        var older = OpenBill(350m, Fresh.AddHours(-20), id: "open-a");
        var newer = OpenBill(420m, Fresh, id: "open-b");

        Assert.Equal(420m, November(Facts([older, newer], pending: LocalPurchases())).Obligation);   // vale o mais recente, não a soma
        Assert.Equal(420m, November(Facts([newer, older], pending: LocalPurchases())).Obligation);   // e independe da ordem de chegada
    }

    [Fact]
    public void AnOpenInvoiceWithoutABankTotalFallsBackToTheLocalProjection()
    {
        var invoice = November(Facts(pending: LocalPurchases(100m), installments: [CardProjectionEngineTests.Obs("k", "Sofá 02/04", 80m, 2, 4, new DateOnly(2026, 11, 5), inInvoice: false)]));

        Assert.Equal(("LocalProjection", 180m, null, null), (invoice.Source, invoice.Obligation, invoice.BankTotal, invoice.Undistributed));
        Assert.Equal(180m, invoice.LocalTotal);
    }

    [Fact]
    public void FutureInstallmentsAreProjectedLocallyWhenThereIsNoBankInformation()
    {
        var facts = Facts(installments: [CardProjectionEngineTests.Obs("a", "Sofá 03/04", 80m, 3, 4, new DateOnly(2026, 12, 5), inInvoice: false), CardProjectionEngineTests.Obs("b", "Sofá 02/04", 80m, 2, 4, new DateOnly(2026, 11, 5), inInvoice: false)]);

        var projection = CardProjectionEngine.Compute(facts, Today);

        Assert.All(new[] { "2026-11", "2026-12" }, c => Assert.Equal(("LocalProjection", 80m), (projection.Invoices.Single(i => i.Competence == c).Source, projection.Invoices.Single(i => i.Competence == c).Obligation)));
    }

    [Fact]
    public void AnExplicitZeroTotalFromTheBankIsValidAndDifferentFromAnUnavailableTotal()
    {
        var zero = November(Facts([OpenBill(0m)], pending: LocalPurchases(100m)));
        var unavailable = November(Facts([OpenBill(null)], pending: LocalPurchases(100m)));

        Assert.Equal(("BankOpen", 0m, -100m), (zero.Source, zero.Obligation, zero.Undistributed));              // o banco disse zero: vale zero
        Assert.Equal(("LocalProjection", 100m), (unavailable.Source, unavailable.Obligation));                  // sem total: projeção local
    }

    // ---- 13: validações (identidade, competência, moeda, instante) ---------------------------------------------------------------------

    [Theory]
    [InlineData("stale", -10, "BRL", "BankTotalStale")]        // observação antiga demais
    [InlineData("future", 48, "BRL", "BankTotalStale")]        // instante inconsistente (no futuro)
    [InlineData("usd", 0, "USD", "BankTotalCurrency")]         // moeda diferente
    public void AnObservationThatFailsValidationNeverOverridesTheLocalProjectionAndIsFlagged(string label, int hoursFromNow, string currency, string flag)
    {
        var invoice = November(Facts([OpenBill(999m, Fresh.AddHours(hoursFromNow * (label == "stale" ? 24 : 1)), currency)], pending: LocalPurchases(100m)));

        Assert.Equal(("LocalProjection", 100m), (invoice.Source, invoice.Obligation));
        Assert.Contains(flag, invoice.Flags);
    }

    [Fact]
    public void ABillWithoutAnObservationInstantAndABillOfAnotherCompetenceAreNotAccepted()
    {
        var noInstant = new PluggyBillFact("x", Closing, Due, 999m, "BRL");
        var otherCompetence = OpenBill(999m, id: "y", due: new DateOnly(2026, 12, 15), closing: new DateOnly(2026, 12, 8));

        Assert.Equal("LocalProjection", November(Facts([noInstant], pending: LocalPurchases())).Source);
        var withOther = CardProjectionEngine.Compute(Facts([otherCompetence], pending: LocalPurchases()), Today).Invoices;
        Assert.Equal("LocalProjection", withOther.Single(i => i.Competence == "2026-11").Source);   // o total de dezembro não vaza para novembro
        Assert.Equal(("BankOpen", 999m), (withOther.Single(i => i.Competence == "2026-12").Source, withOther.Single(i => i.Competence == "2026-12").Obligation));
    }

    [Fact]
    public void TheMinimumPaymentAndTheAccountBalanceAreNeverReadAsTheInvoiceTotal()
    {
        var minimumOnly = new PluggyBillFact("m", Closing, Due, null, "BRL") { MinimumPayment = 120m, ObservedAt = Fresh };

        var invoice = November(Facts([minimumOnly], pending: LocalPurchases(100m)));

        Assert.Equal(("LocalProjection", 100m), (invoice.Source, invoice.Obligation));
        Assert.Null(invoice.BankTotal);
    }

    [Fact]
    public void ALessReliableOpenValueNeverOverridesAnOfficialClosedTotalOfTheSameCompetence()
    {
        var closed = CardProjectionEngineTests.Closed(new DateOnly(2026, 10, 8), 1000m, 900m);
        var inconsistentOpen = OpenBill(5m, due: new DateOnly(2026, 10, 15), closing: new DateOnly(2026, 11, 8));

        var invoice = CardProjectionEngine.Compute(Facts([inconsistentOpen], invoices: [closed]), Today).Invoices.Single(i => i.Competence == "2026-10");

        Assert.Equal(("BankClosed", 1000m), (invoice.Source, invoice.Obligation));
    }

    // ---- 7 e 8: parcelamento de fatura e pagamento parcial; Plano × Realizado ------------------------------------------------------------

    [Fact]
    public void TheFinancingInstallmentIsInsideTheBankTotalAndIsNotAddedAgain()
    {
        var financing = CardProjectionEngineTests.Obs("f", "PARCELAMEN FATURA 05/06", 1814.73m, 5, 6, new DateOnly(2026, 11, 5), inInvoice: false) with { Financing = true };
        var invoice = November(Facts([OpenBill(1914.73m)], installments: [financing], pending: LocalPurchases(100m)));

        Assert.Equal(("BankOpen", 1914.73m, 1914.73m, 0m), (invoice.Source, invoice.Obligation, invoice.LocalTotal, invoice.Undistributed));   // 1.814,73 + 100,00, uma vez só
        Assert.Equal(1814.73m, invoice.Financed);                                                                                               // destaque informativo
    }

    [Fact]
    public void APartialPaymentReducesTheCashDueButNeverTheCompetenceConsumptionAndPaymentsAreNotASecondExpense()
    {
        var bills = new[] { OpenBill(600m) };
        InvoicePaymentState State(decimal paid, out CardSlice slice)
        {
            var fact = new CardInvoiceFact(Guid.NewGuid(), new DateOnly(2026, 10, 9), Closing, Due, "2026-11", false, null, null, Composition: 0m, Paid: paid, "NotVerified", null, null);
            var projection = CardProjectionEngine.Compute(Facts(bills, invoices: [fact], pending: LocalPurchases(100m)), Today);
            var invoice = projection.Invoices.Single(i => i.Competence == "2026-11");
            slice = PlanActualCalculator.Slice(projection, [.. bills], "2026-11");
            Assert.Equal(600m, invoice.Obligation); Assert.Equal(Math.Max(0m, 600m - paid), invoice.Remaining);
            return invoice.PaymentState;
        }

        Assert.Equal(InvoicePaymentState.Unpaid, State(0m, out var none));
        Assert.Equal(InvoicePaymentState.Partial, State(250m, out var partial));
        Assert.Equal(InvoicePaymentState.Paid, State(600m, out var full));
        Assert.All(new[] { none, partial, full }, s => Assert.Equal((600m, 0m, 0m), (s.Confirmed, s.PendingKnown, s.Estimated)));   // consumo = total do banco, igual com ou sem pagamento
    }

    [Fact]
    public void ThePlanActualSliceUsesTheBankTotalForOpenAndClosedInvoicesAndTheLocalProjectionOtherwise()
    {
        var closed = CardProjectionEngine.Compute(Facts(invoices: [CardProjectionEngineTests.Closed(new DateOnly(2026, 10, 8), 1000m, 600m)]), Today);
        var open = CardProjectionEngine.Compute(Facts([OpenBill(350m)], pending: LocalPurchases(100m)), Today);
        var local = CardProjectionEngine.Compute(Facts(pending: LocalPurchases(100m)), Today);

        Assert.Equal(1000m, PlanActualCalculator.Slice(closed, [], "2026-10").Confirmed);
        var bankOpen = PlanActualCalculator.Slice(open, [], "2026-11");
        Assert.Equal((350m, 0m, 0m), (bankOpen.Confirmed, bankOpen.PendingKnown, bankOpen.Estimated));
        var projected = PlanActualCalculator.Slice(local, [], "2026-11");
        Assert.Equal(100m, projected.Confirmed + projected.PendingKnown + projected.Estimated);
    }

    // ---- 11 e 12: competências e escopos ----------------------------------------------------------------------------------------------

    [Fact]
    public void NovemberDecemberAndJanuaryEachChargeOnlyTheirOwnInstallmentAndTheirOwnSource()
    {
        InstallmentObservation Fin(string id, int n, DateOnly date) => CardProjectionEngineTests.Obs(id, $"PARCELAMEN FATURA {n:00}/06", 1814.73m, n, 6, date, inInvoice: false) with { Financing = true };
        var facts = Facts([OpenBill(6463.76m)], installments: [Fin("a", 4, new DateOnly(2026, 11, 5)), Fin("b", 5, new DateOnly(2026, 12, 5)), Fin("c", 6, new DateOnly(2027, 1, 5))]);

        var invoices = CardProjectionEngine.Compute(facts, Today).Invoices.ToDictionary(i => i.Competence);

        Assert.Equal(("BankOpen", 6463.76m), (invoices["2026-11"].Source, invoices["2026-11"].Obligation));         // novembro: total do banco (já inclui a prestação 4/6)
        Assert.Equal(("LocalProjection", 1814.73m, 1814.73m), (invoices["2026-12"].Source, invoices["2026-12"].Obligation, invoices["2026-12"].Financed));   // dezembro: 5/6
        Assert.Equal(("LocalProjection", 1814.73m, 1814.73m), (invoices["2027-01"].Source, invoices["2027-01"].Obligation, invoices["2027-01"].Financed));   // janeiro: 6/6
    }

    [Fact]
    public void CardsOfDifferentScopesAndPayersKeepTheirOwnSourceAndTotals()
    {
        var pj = CardProjectionEngineTests.Card() with { Id = Guid.NewGuid(), Name = "Cartão PJ", PayerScope = "PJ", PayerAccountId = Guid.NewGuid() };

        var pf = CardProjectionEngine.Compute(Facts([OpenBill(350m)], pending: LocalPurchases()), Today);
        var local = CardProjectionEngine.Compute(Facts(card: pj, pending: LocalPurchases(40m)), Today);

        Assert.Equal(("PF", "BankOpen", 350m), (pf.Card.PayerScope, pf.Invoices.Single(i => i.Competence == "2026-11").Source, pf.Invoices.Single(i => i.Competence == "2026-11").Obligation));
        Assert.Equal(("PJ", "LocalProjection", 40m), (local.Card.PayerScope, local.Invoices.Single(i => i.Competence == "2026-11").Source, local.Invoices.Single(i => i.Competence == "2026-11").Obligation));
    }

    // ---- 10: divergência preservada na distribuição por categoria -----------------------------------------------------------------------

    [Fact]
    public void ADivergenceBetweenTheBankTotalAndTheCategorizedLinesStaysVisibleAndTheTotalRemainsReconcilable()
    {
        var food = Guid.NewGuid(); var card = Guid.NewGuid(); var payer = Guid.NewGuid();
        var categories = new Dictionary<Guid, FinancialCategoryDto> { [food] = new(food, "Alimentação", CategoryType.Expense, true, DateTimeOffset.UnixEpoch) };
        BudgetFact Line(decimal amount) => new(BudgetFactOrigin.CardInstallment, Guid.NewGuid(), new DateOnly(2026, 11, 5), amount, BudgetFactState.CardLaunched, food, ExpenseNature.Variable, "Compra", card, "Cartão", InvoiceCompetence: "2026-11");
        IReadOnlySet<Guid> pf = new HashSet<Guid> { payer }; var payers = new Dictionary<Guid, Guid> { [card] = payer };

        // Linhas categorizadas somam 1.000; o banco informa 800 (crédito já abatido) ou 1.300 (componentes não individualizados).
        var lower = BudgetCategoryForecastCalculator.Build("2026-11", [Line(1000m)], categories, payers, pf, new BudgetReference(800m, 0m, 0m, null));
        var higher = BudgetCategoryForecastCalculator.Build("2026-11", [Line(1000m)], categories, payers, pf, new BudgetReference(1300m, 0m, 0m, null));

        Assert.Equal(("-200.00", "800.00"), (lower.UnallocatedRealized, lower.Projected));
        Assert.Equal(("300.00", "1300.00"), (higher.UnallocatedRealized, higher.Projected));
        Assert.Contains(BudgetCategoryForecastCalculator.Reasons.RealizedNotDiscriminated, lower.Reasons);
        Assert.Equal("1000.00", lower.Categories.Single().Realized);   // a categoria mantém só o que foi identificado: a diferença não é distribuída
    }
}

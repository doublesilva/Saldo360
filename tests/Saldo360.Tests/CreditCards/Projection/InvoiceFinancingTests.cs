using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.CreditCards.Projection;

/// <summary>
/// T13.3 (item 2): parcelamento de saldo de fatura. Evidência da própria série (nunca a descrição sozinha), principal fora do consumo do orçamento, obrigação integral na fatura e no caixa,
/// encargos desconhecidos como cobertura parcial. Hoje = 29/10/2026 (fechamento dia 8, vencimento dia 15).
/// </summary>
[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
public sealed class InvoiceFinancingTests
{
    private static readonly Guid Card = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Payment = "Credit card payment";

    private static FinancingObservation Obs(string description = "PARCELAMEN FATURA 04/06", decimal amount = 1814.73m, int? total = 6, string? category = Payment, Guid? card = null) =>
        new(card ?? Card, description, amount, total, category);

    // ---- evidência ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASeriesOfAtLeastTwoCandidateInstallmentsWithTheSameAmountIsConfirmedAsFinancing()
    {
        var series = new[] { Obs("PARCELAMEN FATURA 04/06"), Obs("PARCELAMEN FATURA 05/06"), Obs("PARCELAMEN FATURA 06/06") };

        var confirmed = InvoiceFinancingEvidence.ConfirmedSeries(series);

        Assert.All(series, o => Assert.True(InvoiceFinancingEvidence.IsFinancing(o, confirmed)));
    }

    [Fact]
    public void TheDescriptionAloneNeverDecidesAndAnUnconfirmedCandidateIsOnlyListedForReview()
    {
        var sameTextOtherCategory = new[] { Obs(category: "Shopping"), Obs("PARCELAMEN FATURA 05/06", category: "Shopping") };
        var singleCandidate = new[] { Obs() };
        var differentAmounts = new[] { Obs(amount: 100m), Obs("PARCELAMEN FATURA 05/06", amount: 101m) };

        Assert.Empty(InvoiceFinancingEvidence.ConfirmedSeries(sameTextOtherCategory));               // descrição igual sem a categoria do banco: compra comum
        Assert.Empty(InvoiceFinancingEvidence.ConfirmedSeries(singleCandidate));                     // uma parcela só: sem cronograma comprovado
        Assert.Empty(InvoiceFinancingEvidence.ConfirmedSeries(differentAmounts));                    // valores diferentes: não é cronograma
        Assert.Single(InvoiceFinancingEvidence.UnconfirmedCandidates(singleCandidate));              // candidata sem evidência = revisão, nunca reclassificação
    }

    [Fact]
    public void SeriesOfDifferentCardsOrDifferentPurchasesNeverMix()
    {
        var other = Guid.NewGuid();
        var observations = new[] { Obs(), Obs("PARCELAMEN FATURA 05/06", card: other), Obs("OUTRA COISA 01/06") };

        Assert.Empty(InvoiceFinancingEvidence.ConfirmedSeries(observations));   // nenhuma série tem duas parcelas candidatas do MESMO cartão e compra
    }

    // ---- motor: consumo × obrigação ---------------------------------------------------------------------------------------------

    private static CardFacts Facts(bool financing)
    {
        var date = new DateOnly(2026, 12, 5);
        InstallmentObservation Inst(string id, string desc, decimal amount, int n, int total, bool fin = false) =>
            new(id, $"{desc} {n:00}/{total:00}", amount, n, total, date, false, null, null, fin);
        return new(CardProjectionEngineTests.Card(), CardProjectionEngineTests.StableBills(), [], [],
            [Inst("f5", "PARCELAMEN FATURA", 1814.73m, 5, 6, financing), Inst("c1", "Geladeira", 100m, 7, 10)], [], []);
    }

    [Fact]
    public void FinancingStaysInTheInvoiceObligationButOutOfTheBudgetConsumptionComponents()
    {
        var normal = CardProjectionEngine.Compute(Facts(financing: false), CardProjectionEngineTests.Today).Invoices.Single(i => i.Competence == "2026-12");
        var financed = CardProjectionEngine.Compute(Facts(financing: true), CardProjectionEngineTests.Today).Invoices.Single(i => i.Competence == "2026-12");

        // Obrigação (fatura e caixa) idêntica nos dois cenários: nenhum valor some do que precisa ser pago.
        Assert.Equal((normal.Obligation, normal.ProjectedTotal), (financed.Obligation, financed.ProjectedTotal));
        Assert.Equal(1914.73m, financed.Obligation);
        // Consumo (o que alimenta o orçamento): o principal financiado sai de KnownInstallments.
        Assert.Equal((1914.73m, 0m), (normal.KnownInstallments, normal.Financed));
        Assert.Equal((100m, 1814.73m), (financed.KnownInstallments, financed.Financed));
        // Diferença do tratamento = exatamente a parcela, e só no consumo.
        Assert.Equal(1814.73m, (normal.Composition + normal.KnownInstallments + normal.Estimated + normal.PendingKnown) - (financed.Composition + financed.KnownInstallments + financed.Estimated + financed.PendingKnown));
    }

    [Fact]
    public void FinancingMakesTheCoveragePartialBecauseInterestIsUnknownAndNeverZero()
    {
        var projection = CardProjectionEngine.Compute(Facts(financing: true), CardProjectionEngineTests.Today);

        Assert.False(projection.Coverage.Complete);
        Assert.Contains(CardCoverageReasons.InvoiceFinancingComposition, projection.Coverage.Reasons);
        Assert.Contains(CardCoverageReasons.InvoiceFinancingComposition, projection.Invoices.Single(i => i.Competence == "2026-12").Flags);
        // Sem financiamento a razão não aparece.
        Assert.DoesNotContain(CardCoverageReasons.InvoiceFinancingComposition, CardProjectionEngine.Compute(Facts(financing: false), CardProjectionEngineTests.Today).Coverage.Reasons);
    }

    [Fact]
    public void ARegisteredFinancingIsKeptInTheObligationOfAClosedInvoiceAndTheBankTotalStillPrevails()
    {
        var october = new DateOnly(2026, 10, 8);
        var closed = new CardInvoiceFact(Guid.NewGuid(), october.AddMonths(-1).AddDays(1), october, october.AddDays(7), "2026-10", true, 2300m, "Pluggy", Composition: 485.27m, Paid: 0m, "Reconciled", null, null, Financed: 1814.73m);

        var invoice = CardProjectionEngine.Compute(new(CardProjectionEngineTests.Card(), CardProjectionEngineTests.StableBills(), [closed], [], [], [], []), CardProjectionEngineTests.Today)
            .Invoices.Single(i => i.Competence == "2026-10");

        Assert.Equal((2300m, 2300m, 1814.73m), (invoice.Obligation, invoice.BankTotal, invoice.Financed));   // o total do banco prevalece
        Assert.Equal(0m, invoice.Difference);                                                                  // consumo 485,27 + financiamento 1.814,73 = total do banco
    }

    [Fact]
    public void TheDiagnosticSeparatesFinancingFromTheOtherComponentsWithNoResidue()
    {
        var diagnosis = CardCompetenceDiagnostic.Build(CardProjectionEngine.Compute(Facts(financing: true), CardProjectionEngineTests.Today)).Single(d => d.Competence == "2026-12");

        Assert.Equal((100m, 1814.73m, 1914.73m, 0m), (diagnosis.KnownInstallments, diagnosis.InvoiceFinancing, diagnosis.Obligation, diagnosis.ComponentsGap));
    }
}

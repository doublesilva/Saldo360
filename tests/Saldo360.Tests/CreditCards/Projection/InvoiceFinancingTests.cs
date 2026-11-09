using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.CreditCards.Projection;

/// <summary>
/// Parcelamento de saldo de fatura (T13.3 evidência, T13.5 regra contábil). A evidência vem da própria série (nunca da descrição sozinha). Cada prestação é CONSUMO da competência da sua fatura,
/// como numa compra parcelada: o valor já traz principal e juros embutidos informados pelo banco, sem acréscimos. O marcador só distingue o rótulo; nada é somado duas vezes e o pagamento bancário
/// da fatura não é uma segunda despesa. Hoje = 29/10/2026 (fechamento dia 8, vencimento dia 15).
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

        Assert.Empty(InvoiceFinancingEvidence.ConfirmedSeries(sameTextOtherCategory));
        Assert.Empty(InvoiceFinancingEvidence.ConfirmedSeries(singleCandidate));
        Assert.Empty(InvoiceFinancingEvidence.ConfirmedSeries(differentAmounts));
        Assert.Single(InvoiceFinancingEvidence.UnconfirmedCandidates(singleCandidate));
    }

    [Fact]
    public void SeriesOfDifferentCardsOrDifferentPurchasesNeverMix()
    {
        var other = Guid.NewGuid();
        var observations = new[] { Obs(), Obs("PARCELAMEN FATURA 05/06", card: other), Obs("OUTRA COISA 01/06") };

        Assert.Empty(InvoiceFinancingEvidence.ConfirmedSeries(observations));
    }

    // ---- motor ------------------------------------------------------------------------------------------------------------------

    private static InstallmentObservation Inst(string id, string desc, decimal amount, int n, int total, DateOnly date, bool fin = false) =>
        new(id, $"{desc} {n:00}/{total:00}", amount, n, total, date, false, null, null, fin);

    private static CardFacts Facts(bool financing, IReadOnlyList<CardInvoiceFact>? invoices = null, IReadOnlyList<InstallmentObservation>? extra = null)
    {
        var date = new DateOnly(2026, 12, 5);
        IReadOnlyList<InstallmentObservation> observations = [Inst("f5", "PARCELAMEN FATURA", 1814.73m, 5, 6, date, financing), Inst("c1", "Geladeira", 100m, 7, 10, date), .. extra ?? []];
        return new(CardProjectionEngineTests.Card(), CardProjectionEngineTests.StableBills(), invoices ?? [], [], observations, [], []);
    }

    private static CardInvoiceProjection December(CardFacts facts) => CardProjectionEngine.Compute(facts, CardProjectionEngineTests.Today).Invoices.Single(i => i.Competence == "2026-12");

    [Fact]
    public void EachFinancingInstallmentIsConsumptionOfItsInvoiceCompetenceExactlyLikeAnyOtherInstallment()
    {
        var normal = December(Facts(financing: false));
        var financed = December(Facts(financing: true));

        // Mesmo consumo, mesma obrigação, mesmo total: o financiamento só ganha o destaque.
        Assert.Equal((normal.KnownInstallments, normal.Obligation, normal.ProjectedTotal), (financed.KnownInstallments, financed.Obligation, financed.ProjectedTotal));
        Assert.Equal((1914.73m, 1914.73m), (financed.KnownInstallments, financed.Obligation));   // 1.814,73 do financiamento + 100,00 da compra parcelada
        Assert.Equal((0m, 1814.73m), (normal.Financed, financed.Financed));                      // informativo: já está dentro de KnownInstallments
        Assert.Equal(financed.Obligation, financed.Composition + financed.KnownInstallments + financed.Estimated + financed.PendingKnown);   // nada somado duas vezes
    }

    [Fact]
    public void TheFinancingDoesNotMakeTheCoverageArtificiallyPartialBecauseThePrincipalAndInterestAreWhatTheBankCharges()
    {
        var normal = CardProjectionEngine.Compute(Facts(financing: false), CardProjectionEngineTests.Today);
        var financed = CardProjectionEngine.Compute(Facts(financing: true), CardProjectionEngineTests.Today);

        Assert.Equal(normal.Coverage.Complete, financed.Coverage.Complete);
        Assert.Equal(normal.Coverage.Reasons, financed.Coverage.Reasons);
        Assert.Equal(normal.Invoices.Single(i => i.Competence == "2026-12").Flags, financed.Invoices.Single(i => i.Competence == "2026-12").Flags);
    }

    [Fact]
    public void ARegisteredFinancingIsPartOfTheCompositionAndTheBankTotalOfAClosedInvoiceStillPrevails()
    {
        var october = new DateOnly(2026, 10, 8);
        // Composição registrada = compras comuns 485,27 + financiamento 1.814,73 = 2.300,00 (o que o banco cobra).
        var closed = new CardInvoiceFact(Guid.NewGuid(), october.AddMonths(-1).AddDays(1), october, october.AddDays(7), "2026-10", true, 2300m, "Pluggy", Composition: 2300m, Paid: 0m, "Reconciled", null, null, Financed: 1814.73m);

        var invoice = CardProjectionEngine.Compute(new(CardProjectionEngineTests.Card(), CardProjectionEngineTests.StableBills(), [closed], [], [], [], []), CardProjectionEngineTests.Today)
            .Invoices.Single(i => i.Competence == "2026-10");

        Assert.Equal((2300m, 2300m, 1814.73m, 0m), (invoice.Obligation, invoice.Composition, invoice.Financed, invoice.Difference));
    }

    [Fact]
    public void TheDiagnosticShowsTheFinancingAsAHighlightInsideTheInstallmentsWithNoResidue()
    {
        var diagnosis = CardCompetenceDiagnostic.Build(CardProjectionEngine.Compute(Facts(financing: true), CardProjectionEngineTests.Today)).Single(d => d.Competence == "2026-12");

        Assert.Equal((1914.73m, 1814.73m, 1914.73m, 0m), (diagnosis.KnownInstallments, diagnosis.InvoiceFinancing, diagnosis.Obligation, diagnosis.ComponentsGap));
        Assert.Equal(diagnosis.Projected, diagnosis.IndividualizedLines);   // o destaque não é somado de novo
    }

    // ---- integridade: pagamentos, competências e caixa ------------------------------------------------------------------------------

    [Fact]
    public void APartiallyPaidInvoiceBeforeTheFinancingKeepsItsOwnConsumptionAndEachFollowingInstallmentChargesOnlyItsOwnCompetence()
    {
        var october = new DateOnly(2026, 10, 8);
        // Fatura de outubro fechada em 1.000,00 com 300,00 já pagos; o saldo foi parcelado em prestações de 600,00 (2/3 em novembro e 3/3 em dezembro).
        var closed = CardProjectionEngineTests.Closed(october, 1000m, 1000m, paid: 300m);
        var facts = new CardFacts(CardProjectionEngineTests.Card(), CardProjectionEngineTests.StableBills(), [closed], [],
            [Inst("p2", "PARCELAMEN FATURA", 600m, 2, 3, new DateOnly(2026, 11, 5), fin: true), Inst("p3", "PARCELAMEN FATURA", 600m, 3, 3, new DateOnly(2026, 12, 5), fin: true)], [], []);

        var projection = CardProjectionEngine.Compute(facts, CardProjectionEngineTests.Today);
        var oct = projection.Invoices.Single(i => i.Competence == "2026-10"); var nov = projection.Invoices.Single(i => i.Competence == "2026-11"); var dec = projection.Invoices.Single(i => i.Competence == "2026-12");

        Assert.Equal((1000m, 300m, 700m), (oct.Obligation, oct.Paid, oct.Remaining));                  // o pagamento parcial reduz o devido, não vira despesa
        Assert.Equal((600m, 600m, 600m), (nov.KnownInstallments, nov.Obligation, nov.Financed));       // cada prestação cai só na sua competência
        Assert.Equal((600m, 600m, 600m), (dec.KnownInstallments, dec.Obligation, dec.Financed));
        // Consumo das três competências = 1.000 (fatura original) + 600 + 600; o caixa devido = 700 (restante de outubro) + 600 + 600.
        Assert.Equal(2200m, oct.Composition + nov.KnownInstallments + dec.KnownInstallments);
        Assert.Equal(1900m, oct.Remaining + nov.Remaining + dec.Remaining);
    }

    [Fact]
    public void PayingALaterInvoiceInFullOrInPartChangesTheCashDueButNeverTheConsumptionOrDuplicatesIt()
    {
        var november = new DateOnly(2026, 11, 8);
        InvoicePaymentState StateOf(decimal paid)
        {
            var fact = new CardInvoiceFact(Guid.NewGuid(), november.AddMonths(-1).AddDays(1), november, november.AddDays(7), "2026-11", false, null, null, Composition: 600m, Paid: paid, "NotVerified", null, null, Financed: 600m);
            var invoice = CardProjectionEngine.Compute(new(CardProjectionEngineTests.Card(), CardProjectionEngineTests.StableBills(), [fact], [], [], [], []), CardProjectionEngineTests.Today).Invoices.Single(i => i.Competence == "2026-11");
            Assert.Equal((600m, 600m), (invoice.Composition + invoice.KnownInstallments, invoice.Obligation));   // consumo e obrigação independem do pagamento
            Assert.Equal(Math.Max(0m, 600m - paid), invoice.Remaining);
            return invoice.PaymentState;
        }

        Assert.Equal(InvoicePaymentState.Unpaid, StateOf(0m));
        Assert.Equal(InvoicePaymentState.Partial, StateOf(250m));
        Assert.Equal(InvoicePaymentState.Paid, StateOf(600m));
    }

    [Fact]
    public void OpenFutureAndClosedInvoicesAreAllConsistentWithTheSameRule()
    {
        var october = new DateOnly(2026, 10, 8);
        var facts = new CardFacts(CardProjectionEngineTests.Card(), CardProjectionEngineTests.StableBills(), [CardProjectionEngineTests.Closed(october, 1000m, 1000m)], [],
            [Inst("a", "PARCELAMEN FATURA", 600m, 4, 6, new DateOnly(2026, 11, 5), fin: true), Inst("b", "PARCELAMEN FATURA", 600m, 5, 6, new DateOnly(2026, 12, 5), fin: true), Inst("c", "PARCELAMEN FATURA", 600m, 6, 6, new DateOnly(2027, 1, 5), fin: true)], [], []);

        var invoices = CardProjectionEngine.Compute(facts, CardProjectionEngineTests.Today).Invoices.ToDictionary(i => i.Competence);

        Assert.Equal(InvoicePhase.Closed, invoices["2026-10"].Phase);
        Assert.All(new[] { "2026-11", "2026-12", "2027-01" }, c => Assert.Equal((600m, 600m, 600m), (invoices[c].KnownInstallments, invoices[c].Obligation, invoices[c].Financed)));
        Assert.Equal(InvoicePhase.Open, invoices["2026-11"].Phase); Assert.Equal(InvoicePhase.Future, invoices["2026-12"].Phase);
    }
}

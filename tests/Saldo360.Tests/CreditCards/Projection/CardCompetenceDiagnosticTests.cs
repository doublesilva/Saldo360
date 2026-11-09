using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.CreditCards.Projection;

/// <summary>
/// T13.3: diagnóstico somente leitura de uma competência (dezembro) por cartão e escopo PF/PJ, em cima do motor real. Prova que as linhas individualizadas somam o total projetado SEM resíduo
/// (qualquer diferença aparece como <c>ComponentsGap</c>) e que a diferença de uma fatura fechada pelo banco é mostrada como componente não individualizado, nunca distribuída. Hoje = 29/10/2026.
/// </summary>
[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
public sealed class CardCompetenceDiagnosticTests
{
    private static readonly DateOnly Today = CardProjectionEngineTests.Today;
    private static readonly DateOnly December = new(2026, 12, 5);

    private static CardFacts Facts(CardInfo card, IReadOnlyList<CardInvoiceFact>? invoices = null, IReadOnlyList<PendingPurchaseFact>? pending = null,
        IReadOnlyList<InstallmentObservation>? installments = null, IReadOnlyList<RecurringChargeFact>? recurring = null) =>
        new(card, CardProjectionEngineTests.StableBills(), invoices ?? [], pending ?? [], installments ?? [], [], recurring ?? []);

    private static CardInfo Pf => CardProjectionEngineTests.Card();
    private static CardInfo Pj => CardProjectionEngineTests.Card() with { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = "Nubank PJ", PayerScope = "PJ", Scope = "PJ" };

    private static InstallmentObservation Known(string id, string description, decimal amount, int number, int total) =>
        CardProjectionEngineTests.Obs(id, $"{description} {number:00}/{total:00}", amount, number, total, December, inInvoice: false);

    private static CardCompetenceDiagnosis December2026(CardFacts facts) =>
        CardCompetenceDiagnostic.Build(CardProjectionEngine.Compute(facts, Today)).Single(d => d.Competence == "2026-12");

    [Fact]
    public void TheIndividualizedLinesOfDecemberAddUpToTheProjectedTotalWithNoResidueAndTheComponentsAreSeparated()
    {
        var facts = Facts(Pf, installments: [Known("a", "Geladeira", 100m, 7, 10)], pending: [new PendingPurchaseFact("p", "Mercado", 30m, December)],
            recurring: [new RecurringChargeFact(Guid.NewGuid(), "Streaming", 39.90m, December, false, Manual: true, Sequence: 1), new RecurringChargeFact(Guid.NewGuid(), "Cloud", 25m, December, true)]);

        var december = December2026(facts);

        Assert.Equal((100m, 39.90m, 25m, 30m), (december.KnownInstallments, december.CommittedSubscriptions, december.EstimatedSubscriptions, december.PendingPurchases));
        Assert.Equal(194.90m, december.IndividualizedLines);
        Assert.Equal(december.IndividualizedLines, december.Projected);   // sem composição registrada: as linhas explicam todo o total
        Assert.Equal(0m, december.ComponentsGap);                          // nenhum valor não explicado por componente
        Assert.Equal(("PF", InvoicePhase.Future), (december.Scope, december.Phase));
    }

    [Fact]
    public void PfAndPjCardsAreDiagnosedSeparatelyAndNeverMixed()
    {
        var pf = December2026(Facts(Pf, installments: [Known("a", "Geladeira", 100m, 7, 10)]));
        var pj = December2026(Facts(Pj, installments: [Known("b", "Notebook", 295.80m, 2, 4)]));

        Assert.Equal(("PF", 100m), (pf.Scope, pf.Projected));
        Assert.Equal(("PJ", 295.80m), (pj.Scope, pj.Projected));
    }

    [Fact]
    public void AClosedInvoiceShowsTheBankDifferenceAsAnUnattributedComponentAndPaidAndRemainingStaySeparate()
    {
        var october = new DateOnly(2026, 10, 8);
        var facts = Facts(Pf, invoices: [CardProjectionEngineTests.Closed(october, 230m, 200m, paid: 100m)]);

        var diagnosis = CardCompetenceDiagnostic.Build(CardProjectionEngine.Compute(facts, Today)).Single(d => d.Competence == "2026-10");

        Assert.Equal((InvoicePhase.Closed, 230m, 200m), (diagnosis.Phase, diagnosis.OfficialTotal, diagnosis.Registered));
        Assert.Equal(30m, diagnosis.UnattributedToBank);                 // o banco informou 30 a mais que a composição: não é distribuído por suposição
        Assert.Equal((230m, 100m, 130m), (diagnosis.Obligation, diagnosis.Paid, diagnosis.Remaining));   // obrigação × pagamento × saldo devido
    }
}

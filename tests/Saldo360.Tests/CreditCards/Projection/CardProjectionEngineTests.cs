using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.CreditCards.Projection;

/// <summary>T09.4: regras de obrigação do cartão, sem banco. Hoje = 29/10/2026; ciclo estável: fecha dia 8, vence dia 15.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
public sealed class CardProjectionEngineTests
{
    internal static readonly DateOnly Today = new(2026, 10, 29);
    private static readonly Guid CardId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal static CardInfo Card(Guid? payer = null, string origin = "Pluggy", string status = "Active") =>
        new(CardId, "Cartão", "Banco", "1234", "PF", origin, 8, 15, 10000m, 9000m, "Bank", payer ?? PayerId, "Itaú PF", "PF", true, status, null);

    internal static IReadOnlyList<PluggyBillFact> StableBills() =>
        Enumerable.Range(0, 6).Select(i => new DateOnly(2026, 10, 8).AddMonths(-i))
            .Select(closing => new PluggyBillFact($"b{closing:yyyyMM}", closing, closing.AddDays(7), 100m, "BRL")).ToList();

    internal static CardInvoiceFact Closed(DateOnly closing, decimal bankTotal, decimal composition, decimal paid = 0m, string reconciliation = "Reconciled") =>
        new(Guid.NewGuid(), closing.AddMonths(-1).AddDays(1), closing, closing.AddDays(7), closing.AddDays(7).ToString("yyyy-MM"), true, bankTotal, "Pluggy", composition, paid,
            reconciliation, null, null);

    private static CardFacts Facts(IReadOnlyList<CardInvoiceFact>? invoices = null, IReadOnlyList<PendingPurchaseFact>? pending = null,
        IReadOnlyList<InstallmentObservation>? installments = null, IReadOnlyList<PluggyBillFact>? bills = null, CardInfo? card = null, IReadOnlyList<KnownImportedFact>? known = null) =>
        new(card ?? Card(), bills ?? StableBills(), invoices ?? [], pending ?? [], installments ?? [], known ?? []);

    private static CardInvoiceProjection ByCompetence(CardProjection projection, string competence) => projection.Invoices.Single(i => i.Competence == competence);

    // ---- 1, 4, 5: fatura fechada pelo banco ------------------------------------------------------------------------------

    [Fact]
    public void AClosedInvoiceUsesTheBankTotalAndNeverAddsItsComponentsAgain()
    {
        var october = new DateOnly(2026, 10, 8);
        // Composição local (200) + um item pendente datado dentro do ciclo já fechado: o total do banco (230) já os absorveu.
        var facts = Facts([Closed(october, 230m, 200m)], [new PendingPurchaseFact("p", "Loja", 35m, new DateOnly(2026, 10, 5))]);

        var invoice = ByCompetence(CardProjectionEngine.Compute(facts, Today), "2026-10");

        Assert.Equal((InvoicePhase.Closed, ObligationBasis.BankTotal, false), (invoice.Phase, invoice.Basis, invoice.Provisional));
        Assert.Equal((230m, 230m, 230m), (invoice.BankTotal, invoice.Obligation, invoice.Remaining));
        Assert.Equal(1, invoice.AbsorbedItems);
        Assert.Equal(30m, invoice.Difference); // diferença de conciliação: aparece, nunca é compensada
    }

    [Fact]
    public void ADivergentClosedInvoiceKeepsTheBankTotalAsTheConfirmedObligation()
    {
        var facts = Facts([Closed(new DateOnly(2026, 10, 8), 1000m, 600m, reconciliation: "Divergent")]);

        var invoice = ByCompetence(CardProjectionEngine.Compute(facts, Today), "2026-10");

        Assert.Equal((1000m, "Divergent", 400m), (invoice.Obligation, invoice.Reconciliation, invoice.Difference));
        Assert.Equal(InvoicePhase.Closed, invoice.Phase);
    }

    [Fact]
    public void CreditsAndChargesInsideAClosedInvoiceAreNotDeductedOrAddedAgain()
    {
        // Composição já inclui encargos (+30) e estorno (-40): 230 + 30 - 40 = 220. O banco informa 220 e é isso que se deve.
        var facts = Facts([Closed(new DateOnly(2026, 10, 8), 220m, 220m)]);

        var invoice = ByCompetence(CardProjectionEngine.Compute(facts, Today), "2026-10");

        Assert.Equal((220m, 220m, 0m), (invoice.Obligation, invoice.Composition, invoice.Difference));
    }

    // ---- 6, 7, 8: pagamentos -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, InvoicePaymentState.Unpaid, 230)]
    [InlineData(100, InvoicePaymentState.Partial, 130)]
    [InlineData(230, InvoicePaymentState.Paid, 0)]
    [InlineData(260, InvoicePaymentState.Overpaid, 0)]
    public void PaymentsReduceTheRemainingAmountOnceAndTheStateNamesTheOutcome(int paid, InvoicePaymentState state, int remaining)
    {
        var facts = Facts([Closed(new DateOnly(2026, 10, 8), 230m, 230m, paid)]);

        var invoice = ByCompetence(CardProjectionEngine.Compute(facts, Today), "2026-10");

        Assert.Equal((state, (decimal)remaining), (invoice.PaymentState, invoice.Remaining));
        Assert.Equal(230m, invoice.Obligation); // o pagamento nunca altera a obrigação (nem vira consumo)
    }

    // ---- 2: fatura aberta ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheOpenInvoiceIsProjectedFromWhatIsKnownAndFlaggedAsProvisional()
    {
        var facts = Facts([Closed(new DateOnly(2026, 10, 8), 230m, 230m)],
            [new PendingPurchaseFact("a", "Mercado", 40.10m, new DateOnly(2026, 10, 20)), new PendingPurchaseFact("b", "Padaria", 0.20m, new DateOnly(2026, 10, 25))]);

        var open = ByCompetence(CardProjectionEngine.Compute(facts, Today), "2026-11");

        Assert.Equal((InvoicePhase.Open, ObligationBasis.ProjectedOpen, true), (open.Phase, open.Basis, open.Provisional));
        Assert.Equal(40.30m, open.Obligation); // decimal exato: 40.10 + 0.20
        Assert.Null(open.BankTotal);
        Assert.Equal(new DateOnly(2026, 11, 15), open.Due);
    }

    [Fact]
    public void ACardWithNothingPendingStillHasACurrentEmptyInvoice()
    {
        var projection = CardProjectionEngine.Compute(Facts([Closed(new DateOnly(2026, 10, 8), 100m, 100m)]), Today);

        var open = ByCompetence(projection, "2026-11");

        Assert.Equal((InvoicePhase.Open, 0m), (open.Phase, open.Obligation));
        Assert.DoesNotContain(projection.Items, item => item.Competence == "2026-11"); // sem valor nem pagamento não vira obrigação
    }

    // ---- 3: fatura futura ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AFutureInvoiceCarriesOnlyKnownAndEstimatedInstallments()
    {
        var facts = Facts([Closed(new DateOnly(2026, 10, 8), 230m, 230m)], installments:
        [
            Obs("a", "Geladeira 05/10", 100m, 5, 10, new DateOnly(2026, 10, 5), inInvoice: true, bill: new DateOnly(2026, 10, 8)),
            Obs("b", "Geladeira 06/10", 100m, 6, 10, new DateOnly(2026, 11, 5), inInvoice: false),
            Obs("c", "Geladeira 07/10", 100m, 7, 10, new DateOnly(2026, 12, 5), inInvoice: false),
        ]);

        var projection = CardProjectionEngine.Compute(facts, Today);

        Assert.Equal((InvoicePhase.Open, 100m, 100m), (ByCompetence(projection, "2026-11").Phase, ByCompetence(projection, "2026-11").KnownInstallments, ByCompetence(projection, "2026-11").Obligation));
        var december = ByCompetence(projection, "2026-12");
        Assert.Equal((InvoicePhase.Future, ObligationBasis.ProjectedFuture, 100m), (december.Phase, december.Basis, december.KnownInstallments));
        // 08..10 faltam: estimadas só porque há duas parcelas com periodicidade mensal provada
        Assert.Equal(3, projection.Chains.Single().Estimated);
        Assert.Equal(100m, ByCompetence(projection, "2027-01").Estimated);
    }

    // ---- 14: idempotência e 21: precisão ------------------------------------------------------------------------------------

    [Fact]
    public void ComputingTwiceFromTheSameFactsGivesTheSameResult()
    {
        var facts = Facts([Closed(new DateOnly(2026, 10, 8), 230m, 230m, 30m)], [new PendingPurchaseFact("a", "x", 10m, new DateOnly(2026, 10, 20))], installments:
        [
            Obs("a", "Sofa 02/04", 50m, 2, 4, new DateOnly(2026, 11, 5), inInvoice: false),
            Obs("b", "Sofa 01/04", 50m, 1, 4, new DateOnly(2026, 10, 5), inInvoice: true, bill: new DateOnly(2026, 10, 8)),
        ]);

        var first = CardProjectionEngine.Compute(facts, Today);
        var second = CardProjectionEngine.Compute(facts, Today);

        Assert.Equal(first.Invoices.Select(i => (i.Competence, i.Obligation, i.Remaining, i.Estimated)), second.Invoices.Select(i => (i.Competence, i.Obligation, i.Remaining, i.Estimated)));
        Assert.Equal(first.Items.Select(i => (i.DueDate, i.Remaining)), second.Items.Select(i => (i.DueDate, i.Remaining)));
    }

    [Fact]
    public void AmountsAreAddedAsExactDecimalsWithNoFloatingPointDrift()
    {
        var pending = Enumerable.Range(0, 10).Select(i => new PendingPurchaseFact($"p{i}", "Item", 0.1m, new DateOnly(2026, 10, 20))).ToList();

        var open = ByCompetence(CardProjectionEngine.Compute(Facts(pending: pending), Today), "2026-11");

        Assert.Equal(1.0m, open.Obligation);
    }

    // ---- 15, 16: pagadora; 22: cobertura incompleta ------------------------------------------------------------------------

    [Fact]
    public void EveryObligationIsAttributedToTheConfiguredPayerAndNeverCreatesATransfer()
    {
        var pj = Guid.NewGuid();
        var facts = Facts([Closed(new DateOnly(2026, 10, 8), 500m, 500m)], card: Card(pj) with { Scope = "PJ", PayerScope = "PF" });

        var projection = CardProjectionEngine.Compute(facts, Today);

        Assert.All(projection.Items, item => Assert.Equal(pj, item.PayerAccountId));
        Assert.Equal("PJ", projection.Card.Scope); // o cartão continua PJ mesmo com pagadora PF
        Assert.DoesNotContain(CardCoverageReasons.MissingPayerAccount, projection.Coverage.Reasons);
    }

    [Fact]
    public void ACardWithoutPayerOrBankHistoryReportsIncompleteCoverageInsteadOfHidingTheData()
    {
        var card = Card() with { PayerAccountId = null };
        var facts = Facts(bills: [], card: card, pending: [new PendingPurchaseFact("a", "x", 10m, new DateOnly(2026, 10, 20))]);

        var projection = CardProjectionEngine.Compute(facts, Today);

        Assert.False(projection.Coverage.Complete);
        Assert.Contains(CardCoverageReasons.MissingPayerAccount, projection.Coverage.Reasons);
        Assert.Contains(CardCoverageReasons.NoBankHistory, projection.Coverage.Reasons);
        Assert.Equal(CardCycleSchedule.Configured, projection.Schedule.Source);
        Assert.Equal(10m, ByCompetence(projection, "2026-11").Obligation); // o valor conhecido continua visível
        Assert.Contains(CardCoverageReasons.WeakSchedule, ByCompetence(projection, "2026-11").Flags);
    }

    [Fact]
    public void AnOpenCycleWhoseClosingPassedWithoutABankBillAwaitsTheBankInsteadOfBeingTreatedAsClosed()
    {
        // Passou do fechamento de 08/10 e o banco não informou a fatura: segue aberta, sinalizada, nunca "fechada" por data.
        var facts = Facts(pending: [new PendingPurchaseFact("a", "x", 70m, new DateOnly(2026, 10, 3))], bills: StableBills().Skip(1).ToList());

        var invoice = ByCompetence(CardProjectionEngine.Compute(facts, Today), "2026-10");

        Assert.Equal((InvoicePhase.Open, true, 70m), (invoice.Phase, invoice.AwaitingBankBill, invoice.Obligation));
        Assert.Contains(CardCoverageReasons.AwaitingBankBill, invoice.Flags);
    }

    [Fact]
    public void KnownImportedObligationsWithoutInvoiceAreKeptAsObligationsWithTheirOwnDueDate()
    {
        var facts = Facts(known: [new KnownImportedFact(Guid.NewGuid(), 75m, new DateOnly(2026, 12, 20)), new KnownImportedFact(Guid.NewGuid(), 10m, null)]);

        var projection = CardProjectionEngine.Compute(facts, Today);

        Assert.Equal(2, projection.Items.Count(i => i.Kind == ObligationKind.KnownImported));
        Assert.Contains(CardCoverageReasons.KnownWithoutDueDate, projection.Coverage.Reasons);
    }

    internal static InstallmentObservation Obs(string id, string description, decimal amount, int number, int total, DateOnly date, bool inInvoice, DateOnly? bill = null) =>
        new(id, description, amount, number, total, date, inInvoice, bill, bill?.AddDays(7));
}

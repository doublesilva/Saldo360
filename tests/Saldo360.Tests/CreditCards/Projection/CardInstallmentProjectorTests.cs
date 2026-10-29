using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.CreditCards.Projection;

/// <summary>T09.4: parcelamentos reaproveitados, estimados somente com evidência e nunca duplicados.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
public sealed class CardInstallmentProjectorTests
{
    private static readonly DateOnly LatestClosed = new(2026, 10, 8);
    private static readonly CardCycleSchedule Strong = new(8, 15, CardCycleSchedule.BankHistory, true);
    private static readonly CardCycleSchedule Weak = new(8, 15, CardCycleSchedule.LatestBill, false);

    private static InstallmentObservation Billed(string id, string text, decimal amount, int number, int total, string billClosing = "2026-10-08") =>
        CardProjectionEngineTests.Obs(id, text, amount, number, total, DateOnly.Parse(billClosing).AddDays(-3), inInvoice: true, bill: DateOnly.Parse(billClosing));

    private static InstallmentObservation Known(string id, string text, decimal amount, int number, int total, string date) =>
        CardProjectionEngineTests.Obs(id, text, amount, number, total, DateOnly.Parse(date), inInvoice: false);

    // ---- 9: todas as parcelas disponíveis ---------------------------------------------------------------------------------

    [Fact]
    public void WhenTheBankReturnedEveryRemainingInstallmentNothingIsEstimated()
    {
        var chain = Assert.Single(CardInstallmentProjector.Build(
        [
            Billed("a", "TV 02/04", 250m, 2, 4), Known("b", "TV 03/04", 250m, 3, 4, "2026-11-05"), Known("c", "TV 04/04", 250m, 4, 4, "2026-12-05"),
        ], Strong, LatestClosed));

        Assert.Equal((ChainStatus.Complete, 0, 0), (chain.Status, chain.Estimated, chain.Unprojected));
        Assert.Equal([InstallmentOrigin.InInvoice, InstallmentOrigin.Known, InstallmentOrigin.Known], chain.Installments.Select(i => i.Origin));
    }

    // ---- 10, 11: parcelas faltantes e estimadas -----------------------------------------------------------------------------

    [Fact]
    public void MissingTailInstallmentsAreEstimatedMonthlyWithTheKnownAmountAndMarkedAsEstimated()
    {
        var chain = Assert.Single(CardInstallmentProjector.Build(
            [Billed("a", "Notebook 03/06", 199.90m, 3, 6), Known("b", "Notebook 04/06", 199.90m, 4, 6, "2026-11-05")], Strong, LatestClosed));

        Assert.Equal(ChainStatus.Estimated, chain.Status);
        var estimated = chain.Installments.Where(i => i.Origin == InstallmentOrigin.Estimated).ToList();
        Assert.Equal([5, 6], estimated.Select(i => i.Number));
        Assert.All(estimated, i => Assert.Equal(199.90m, i.Amount));
        Assert.Equal([new DateOnly(2026, 12, 8), new DateOnly(2027, 1, 8)], estimated.Select(i => i.Closing));
        Assert.Equal([new DateOnly(2026, 12, 15), new DateOnly(2027, 1, 15)], estimated.Select(i => i.Due));
    }

    [Fact]
    public void AGapBetweenTwoKnownFutureInstallmentsIsFilledOnlyWhenTheMonthlySpacingIsProven()
    {
        var chain = Assert.Single(CardInstallmentProjector.Build(
            [Billed("a", "Sofá 04/09", 80m, 4, 9), Known("b", "Sofá 05/09", 80m, 5, 9, "2026-11-05"), Known("d", "Sofá 07/09", 80m, 7, 9, "2027-01-05"),
             Known("e", "Sofá 08/09", 80m, 8, 9, "2027-02-05"), Known("f", "Sofá 09/09", 80m, 9, 9, "2027-03-05")], Strong, LatestClosed));

        Assert.Equal([6], chain.Installments.Where(i => i.Origin == InstallmentOrigin.Estimated).Select(i => i.Number));
        Assert.Equal(new DateOnly(2026, 12, 8), chain.Installments.Single(i => i.Number == 6).Closing);
    }

    [Fact]
    public void AMissingInstallmentInThePastIsNeverEstimatedAndSignalsADataGap()
    {
        var chain = Assert.Single(CardInstallmentProjector.Build(
            [Billed("a", "Curso 01/05", 90m, 1, 5, "2026-08-08"), Billed("b", "Curso 03/05", 90m, 3, 5, "2026-10-08"), Known("c", "Curso 04/05", 90m, 4, 5, "2026-11-05"),
             Known("d", "Curso 05/05", 90m, 5, 5, "2026-12-05")], Strong, LatestClosed));

        Assert.Equal((ChainStatus.GapDetected, "MissingPastInstallment", 0), (chain.Status, chain.Reason, chain.Estimated));
        Assert.DoesNotContain(chain.Installments, i => i.Number == 2);
    }

    [Fact]
    public void WithASingleKnownInstallmentTheEstimateNeedsAStrongBankSchedule()
    {
        var observation = new[] { Known("a", "Tênis 01/03", 120m, 1, 3, "2026-10-20") };

        var strong = Assert.Single(CardInstallmentProjector.Build(observation, Strong, LatestClosed));
        var weak = Assert.Single(CardInstallmentProjector.Build(observation, Weak, LatestClosed));

        Assert.Equal((ChainStatus.Estimated, 2), (strong.Status, strong.Estimated));
        Assert.Equal((ChainStatus.InsufficientEvidence, 0, 2), (weak.Status, weak.Estimated, weak.Unprojected));
    }

    [Fact]
    public void ASeriesThatStoppedAppearingIsInterruptedAndNotProjected()
    {
        // Última parcela conhecida em agosto; as faturas de setembro e outubro já fecharam sem ela.
        var chain = Assert.Single(CardInstallmentProjector.Build(
            [Billed("a", "Plano 02/06", 70m, 2, 6, "2026-07-08"), Billed("b", "Plano 03/06", 70m, 3, 6, "2026-08-08")], Strong, LatestClosed));

        Assert.Equal((ChainStatus.Interrupted, 0), (chain.Status, chain.Estimated));
        Assert.True(chain.Unprojected > 0);
    }

    // ---- 12: reconhecimento posterior de parcela estimada --------------------------------------------------------------------

    [Fact]
    public void WhenAnEstimatedInstallmentLaterShowsUpTheEstimateIsReplacedNotDuplicated()
    {
        var before = CardInstallmentProjector.Build(
            [Billed("a", "Móvel 02/04", 60m, 2, 4), Known("b", "Móvel 03/04", 60m, 3, 4, "2026-11-05")], Strong, LatestClosed).Single();
        Assert.Equal([4], before.Installments.Where(i => i.Origin == InstallmentOrigin.Estimated).Select(i => i.Number));

        var after = CardInstallmentProjector.Build(
            [Billed("a", "Móvel 02/04", 60m, 2, 4), Known("b", "Móvel 03/04", 60m, 3, 4, "2026-11-05"), Known("c", "Móvel 04/04", 60m, 4, 4, "2026-12-05")], Strong, LatestClosed).Single();

        Assert.Equal((0, 3), (after.Estimated, after.Installments.Count));
        Assert.Single(after.Installments, i => i.Number == 4);
        Assert.Equal(InstallmentOrigin.Known, after.Installments.Single(i => i.Number == 4).Origin);
    }

    [Fact]
    public void ThePendingCopyOfAnInstallmentThatWasAlreadyBilledDoesNotCountTwice()
    {
        var chain = Assert.Single(CardInstallmentProjector.Build(
            [Known("pending", "Fone 03/05", 55m, 3, 5, "2026-10-05"), Billed("posted", "Fone 03/05", 55m, 3, 5)], Strong, LatestClosed));

        Assert.Single(chain.Installments, i => i.Number == 3);
        Assert.Equal(InstallmentOrigin.InInvoice, chain.Installments.Single(i => i.Number == 3).Origin);
    }

    // ---- 13: compras parecidas de origens distintas -------------------------------------------------------------------------

    [Fact]
    public void SimilarPurchasesWithDifferentTotalsOrDescriptionsAreNeverMerged()
    {
        var chains = CardInstallmentProjector.Build(
        [
            Known("a", "Mercado Bom 01/03", 100m, 1, 3, "2026-10-20"), Known("b", "Mercado Bom 01/06", 100m, 1, 6, "2026-10-21"), Known("c", "Mercado Ótimo 01/03", 100m, 1, 3, "2026-10-22"),
        ], Strong, LatestClosed);

        Assert.Equal(3, chains.Count);
        Assert.Equal(2, chains.Count(c => c.Total == 3));
        Assert.Equal(2 + 5 + 2, chains.Sum(c => c.Estimated));
    }

    [Fact]
    public void TwoIdenticalPurchasesInParallelAreAmbiguousAndTheirMissingInstallmentsAreNotGuessed()
    {
        var chain = Assert.Single(CardInstallmentProjector.Build(
            [Known("a", "Loja X 01/04", 40m, 1, 4, "2026-10-20"), Known("b", "Loja X 01/04", 40m, 1, 4, "2026-10-20")], Strong, LatestClosed));

        Assert.Equal((ChainStatus.Ambiguous, "ParallelIdenticalPurchases", 0), (chain.Status, chain.Reason, chain.Estimated));
        Assert.Equal(2, chain.Installments.Count); // o que o banco devolveu continua visível
        Assert.True(chain.Unprojected > 0);
    }

    [Fact]
    public void IdenticalFinalInstallmentsInTheSameInvoiceAreTwoPurchasesNotAProblem()
    {
        var chain = Assert.Single(CardInstallmentProjector.Build(
            [Billed("a", "Loja Y 04/04", 30m, 4, 4), Billed("b", "Loja Y 04/04", 30m, 4, 4)], Strong, LatestClosed));

        Assert.Equal((ChainStatus.Complete, 2), (chain.Status, chain.Installments.Count));
    }

    [Fact]
    public void InstallmentsWithIncompatibleAmountsAreTreatedAsDifferentPurchases()
    {
        var chain = Assert.Single(CardInstallmentProjector.Build(
            [Billed("a", "Loja Z 02/06", 100m, 2, 6), Known("b", "Loja Z 03/06", 180m, 3, 6, "2026-11-05")], Strong, LatestClosed));

        Assert.Equal((ChainStatus.Ambiguous, "InconsistentAmounts", 0), (chain.Status, chain.Reason, chain.Estimated));
    }

    [Fact]
    public void TheDescriptionSuffixIsIgnoredWhenGroupingTheSamePurchase()
    {
        Assert.Equal(CardInstallmentProjector.BaseKey("Amazon  Marketplace 03/12"), CardInstallmentProjector.BaseKey("AMAZON MARKETPLACE (04/12)"));
        Assert.NotEqual(CardInstallmentProjector.BaseKey("Amazon Marketplace 03/12"), CardInstallmentProjector.BaseKey("Amazon Prime 03/12"));
    }

    [Fact]
    public void ARoundingDifferenceInTheLastInstallmentIsNotAnInconsistency()
    {
        var chain = Assert.Single(CardInstallmentProjector.Build(
            [Billed("a", "Notebook 02/03", 333.33m, 2, 3), Known("b", "Notebook 03/03", 333.34m, 3, 3, "2026-11-05")], Strong, LatestClosed));

        Assert.Equal(ChainStatus.Complete, chain.Status);
    }
}

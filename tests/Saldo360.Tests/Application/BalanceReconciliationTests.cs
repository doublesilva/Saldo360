using Saldo360.Application.Accounts.Reconciliation;

namespace Saldo360.Tests.Application;

/// <summary>T09.5: reconstrução do saldo pelo lado do banco e classificação da conta. Regras puras, sem banco.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Accounts")]
public sealed class BalanceReconciliationTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 28);

    private static AnchorFact Anchor(decimal amount, string reliability = "Reliable", string kind = "CurrentBalance") =>
        new(new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero), AsOf, amount, kind, "Pluggy", reliability, null, true, null, null);

    private static BankMovementFact Move(string date, decimal? amount, string state = "Applied", string? reason = null) => new(DateOnly.Parse(date), amount, state, reason);

    // ---- 3, 4: reconstrução anterior e posterior à âncora ----------------------------------------------------------------------

    [Fact]
    public void TheBankBalanceBeforeTheAnchorIsTheObservedBalanceMinusThePostedMovementsSince()
    {
        IReadOnlyList<BankMovementFact> posted = [Move("2026-10-05", 300m), Move("2026-10-10", 500m), Move("2026-10-20", -200m)];

        var openingOfMonth = BalanceReconciliation.BankStartOfDay(Anchor(1000m), posted, new DateOnly(2026, 10, 6));
        var afterTheSecond = BalanceReconciliation.BankStartOfDay(Anchor(1000m), posted, new DateOnly(2026, 10, 20));

        Assert.Equal((1000m - (500m - 200m), null), openingOfMonth); // 700: tudo de 06/10 em diante já está no valor observado
        Assert.Equal((1200m, null), afterTheSecond);
    }

    [Fact]
    public void TheStartOfTheDayAfterTheObservationIsTheObservedBalanceItself()
    {
        IReadOnlyList<BankMovementFact> posted = [Move("2026-10-05", 300m)];

        Assert.Equal((1000m, (string?)null), BalanceReconciliation.BankStartOfDay(Anchor(1000m), posted, AsOf.AddDays(1)));
        Assert.Equal((null, "AfterBankReference"), BalanceReconciliation.BankStartOfDay(Anchor(1000m), posted, AsOf.AddDays(2)));
    }

    // ---- 5: histórico incompleto -----------------------------------------------------------------------------------------------

    [Fact]
    public void BeforeTheImportedHistoryTheBalanceIsUnavailableNeverZero()
    {
        IReadOnlyList<BankMovementFact> posted = [Move("2026-10-05", 300m)];

        var early = BalanceReconciliation.BankStartOfDay(Anchor(1000m), posted, new DateOnly(2026, 9, 1));
        var boundary = BalanceReconciliation.BankStartOfDay(Anchor(1000m), posted, new DateOnly(2026, 10, 5)); // dia da primeira movimentação: fronteira incerta
        var first = BalanceReconciliation.BankStartOfDay(Anchor(1000m), posted, new DateOnly(2026, 10, 6));

        Assert.Equal((null, "BeforeBankHistory"), early);
        Assert.Equal((null, "BeforeBankHistory"), boundary);
        Assert.Equal(1000m, first.Amount);
        Assert.Equal((null, "NoImportedHistory"), BalanceReconciliation.BankStartOfDay(Anchor(1000m), [], new DateOnly(2026, 10, 6)));
    }

    [Fact]
    public void AMovementThatCannotBeSignedInterruptsTheReconstructionUpToItsDate()
    {
        IReadOnlyList<BankMovementFact> posted = [Move("2026-10-01", 100m), Move("2026-10-12", null), Move("2026-10-20", -50m)];

        Assert.Equal(new DateOnly(2026, 10, 13), BalanceReconciliation.BankSideFrom(posted));
        Assert.Equal((null, "BeforeBankHistory"), BalanceReconciliation.BankStartOfDay(Anchor(1000m), posted, new DateOnly(2026, 10, 12)));
        Assert.Equal(1050m, BalanceReconciliation.BankStartOfDay(Anchor(1000m), posted, new DateOnly(2026, 10, 13)).Amount);
    }

    // ---- 15: precisão monetária ----------------------------------------------------------------------------------------------

    [Fact]
    public void AmountsAreAddedAsExactDecimalsAndCentsComparedWithATolerance()
    {
        IReadOnlyList<BankMovementFact> posted = [Move("2026-10-01", 0.1m), Move("2026-10-10", 0.2m), Move("2026-10-11", -0.3m)];

        var start = BalanceReconciliation.BankStartOfDay(Anchor(1000.00m), posted, new DateOnly(2026, 10, 2));

        Assert.Equal(1000.00m - (0.2m - 0.3m), start.Amount);
        Assert.True(BalanceReconciliation.SameCents(0.001m, 0m));
        Assert.False(BalanceReconciliation.SameCents(0.01m, 0m));
    }

    // ---- 12, 11: classificação ---------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutAUsableBankReferenceNothingIsComparedAndTheReasonIsExplicit()
    {
        Assert.Equal((ReconciliationStatus.InsufficientBankReference, "BalanceNatureAmbiguous"), BalanceReconciliation.Classify(false, "BalanceNatureAmbiguous", 10m, 0m, false, "Full"));
        Assert.Equal((ReconciliationStatus.InsufficientBankReference, "NoBankObservation"), BalanceReconciliation.Classify(false, null, null, 0m, false, "None"));
    }

    [Fact]
    public void ADifferenceExactlyExplainedByMovementsInReviewIsNotCalledADivergence()
    {
        var (status, reason) = BalanceReconciliation.Classify(true, null, -20m, -20m, true, "LimitedByReview");

        Assert.Equal((ReconciliationStatus.MovementsInReview, "DifferenceExplainedByReview"), (status, reason));
    }

    [Theory]
    [InlineData(-37.5, 0, "UnexplainedDifference")]
    [InlineData(-37.5, -20, "PartiallyExplainedByReview")]
    public void ADifferenceNobodyExplainsIsARealDivergence(double difference, double review, string reason)
    {
        var (status, found) = BalanceReconciliation.Classify(true, null, (decimal)difference, (decimal)review, false, "Full");

        Assert.Equal((ReconciliationStatus.DivergenceFound, reason), (status, found));
    }

    [Theory]
    [InlineData("Full", false, ReconciliationStatus.NoDivergence)]
    [InlineData("Full", true, ReconciliationStatus.MovementsInReview)]
    [InlineData("LimitedByReview", true, ReconciliationStatus.MovementsInReview)]
    [InlineData("LimitedByBankData", false, ReconciliationStatus.IncompleteHistory)]
    [InlineData("None", false, ReconciliationStatus.IncompleteHistory)]
    public void WithNoDifferenceTheClassificationDescribesHowMuchHistoryCanBeCalculated(string quality, bool reviewLimits, ReconciliationStatus expected) =>
        Assert.Equal(expected, BalanceReconciliation.Classify(true, null, 0m, 0m, reviewLimits, quality).Status);

    [Fact]
    public void AnUnavailableCalculatedBalanceIsAnIncompleteHistoryNotAZeroDifference() =>
        Assert.Equal(ReconciliationStatus.IncompleteHistory, BalanceReconciliation.Classify(true, null, null, 0m, false, "None").Status);

    [Fact]
    public void ReviewMovementsAreRecognizedByStateAndNeverBeforeTheIntegrationStart()
    {
        Assert.True(Move("2026-10-01", 1m, "NeedsReview").InReview);
        Assert.True(Move("2026-10-01", 1m, "Ignored", "UserIgnored").InReview);
        Assert.False(Move("2026-10-01", 1m, "Ignored", "BeforeImportFrom").InReview);
        Assert.False(Move("2026-10-01", 1m, "Applied").InReview);
    }
}

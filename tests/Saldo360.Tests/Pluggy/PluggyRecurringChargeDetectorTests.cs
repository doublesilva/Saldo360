using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.Pluggy;

[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggyRecurringChargeDetectorTests
{
    private static readonly Guid Card = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Category = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static PluggyRecurringChargeObservation Obs(string id, string date, decimal amount = 49.90m, string description = "STREAMING PLUS", string? merchant = "Streaming Plus") =>
        new(Card, Category, description, merchant, "Entertainment", DateOnly.Parse(date), amount, 8, 15, id);

    [Fact] public void TwoCyclesAreOnlyACandidateAndThreeConsecutiveCyclesAreHighConfidence()
    {
        var candidate = Assert.Single(PluggyRecurringChargeDetector.Detect([Obs("1", "2026-08-02"), Obs("2", "2026-09-02")]));
        Assert.Equal(("Candidate", 2), (candidate.Confidence, candidate.EvidenceCount));
        var confirmed = Assert.Single(PluggyRecurringChargeDetector.Detect([Obs("1", "2026-08-02"), Obs("2", "2026-09-02"), Obs("3", "2026-10-02")]));
        Assert.Equal(("High", false, 49.90m), (confirmed.Confidence, confirmed.IsEstimated, confirmed.ProjectedAmount));
    }

    [Fact] public void VariableMonthlyValuesAreEstimatedFromTheLatestConfirmedCharge()
    {
        var result = Assert.Single(PluggyRecurringChargeDetector.Detect([Obs("1", "2026-08-02", 40m), Obs("2", "2026-09-02", 45m), Obs("3", "2026-10-02", 47m)]));
        Assert.True(result.IsEstimated); Assert.Equal(47m, result.ProjectedAmount);
    }

    [Fact] public void GenericAggregatorWithDifferentValuesAndRepeatedPurchasesInOneCycleAreRejected()
    {
        Assert.Empty(PluggyRecurringChargeDetector.Detect([Obs("1", "2026-08-02", 10m, "APPLE.COM/BILL", null), Obs("2", "2026-09-02", 20m, "APPLE.COM/BILL", null), Obs("3", "2026-10-02", 30m, "APPLE.COM/BILL", null)]));
        Assert.Empty(PluggyRecurringChargeDetector.Detect([Obs("1", "2026-08-02"), Obs("2", "2026-09-02"), Obs("2b", "2026-09-20"), Obs("3", "2026-10-02")]));
    }

    [Fact] public void PendingDuplicatePayloadsCollapseByCardDateAmountDescriptionAndMerchant()
    {
        var result = Assert.Single(PluggyRecurringChargeDetector.Detect([Obs("1", "2026-08-02"), Obs("1-copy", "2026-08-02"), Obs("2", "2026-09-02"), Obs("3", "2026-10-02")]));
        Assert.Equal(3, result.EvidenceCount);
    }
}

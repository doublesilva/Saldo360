using Saldo360.Application.Recurrences;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.Application;

/// <summary>Regras puras de vínculo e consolidação de despesas fixas (sem banco, datas fixas).</summary>
[Trait("Area", "Recurrences")]
[Trait("Category", "CORE")]
public sealed class FixedObligationMatcherTests
{
    private static readonly Guid Account = Guid.NewGuid();
    private static readonly DateOnly Due = new(2026, 11, 10);

    private static FixedObligation Obligation(string description = "Aluguel", decimal planned = 200m, bool estimated = false, ObligationSituation situation = ObligationSituation.Planned,
        decimal? actual = null, Guid? account = null, DateOnly? due = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, new DateOnly(2026, 11, 1), null, account ?? Account, Guid.NewGuid(), CategoryType.Expense, ExpenseNature.Fixed, description,
            planned, estimated, due ?? Due, situation, true, actual, actual is null ? null : Due, actual is null ? null : Guid.NewGuid());

    private static SettlementCandidate Tx(string description = "PIX ALUGUEL JOAO", decimal amount = 200m, DateOnly? date = null, Guid? account = null, CategoryType type = CategoryType.Expense) =>
        new(Guid.NewGuid(), account ?? Account, type, description, amount, date ?? Due);

    [Fact]
    public void PlannedWithoutPayment_ConsolidatesThePlannedAmount()
    {
        var o = Obligation(planned: 200m);
        Assert.Equal(200m, o.Consolidated);
        Assert.Equal(200m, FixedObligationIndicators.FixasPrevistasPendentes([o]));
        Assert.Equal(0m, FixedObligationIndicators.FixasRealizadas([o]));
    }

    [Fact]
    public void PaymentEqualToPlanned_ConsolidatesOnce()
    {
        var o = Obligation(planned: 200m, situation: ObligationSituation.Realized, actual: 200m);
        Assert.Equal(200m, FixedObligationIndicators.FixasConsolidadas([o]));
        Assert.Equal(200m, FixedObligationIndicators.FixasRealizadas([o]));
        Assert.Equal(0m, FixedObligationIndicators.FixasPrevistasPendentes([o]));
    }

    [Fact]
    public void PaymentBelowEstimate_IsFavorableDeviation_WithoutResidualBalance()
    {
        var o = Obligation(planned: 200m, estimated: true, situation: ObligationSituation.Realized, actual: 184.50m);
        Assert.Equal(184.50m, o.Consolidated);
        Assert.Equal(184.50m, FixedObligationIndicators.FixasConsolidadas([o]));
        Assert.Equal(0m, FixedObligationIndicators.FixasPrevistasPendentes([o])); // nenhum saldo devedor de 15,50
    }

    [Fact]
    public void NeedsReview_IsNeitherConsolidatedNorPendingCashFlow()
    {
        var o = Obligation(situation: ObligationSituation.NeedsReview);
        Assert.Equal(0m, FixedObligationIndicators.FixasConsolidadas([o]));
        Assert.Equal(0m, FixedObligationIndicators.FixasPrevistasPendentes([o]));
        Assert.Equal(1, FixedObligationIndicators.RevisaoPendente([o]));
    }

    [Fact]
    public void UniqueMatch_IsLinked_ButAmountAloneNeverConfirms()
    {
        var o = Obligation();
        Assert.Single(FixedObligationMatcher.UniquePairs([o], [Tx()]));
        Assert.Empty(FixedObligationMatcher.UniquePairs([o], [Tx(description: "SUPERMERCADO CENTRAL", amount: 200m)]));
    }

    [Fact]
    public void EstimatedObligation_AcceptsApproximateValue_FixedOneRejectsPartial()
    {
        Assert.Single(FixedObligationMatcher.UniquePairs([Obligation(estimated: true)], [Tx(amount: 184.50m)]));
        Assert.Empty(FixedObligationMatcher.UniquePairs([Obligation(estimated: false)], [Tx(amount: 100m)]));
    }

    [Fact]
    public void Ambiguity_InEitherDirection_StaysForManualReview()
    {
        var o = Obligation();
        Assert.Empty(FixedObligationMatcher.UniquePairs([o], [Tx(), Tx(date: Due.AddDays(1))])); // duas movimentações para uma obrigação
        var t = Tx();
        Assert.Empty(FixedObligationMatcher.UniquePairs([Obligation(), Obligation()], [t])); // uma movimentação para duas obrigações
    }

    [Fact]
    public void DifferentAccountTypeOrDateOutsideWindow_DoesNotMatch()
    {
        var o = Obligation();
        Assert.False(FixedObligationMatcher.Matches(o, Tx(account: Guid.NewGuid())));
        Assert.False(FixedObligationMatcher.Matches(o, Tx(type: CategoryType.Income)));
        Assert.False(FixedObligationMatcher.Matches(o, Tx(date: Due.AddDays(40))));
    }

    [Fact]
    public void StableId_IsDeterministicPerRecurrenceAndCompetence()
    {
        var recurrence = Guid.NewGuid();
        Assert.Equal(FixedObligation.DeterministicId(recurrence, new DateOnly(2026, 11, 1)), FixedObligation.DeterministicId(recurrence, new DateOnly(2026, 11, 1)));
        Assert.NotEqual(FixedObligation.DeterministicId(recurrence, new DateOnly(2026, 11, 1)), FixedObligation.DeterministicId(recurrence, new DateOnly(2026, 12, 1)));
    }
}

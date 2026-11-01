using Saldo360.Application.Dashboard.Overview;
using Saldo360.Domain.Accounts;

namespace Saldo360.Tests.Application;

/// <summary>
/// Meta de recuperação do cheque especial (80% da capacidade, fixada no dia 10). O "hoje" é sempre explícito: nenhum teste depende do dia real do mês.
/// </summary>
[Trait("Category", "REGRESSION")]
[Trait("Area", "Dashboard")]
[Trait("Profile", "FAST")]
public sealed class OverdraftRecoveryRuleTests
{
    private static readonly DateOnly First = new(2026, 11, 1);
    private static readonly DateOnly Last = new(2026, 11, 30);
    private static readonly Guid W = Guid.NewGuid(), A = Guid.NewGuid();

    private static RecoveryProjectionInput Projection(decimal? closing, string? block = null) => new(closing, 0m, 0m, "Complete", string.Empty, block);
    private static RecoveryBuild Build(DateOnly today, decimal opening, decimal current, OverdraftRecoverySnapshot? snapshot, RecoveryProjectionInput? projection, DateOnly? last = null) =>
        OverdraftRecoveryRule.Build(First, last ?? Last, today, opening, current, snapshot, projection)!;
    private static OverdraftRecoverySnapshot Fixed(decimal opening, decimal projected, decimal capacity, decimal target) =>
        OverdraftRecoverySnapshot.CreateFixed(W, A, First, opening, First, 0m, 0m, projected, capacity, 0.80m, target, "Complete", string.Empty, new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TargetIsEightyPercentOfTheCapacityRoundedToCentsAwayFromZero()
    {
        Assert.Equal(0.80m, OverdraftRecoveryRule.TargetFactor);
        Assert.Equal(8000m, OverdraftRecoveryRule.Target(10000m));
        Assert.Equal(0.01m, OverdraftRecoveryRule.Target(0.0125m)); // 0.0100 exato
        Assert.Equal(0.04m, OverdraftRecoveryRule.Target(0.05m)); // 0.04 exato
        Assert.Equal(1.21m, OverdraftRecoveryRule.Target(1.51m)); // 1.208 -> 1.21
        Assert.Equal(0.01m, OverdraftRecoveryRule.Target(0.01m)); // 0.008 -> 0.01
        Assert.Equal(0.01m, OverdraftRecoveryRule.Target(0.00625m)); // 0.005 exato: metade afastada de zero (não o arredondamento bancário)
    }

    [Theory]
    [InlineData(10000, -4000, 6000)] // fecha ainda negativo em -4000: capacidade 6000
    [InlineData(10000, 0, 10000)]
    [InlineData(10000, 2500, 10000)] // saldo positivo projetado: limitada ao usado na abertura
    [InlineData(1000, -1500, 0)] // piora projetada: sem capacidade
    public void CapacityIsTheProjectedRecoveryNeverNegative(decimal openingUsed, decimal projectedClosing, decimal expected) =>
        Assert.Equal(expected, OverdraftRecoveryRule.Capacity(openingUsed, projectedClosing));

    [Fact]
    public void NonNegativeOpeningHasNoRecoveryComponent()
    {
        Assert.Null(OverdraftRecoveryRule.Build(First, Last, new(2026, 11, 15), 0m, 100m, null, Projection(100m)));
        Assert.Null(OverdraftRecoveryRule.Build(First, Last, new(2026, 11, 15), 250m, 100m, null, Projection(100m)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void BeforeDayTenTheTargetIsProvisionalAndFollowsTheProjection(int day)
    {
        var today = new DateOnly(2026, 11, day);
        var first = Build(today, -10000m, -9000m, null, Projection(-4000m));
        Assert.False(first.ShouldFix);
        Assert.Equal(("Provisional", "4800.00", "0.80", "6000.00", false), (first.Dto.TargetState, first.Dto.Target, first.Dto.TargetFactor, first.Dto.Capacity, first.Dto.LegacyTarget));
        Assert.Null(first.Dto.TargetFixedAt);
        Assert.Equal(new DateOnly(2026, 11, 10), first.Dto.FixDate);
        // a projeção melhorou: a meta provisória é recalculada
        Assert.Equal("6400.00", Build(today, -10000m, -9000m, null, Projection(-2000m)).Dto.Target);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(28)]
    public void FromDayTenWithoutATargetItAsksToFixItWithTheProjection(int day)
    {
        var build = Build(new DateOnly(2026, 11, day), -10000m, -9000m, null, Projection(-4000m));
        Assert.True(build.ShouldFix);
        Assert.Equal((6000m, 4800m), (build.Capacity, build.Target));
    }

    [Fact]
    public void AFixedTargetIsPreservedWhenTheProjectionChangesAfterwards()
    {
        var snapshot = Fixed(-10000m, -4000m, 6000m, 4800m);
        var worse = Build(new DateOnly(2026, 11, 20), -10000m, -9500m, snapshot, Projection(-9000m)); // novas despesas pioram a projeção
        Assert.False(worse.ShouldFix);
        Assert.Equal(("Fixed", "4800.00", "0.80", false), (worse.Dto.TargetState, worse.Dto.Target, worse.Dto.TargetFactor, worse.Dto.LegacyTarget));
        Assert.NotNull(worse.Dto.TargetFixedAt);
        Assert.Equal("1000.00", worse.Dto.ProjectedRecovery); // a projeção mudou...
        Assert.Equal("20.8", worse.Dto.ProjectedAchievementPercent); // ...e o percentual é contra a meta fixa
        var better = Build(new DateOnly(2026, 11, 20), -10000m, -9500m, snapshot, Projection(-1000m));
        Assert.Equal("4800.00", better.Dto.Target);
        Assert.Equal("9000.00", better.Dto.ProjectedRecovery);
    }

    [Fact]
    public void HistoricalTargetsAreKeptAsStoredAndFlaggedLegacy()
    {
        // meta gravada pelo cálculo anterior (sem fator): 85% de 6.000 = 5.100, preservada exatamente
        var legacy = OverdraftRecoverySnapshot.Create(W, A, First, -10000m, First, 0m, 0m, -4900m, "Complete", string.Empty, new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(5100m, legacy.RecoveryTarget);
        var dto = Build(new DateOnly(2026, 11, 5), -10000m, -9000m, legacy, Projection(-3000m)).Dto;
        Assert.Equal(("Fixed", "5100.00", true, null), (dto.TargetState, dto.Target, dto.LegacyTarget, dto.TargetFactor));
        Assert.Equal(legacy.CapturedAt, dto.TargetFixedAt);
    }

    [Fact]
    public void MissingProjectionOrOpenInvoicesLeaveThePendingStateInsteadOfAnInventedTarget()
    {
        var noProjection = Build(new DateOnly(2026, 11, 12), -10000m, -9000m, null, Projection(null));
        Assert.Equal(("Pending", null, "ProjectionUnavailable", false), (noProjection.Dto.TargetState, noProjection.Dto.Target, noProjection.Dto.PendingReason, noProjection.ShouldFix));
        Assert.Equal("NoTarget", noProjection.Dto.Band);
        var openInvoices = Build(new DateOnly(2026, 11, 12), -10000m, -9000m, null, Projection(-4000m, "CardCoverageIncomplete"));
        Assert.Equal(("Pending", null, "CardCoverageIncomplete", false), (openInvoices.Dto.TargetState, openInvoices.Dto.Target, openInvoices.Dto.PendingReason, openInvoices.ShouldFix));
        // antes do dia 10 a falta de faturas fechadas não bloqueia a meta provisória
        Assert.Equal("Provisional", Build(new DateOnly(2026, 11, 5), -10000m, -9000m, null, Projection(-4000m, "CardCoverageIncomplete")).Dto.TargetState);
    }

    [Fact]
    public void AClosedMonthUsesTheEffectiveClosingAgainstTheFixedTargetAndNeverInventsOne()
    {
        var closed = Build(new DateOnly(2026, 12, 3), -10000m, -3000m, Fixed(-10000m, -4000m, 6000m, 4800m), null);
        Assert.Equal(("7000.00", "145.8", "Exceeded", new DateOnly(2026, 11, 30)), (closed.Dto.FinalRecovery, closed.Dto.FinalAchievementPercent, closed.Dto.Band, closed.Dto.CurrentAsOf));
        Assert.Null(closed.Dto.ProjectedRecovery);
        var none = Build(new DateOnly(2026, 12, 3), -10000m, -3000m, null, null);
        Assert.Equal(("NotRegistered", null, null, "NoTarget"), (none.Dto.TargetState, none.Dto.Target, none.Dto.FinalAchievementPercent, none.Dto.Band));
        Assert.False(none.ShouldFix);
    }

    [Fact]
    public void InProgressUsesTheLatestEffectiveBalanceAndTheProjectedRecovery()
    {
        var build = Build(new DateOnly(2026, 11, 15), -10000m, -8500m, Fixed(-10000m, -4000m, 6000m, 4800m), Projection(-5000m));
        Assert.Equal(("1500.00", "5000.00", "-5000.00", "104.2", "Exceeded", new DateOnly(2026, 11, 15)),
            (build.Dto.RecoveryCurrent, build.Dto.ProjectedRecovery, build.Dto.ProjectedClosing, build.Dto.ProjectedAchievementPercent, build.Dto.Band, build.Dto.CurrentAsOf));
        Assert.Null(build.Dto.FinalRecovery);
    }

    [Theory]
    [InlineData("100.1", "Exceeded")]
    [InlineData("100.0", "Met")]
    [InlineData("80.0", "Met")]
    [InlineData("79.9", "Attention")]
    [InlineData("50.1", "Attention")]
    [InlineData("50.0", "Critical")]
    [InlineData("0.0", "Critical")]
    [InlineData("-12.5", "Critical")]
    [InlineData("250.0", "Exceeded")]
    public void BandsFollowTheDisplayedPercentage(string percent, string band) => Assert.Equal(band, OverdraftRecoveryRule.Band(percent));

    [Fact]
    public void WorseningIsPreservedAsNegativeRecoveryAndPercentage()
    {
        var build = Build(new DateOnly(2026, 12, 2), -1000m, -1300m, Fixed(-1000m, -500m, 500m, 400m), null);
        Assert.Equal(("-300.00", "-75.0", "Critical"), (build.Dto.FinalRecovery, build.Dto.FinalAchievementPercent, build.Dto.Band));
    }

    [Fact]
    public void ZeroTargetMeansNoTargetWithoutAnArtificialPercentage()
    {
        var build = Build(new DateOnly(2026, 11, 20), -1000m, -1100m, Fixed(-1000m, -1200m, 0m, 0m), Projection(-1100m));
        Assert.Equal(("0.00", null, "NoTarget"), (build.Dto.Target, build.Dto.ProjectedAchievementPercent, build.Dto.Band));
        Assert.Null(OverdraftRecoveryRule.Achievement(500m, 0m));
        Assert.Null(OverdraftRecoveryRule.Achievement(500m, null));
    }
}

using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
public sealed class FinancialEmailHistoricalWindowTests
{
    [Theory]
    [InlineData(null, "2026-10-31", 50)]
    [InlineData("2026-10-01", null, 50)]
    [InlineData("2026-02-30", "2026-03-01", 50)]
    [InlineData("01/10/2026", "2026-10-31", 50)]
    [InlineData("2026-11-01", "2026-10-31", 50)]
    [InlineData("2026-01-01", "2027-01-02", 50)]
    [InlineData("2026-10-01", "2026-10-31", 0)]
    [InlineData("2026-10-01", "2026-10-31", 501)]
    [InlineData("9999-12-31", "9999-12-31", 50)]
    public void RejectsInvalidOrUnboundedWindow(string? from, string? through, int limit) =>
        Assert.Throws<ArgumentException>(() => FinancialEmailHistoricalWindow.Create(from, through, limit));

    [Fact]
    public void DatesAreInclusiveInSaoPauloAndDoNotUseContainerTimezone()
    {
        var window = FinancialEmailHistoricalWindow.Create("2026-10-01", "2026-10-31", 500);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero), window.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero), window.EndExclusiveUtc);
        Assert.Equal("(has:attachment (subject:one OR subject:two)) before:" + window.EndExclusiveUtc.ToUnixTimeSeconds(), window.BoundQuery("has:attachment (subject:one OR subject:two)"));
        Assert.Equal(500, window.MaxMessages);
        Assert.NotNull(FinancialEmailHistoricalWindow.Create("2024-01-01", "2024-12-31"));
        Assert.NotNull(FinancialEmailHistoricalWindow.Create("2026-10-01", "2026-10-01"));
    }
}

using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
public sealed class FinancialEmailScheduleTests
{
    private readonly FinancialEmailSchedule schedule = new(new FinancialEmailImportOptions
    {
        TimeZone = "America/Sao_Paulo",
        Schedule = ["09:00", "13:00", "18:00", "23:00"]
    });

    [Theory]
    [InlineData("2026-10-17T11:59:00Z", "2026-10-17T12:00:00Z")]
    [InlineData("2026-10-17T12:01:00Z", "2026-10-17T16:00:00Z")]
    [InlineData("2026-10-17T16:01:00Z", "2026-10-17T21:00:00Z")]
    [InlineData("2026-10-17T21:01:00Z", "2026-10-18T02:00:00Z")]
    public void CalculatesNextSaoPauloWindowWithoutUtcFixedSchedule(string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), schedule.Next(DateTimeOffset.Parse(now)));

    [Fact]
    public void StartupAfterWindowChoosesFutureWindowAndDoesNotReplayMissedRuns()
    {
        var restarted = DateTimeOffset.Parse("2026-10-17T17:30:00Z"); // 14:30 em São Paulo.
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T21:00:00Z"), schedule.Next(restarted));
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T16:00:00Z"), schedule.Previous(restarted));
    }
}

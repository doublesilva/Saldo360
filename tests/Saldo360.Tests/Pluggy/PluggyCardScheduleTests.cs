using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>T09.3.2: o dia habitual de fechamento e de vencimento só é sugerido quando o histórico de faturas do banco é consistente.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggyCardScheduleTests
{
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static PluggyBillFact Bill(int month, int closingDay, int dueDay, int year = 2026) =>
        new($"b{year}{month}", new DateOnly(year, month, closingDay), new DateOnly(year, month, dueDay), 0m, "BRL");

    [Fact]
    public void ConsistentHistorySuggestsClosingAndDueDaysSeparately()
    {
        var bills = new[] { Bill(4, 8, 15), Bill(5, 8, 15), Bill(6, 8, 15), Bill(7, 8, 15), Bill(8, 9, 15), Bill(9, 8, 16) };

        var suggestion = PluggyCardSchedule.Suggest(bills, Today);

        Assert.Equal((8, 15, 6, (string?)null, (string?)null), (suggestion.ClosingDay, suggestion.DueDay, suggestion.SampleSize, suggestion.ClosingReason, suggestion.DueReason));
    }

    [Fact]
    public void ClosingAndDueAreIndependentSoOneCanBeSuggestedWithoutTheOther()
    {
        var bills = new[] { Bill(5, 4, 15), Bill(6, 3, 15), Bill(7, 2, 15), Bill(8, 1, 15) }; // o fechamento varia, o vencimento não

        var suggestion = PluggyCardSchedule.Suggest(bills, Today);

        Assert.Null(suggestion.ClosingDay);
        Assert.Equal("Inconsistent", suggestion.ClosingReason);
        Assert.Equal(15, suggestion.DueDay);
    }

    [Fact]
    public void TooLittleHistoryAndOpenOrFutureBillsNeverProduceASuggestion()
    {
        var future = new PluggyBillFact("f", new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 15), 10m, "BRL");
        var noClosing = new PluggyBillFact("n", null, new DateOnly(2026, 5, 15), 10m, "BRL"); // só vencimento: não é fechamento

        var suggestion = PluggyCardSchedule.Suggest([Bill(8, 8, 15), Bill(9, 8, 15), future, noClosing], Today);

        Assert.Equal((null, null, 2), (suggestion.ClosingDay, suggestion.DueDay, suggestion.SampleSize));
        Assert.Equal(("NotEnoughHistory", "NotEnoughHistory"), (suggestion.ClosingReason, suggestion.DueReason));
    }

    [Fact]
    public void OnlyTheMostRecentBillsCount()
    {
        // Os 6 mais recentes concordam (fechamento 3); os antigos (fechamento 8) não pesam.
        var bills = new[] { Bill(1, 8, 15), Bill(2, 8, 15), Bill(3, 8, 15), Bill(4, 3, 10), Bill(5, 3, 10), Bill(6, 3, 10), Bill(7, 3, 10), Bill(8, 3, 10), Bill(9, 3, 10) };

        var suggestion = PluggyCardSchedule.Suggest(bills, Today);

        Assert.Equal((3, 10), (suggestion.ClosingDay, suggestion.DueDay));
    }
}

using Saldo360.Application.Recurrences;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Recurrences;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
[Trait("Area", "Recurrences")]
[Trait("Profile", "FAST")]
public sealed class FinancialRecurrenceCalendarTests
{
    private static FinancialRecurrence Recurrence(RecurrenceFrequency frequency, DateOnly start, int day = 1, int interval = 1, int? count = null, DateOnly? end = null)
    {
        var r = FinancialRecurrence.Create(Guid.NewGuid(), new RecurrenceDetails(Guid.NewGuid(), Guid.NewGuid(), CategoryType.Expense, "Teste", 1234.56m, true, frequency, interval, start, end, count, day), DateTimeOffset.UnixEpoch);
        return r;
    }

    [Fact]
    public void Monthly31_UsesOriginalAnchorAfterFebruary()
    {
        var result = FinancialRecurrenceCalendar.CalculateOccurrences(Recurrence(RecurrenceFrequency.Monthly, new(2026, 1, 31), 31), new(2026, 1, 1), new(2026, 4, 30));
        Assert.Equal([new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 30)], result.Select(x => x.Date));
    }

    [Fact]
    public void LeapDay_ReturnsTo29InLeapYear()
    {
        var result = FinancialRecurrenceCalendar.CalculateOccurrences(Recurrence(RecurrenceFrequency.Yearly, new(2024, 2, 29), 29), new(2024, 1, 1), new(2028, 3, 1));
        Assert.Equal([new(2024, 2, 29), new(2025, 2, 28), new(2026, 2, 28), new(2027, 2, 28), new(2028, 2, 29)], result.Select(x => x.Date));
    }

    [Fact]
    public void IntervalCountAndInclusiveStartAreRespected()
    {
        var result = FinancialRecurrenceCalendar.CalculateOccurrences(Recurrence(RecurrenceFrequency.Monthly, new(2026, 1, 10), 10, 2, count: 3), new(2026, 1, 10), new(2026, 12, 31));
        Assert.Equal([new(2026, 1, 10), new(2026, 3, 10), new(2026, 5, 10)], result.Select(x => x.Date));
    }

    [Fact]
    public void EndDateIsInclusive()
    {
        var result = FinancialRecurrenceCalendar.CalculateOccurrences(Recurrence(RecurrenceFrequency.Weekly, new(2026, 1, 1), interval: 1, end: new(2026, 1, 15)), new(2026, 1, 1), new(2026, 1, 15));
        Assert.Equal(3, result.Count);
        Assert.Equal(new(2026, 1, 15), result[^1].Date);
    }

    [Fact]
    public void PausedRecurrenceProducesNoOccurrences()
    {
        var recurrence = Recurrence(RecurrenceFrequency.Monthly, new(2026, 1, 1));
        recurrence.Pause(DateTimeOffset.UnixEpoch);
        Assert.Empty(FinancialRecurrenceCalendar.CalculateOccurrences(recurrence, new(2026, 1, 1), new(2026, 3, 1)));
    }
}

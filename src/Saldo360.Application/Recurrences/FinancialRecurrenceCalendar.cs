using Saldo360.Domain.Recurrences;

namespace Saldo360.Application.Recurrences;

public static class FinancialRecurrenceCalendar
{
    public const int MaxDays = 3660;
    public static IReadOnlyList<RecurrenceOccurrenceDto> CalculateOccurrences(FinancialRecurrence recurrence, DateOnly from, DateOnly through)
    {
        if (from > through) throw new ArgumentException("O período é inválido.");
        if (through.DayNumber - from.DayNumber > MaxDays) throw new ArgumentException($"O horizonte máximo é de {MaxDays} dias.");
        if (recurrence.Status is RecurrenceStatus.Paused or RecurrenceStatus.Ended or RecurrenceStatus.Archived) return [];
        var result = new List<RecurrenceOccurrenceDto>();
        var sequence = 0;
        for (var index = 0; index < MaxDays + 1; index++)
        {
            var date = DateAt(recurrence, index);
            if (date > through || recurrence.EndDate is { } end && date > end || recurrence.OccurrenceCount is { } count && index >= count) break;
            sequence = index + 1;
            if (date >= from) result.Add(new(recurrence.Id, sequence, date, recurrence.Description, recurrence.Type, recurrence.Amount, recurrence.IsEstimated, recurrence.FinancialAccountId, recurrence.FinancialCategoryId, recurrence.FinancialCreditCardId));
        }
        return result;
    }

    private static DateOnly DateAt(FinancialRecurrence r, int index) => r.Frequency switch
    {
        RecurrenceFrequency.Weekly => r.StartDate.AddDays(index * 7 * r.Interval),
        RecurrenceFrequency.Monthly => AddMonthsAnchored(r.StartDate, index * r.Interval, r.DayOfMonth),
        RecurrenceFrequency.Yearly => AddYearsAnchored(r.StartDate, index * r.Interval, r.DayOfMonth),
        _ => throw new ArgumentOutOfRangeException(),
    };
    private static DateOnly AddMonthsAnchored(DateOnly start, int months, int day) { var m = start.AddMonths(months); return new(m.Year, m.Month, Math.Min(day, DateTime.DaysInMonth(m.Year, m.Month))); }
    private static DateOnly AddYearsAnchored(DateOnly start, int years, int day) { var y = start.AddYears(years); return new(y.Year, start.Month, Math.Min(day, DateTime.DaysInMonth(y.Year, start.Month))); }
}

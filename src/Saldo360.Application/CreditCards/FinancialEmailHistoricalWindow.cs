using System.Globalization;

namespace Saldo360.Application.CreditCards;

public sealed record FinancialEmailHistoricalWindow
{
    private FinancialEmailHistoricalWindow() { }
    public DateOnly From { get; private init; }
    public DateOnly Through { get; private init; }
    public DateTimeOffset StartUtc { get; private init; }
    public DateTimeOffset EndExclusiveUtc { get; private init; }
    public int MaxMessages { get; private init; }
    public const string TimeZone = "America/Sao_Paulo";

    public static FinancialEmailHistoricalWindow Create(string? from, string? through, int maxMessages = 50)
    {
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(through, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || end < start || end.DayNumber - start.DayNumber >= 366 || end == DateOnly.MaxValue || maxMessages is < 1 or > 500)
            throw new ArgumentException("Informe from/through yyyy-MM-dd, período inclusivo de até 366 dias e maxMessages entre 1 e 500.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        return new() { From = start, Through = end, MaxMessages = maxMessages,
            StartUtc = new(TimeZoneInfo.ConvertTimeToUtc(start.ToDateTime(TimeOnly.MinValue), zone)),
            EndExclusiveUtc = new(TimeZoneInfo.ConvertTimeToUtc(end.AddDays(1).ToDateTime(TimeOnly.MinValue), zone)) };
    }

    // Gmail after is exclusive. One-second overlap plus the exact local filter preserves midnight.
    public string BoundQuery(string profileQuery) => $"({profileQuery}) before:{EndExclusiveUtc.ToUnixTimeSeconds()}";
    public FinancialEmailCollectorResult Annotate(FinancialEmailCollectorResult result) => result with
    { Mode = "Historical", WindowFrom = From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), WindowThrough = Through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), WindowTimeZone = TimeZone, MessageLimit = MaxMessages };
}

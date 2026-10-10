namespace Saldo360.Domain.Common;

/// <summary>Calendário de negócio: datas (sem horário) são dias civis em America/Sao_Paulo, não em UTC.</summary>
public static class BusinessCalendar
{
    public const string TimeZoneId = "America/Sao_Paulo";

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>Dia civil em São Paulo no instante informado (ex.: 01:00 UTC de 11/10 ainda é 10/10).</summary>
    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);
}

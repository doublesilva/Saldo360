using Saldo360.Domain.Common;

namespace Saldo360.Application.MonthlyPlan;

// PROJ-01: previsão de receita PJ por horas. Puro (sem banco, rede ou relógio). A estimativa só existe na projeção quando a configuração está ATIVA (aprovada),
// nunca vira lançamento realizado e é substituída pela receita realizada da conta no mês do recebimento.

public sealed record IncomeForecastSettings(
    Guid AccountId, decimal HourlyRate, decimal HoursPerDay, int PaymentDay, int PaymentOffsetMonths, decimal TaxReservePercent, decimal OperationalReserve, bool IsActive);

public sealed record IncomeForecastMonthOverride(DateOnly Competence, int? BillableDays, decimal? ExpectedAmount, DateOnly? PaymentDate, bool IsDismissed);

public static class IncomeForecastStatus
{
    /// <summary>Estimativa parametrizada e ativada, ainda sem receita realizada correspondente.</summary>
    public const string Forecast = "Forecast";
    /// <summary>A receita realizada da conta no mês do recebimento substitui a estimativa.</summary>
    public const string Realized = "Realized";
    public const string Dismissed = "Dismissed";
}

/// <summary>Uma competência trabalhada e o seu recebimento. <c>TaxReserve</c> é só reserva de disponibilidade: nunca é débito no saldo bancário.</summary>
public sealed record IncomeForecastOccurrence(
    DateOnly WorkedMonth, DateOnly PaymentDate, int BusinessDays, int BillableDays, decimal HourlyRate, decimal HoursPerDay, decimal GrossAmount, decimal TaxReserve,
    string Status, decimal RealizedAmount, bool DaysAdjusted, bool AmountAdjusted, bool DateAdjusted)
{
    /// <summary>Só a estimativa ainda não realizada projeta entrada de caixa.</summary>
    public bool ProjectsCash => Status == IncomeForecastStatus.Forecast;
}

/// <summary>Configuração e ocorrências do período, mais os meses em que já existe obrigação tributária cadastrada (a reserva não se aplica de novo).</summary>
public sealed record IncomeForecastView(IncomeForecastSettings? Settings, IReadOnlyList<IncomeForecastOccurrence> Occurrences, IReadOnlySet<DateOnly> TaxObligationMonths, bool Inferred = false, int InferredMonths = 0)
{
    public static IncomeForecastView None { get; } = new(null, [], new HashSet<DateOnly>());
    public bool Effective => Settings is { IsActive: true };
}

/// <summary>
/// Feriados do calendário de trabalho de São Paulo/SP: nacionais com data legal fixa ou móvel (Sexta-feira Santa), 9 de julho (estadual) e 25 de janeiro (aniversário da
/// cidade). Carnaval e Corpus Christi são ponto facultativo e ficam para o ajuste manual de dias faturáveis, assim como qualquer dia efetivamente trabalhado num feriado.
/// </summary>
public static class SaoPauloHolidays
{
    public static IReadOnlyDictionary<DateOnly, string> For(int year)
    {
        var easter = Easter(year);
        return new Dictionary<DateOnly, string>
        {
            [new(year, 1, 1)] = "Confraternização Universal", [new(year, 1, 25)] = "Aniversário da cidade de São Paulo", [easter.AddDays(-2)] = "Sexta-feira Santa",
            [new(year, 4, 21)] = "Tiradentes", [new(year, 5, 1)] = "Dia do Trabalho", [new(year, 7, 9)] = "Revolução Constitucionalista", [new(year, 9, 7)] = "Independência",
            [new(year, 10, 12)] = "Nossa Senhora Aparecida", [new(year, 11, 2)] = "Finados", [new(year, 11, 15)] = "Proclamação da República",
            [new(year, 11, 20)] = "Consciência Negra", [new(year, 12, 25)] = "Natal",
        };
    }

    public static IReadOnlySet<DateOnly> Dates(DateOnly from, DateOnly through) =>
        Enumerable.Range(from.Year, through.Year - from.Year + 1).SelectMany(year => For(year).Keys).Where(d => d >= from && d <= through).ToHashSet();

    /// <summary>Domingo de Páscoa (algoritmo gregoriano anônimo).</summary>
    private static DateOnly Easter(int year)
    {
        var a = year % 19; var b = year / 100; var c = year % 100; var d = b / 4; var e = b % 4; var f = (b + 8) / 25; var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30; var i = c / 4; var k = c % 4; var l = (32 + 2 * e + 2 * i - h - k) % 7; var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31; var day = (h + l - 7 * m + 114) % 31 + 1;
        return new(year, month, day);
    }
}

public static class IncomeForecastCalculator
{
    /// <summary>A receita realizada que cobre ao menos esta fração do valor esperado, no mês do recebimento, substitui a estimativa (nunca as duas).</summary>
    public const decimal RealizedReplacementRatio = 0.5m;

    public static DateOnly PaymentDateFor(IncomeForecastSettings settings, DateOnly workedMonth)
    {
        var month = workedMonth.AddMonths(settings.PaymentOffsetMonths);
        return new(month.Year, month.Month, Math.Min(settings.PaymentDay, DateTime.DaysInMonth(month.Year, month.Month)));
    }

    /// <summary>Dias úteis do calendário de São Paulo, somados aos feriados e dias não trabalhados cadastrados pelo usuário.</summary>
    public static int BusinessDays(DateOnly month, IEnumerable<DateOnly> userNonWorking)
    {
        var last = month.AddMonths(1).AddDays(-1);
        var nonWorking = SaoPauloHolidays.Dates(month, last).Concat(userNonWorking).ToHashSet();
        return MonthlyPlanCalculator.CountDays(month, nonWorking).BusinessDays;
    }

    public static decimal TaxReserve(decimal baseAmount, decimal percent) => decimal.Round(baseAmount * percent / 100m, Money.MaxScale, MidpointRounding.AwayFromZero);

    public static IncomeForecastOccurrence Occurrence(IncomeForecastSettings settings, DateOnly workedMonth, int businessDays, IncomeForecastMonthOverride? adjust, decimal realizedInPaymentMonth)
    {
        var days = adjust?.BillableDays ?? businessDays;
        var gross = adjust?.ExpectedAmount ?? MonthlyPlanCalculator.HourlyAmount(settings.HourlyRate, settings.HoursPerDay, days);
        var payment = adjust?.PaymentDate ?? PaymentDateFor(settings, workedMonth);
        var status = adjust?.IsDismissed == true ? IncomeForecastStatus.Dismissed
            : gross > 0m && realizedInPaymentMonth >= gross * RealizedReplacementRatio ? IncomeForecastStatus.Realized : IncomeForecastStatus.Forecast;
        var reserveBase = status switch { IncomeForecastStatus.Realized => realizedInPaymentMonth, IncomeForecastStatus.Forecast => gross, _ => 0m };
        return new(workedMonth, payment, businessDays, days, settings.HourlyRate, settings.HoursPerDay, gross, TaxReserve(reserveBase, settings.TaxReservePercent), status, realizedInPaymentMonth,
            adjust?.BillableDays is not null, adjust?.ExpectedAmount is not null, adjust?.PaymentDate is not null);
    }

    /// <summary>Competências trabalhadas que podem ter recebimento no intervalo (com folga de um mês para datas ajustadas à mão).</summary>
    public static IEnumerable<DateOnly> WorkedMonthsFor(IncomeForecastSettings settings, DateOnly from, DateOnly through)
    {
        var first = new DateOnly(from.Year, from.Month, 1).AddMonths(-settings.PaymentOffsetMonths - 1);
        var last = new DateOnly(through.Year, through.Month, 1).AddMonths(-settings.PaymentOffsetMonths + 1);
        for (var month = first; month <= last; month = month.AddMonths(1)) yield return month;
    }
}

/// <summary>
/// Disponibilidade da PJ para repasse. O saldo bancário projetado da conta JÁ está líquido das despesas pendentes e das transferências previstas; por isso a fórmula
/// <c>Saldo antes das despesas − Despesas PJ pendentes − Tributos − Reserva operacional</c> equivale a <c>Saldo projetado − Tributos − Reserva operacional</c>: as despesas
/// não são subtraídas duas vezes. Tributos e reserva reduzem a disponibilidade, nunca o saldo bancário.
/// </summary>
public static class PjAvailability
{
    public static decimal Available(decimal projectedClosing, decimal taxReserve, decimal operationalReserve) => projectedClosing - taxReserve - operationalReserve;

    /// <summary>Sugestão de repasse: nunca negativa e nunca acima de centavos inteiros disponíveis. Não cria transferência: a confirmação é do usuário.</summary>
    public static decimal Suggested(decimal available) => available > 0m ? decimal.Round(available, Money.MaxScale, MidpointRounding.ToZero) : 0m;
}

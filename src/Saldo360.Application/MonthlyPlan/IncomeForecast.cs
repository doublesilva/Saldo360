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
    /// <summary>Recebimento vinculado cobre integralmente o valor esperado.</summary>
    public const string Realized = "Realized";
    /// <summary>Há recebimento vinculado, mas menor que o esperado: o residual segue a receber.</summary>
    public const string PartiallyReceived = "PartiallyReceived";
    public const string Dismissed = "Dismissed";
}

/// <summary>Entrada bancária operacional da conta de recebimento.</summary>
public sealed record IncomeReceipt(DateOnly Date, decimal Amount, string Description);

/// <summary>Recebimentos do mês de pagamento separados pela evidência: <c>Linked</c> tem origem reconhecida (pagador já visto em meses anteriores); <c>Unidentified</c> não prova pertencer à competência.</summary>
public sealed record IncomeReceiptEvidence(decimal Linked, decimal Unidentified)
{
    public static IncomeReceiptEvidence None { get; } = new(0m, 0m);
}

/// <summary>Uma competência trabalhada e o seu recebimento. <c>TaxReserve</c> é só reserva de disponibilidade: nunca é débito no saldo bancário.</summary>
public sealed record IncomeForecastOccurrence(
    DateOnly WorkedMonth, DateOnly PaymentDate, int BusinessDays, int BillableDays, decimal HourlyRate, decimal HoursPerDay, decimal GrossAmount, decimal TaxReserve,
    string Status, decimal RealizedAmount, bool DaysAdjusted, bool AmountAdjusted, bool DateAdjusted, decimal Residual = 0m, decimal UnidentifiedAmount = 0m, bool Overdue = false, bool Ambiguous = false)
{
    /// <summary>Só o residual a receber, com data ainda não vencida, projeta entrada de caixa. Residual atrasado fica identificado, mas não vira entrada hoje.</summary>
    public bool ProjectsCash => Status is IncomeForecastStatus.Forecast or IncomeForecastStatus.PartiallyReceived && !Overdue && Residual > 0m;

    /// <summary>Valor que entra no caixa projetado na data do recebimento.</summary>
    public decimal CashAmount => ProjectsCash ? Residual : 0m;

    /// <summary>Recebimento vinculado acima do esperado: o valor efetivo é preservado e a divergência fica visível.</summary>
    public decimal Excess => Status == IncomeForecastStatus.Dismissed ? 0m : Math.Max(0m, RealizedAmount - GrossAmount);
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

    /// <summary>
    /// A receita só é liquidada pelo que tem evidência (<see cref="IncomeReceiptEvidence.Linked"/>): sem percentual de substituição. Vinculado parcial deixa o residual a receber;
    /// vinculado acima do esperado preserva o valor efetivo. Residual com data anterior a <paramref name="today"/> continua identificado, mas não projeta entrada de caixa (nenhuma data nova é inventada).
    /// A reserva de tributos incide uma única vez sobre o maior entre o esperado e o recebido.
    /// </summary>
    public static IncomeForecastOccurrence Occurrence(IncomeForecastSettings settings, DateOnly workedMonth, int businessDays, IncomeForecastMonthOverride? adjust, IncomeReceiptEvidence evidence, DateOnly today)
    {
        var days = adjust?.BillableDays ?? businessDays;
        var gross = adjust?.ExpectedAmount ?? MonthlyPlanCalculator.HourlyAmount(settings.HourlyRate, settings.HoursPerDay, days);
        var payment = adjust?.PaymentDate ?? PaymentDateFor(settings, workedMonth);
        var dismissed = adjust?.IsDismissed == true;
        var status = dismissed ? IncomeForecastStatus.Dismissed
            : gross > 0m && evidence.Linked >= gross ? IncomeForecastStatus.Realized
            : evidence.Linked > 0m ? IncomeForecastStatus.PartiallyReceived : IncomeForecastStatus.Forecast;
        var residual = dismissed ? 0m : Math.Max(0m, gross - evidence.Linked);
        var reserveBase = dismissed ? 0m : Math.Max(gross, evidence.Linked);
        return new(workedMonth, payment, businessDays, days, settings.HourlyRate, settings.HoursPerDay, gross, TaxReserve(reserveBase, settings.TaxReservePercent), status, evidence.Linked,
            adjust?.BillableDays is not null, adjust?.ExpectedAmount is not null, adjust?.PaymentDate is not null, residual, dismissed ? 0m : evidence.Unidentified, residual > 0m && payment < today);
    }

    /// <summary>
    /// Separa as entradas do mês de pagamento pela evidência: operacionais (não rendimento/estorno) cujo pagador (descrição normalizada) já apareceu em meses anteriores da conta são vinculadas;
    /// as demais ficam como não identificadas. Sem histórico do pagador, nada é vinculado.
    /// </summary>
    public static IncomeReceiptEvidence Classify(IEnumerable<IncomeReceipt> paymentMonthReceipts, IEnumerable<IncomeReceipt> priorReceipts)
    {
        var known = priorReceipts.Where(r => IncomeInference.IsOperational(r.Description)).Select(r => Normalize(r.Description)).ToHashSet();
        decimal linked = 0m, unidentified = 0m;
        foreach (var receipt in paymentMonthReceipts.Where(r => r.Amount > 0m && IncomeInference.IsOperational(r.Description)))
            if (known.Contains(Normalize(receipt.Description))) linked += receipt.Amount; else unidentified += receipt.Amount;
        return new(linked, unidentified);
    }

    private static string Normalize(string description) => string.Join(' ', description.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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

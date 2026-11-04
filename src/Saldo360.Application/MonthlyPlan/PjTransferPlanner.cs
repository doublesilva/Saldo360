using Saldo360.Domain.Common;

namespace Saldo360.Application.MonthlyPlan;

// PROJ-06: receita PJ inferida do histórico e repasse PJ → PF projetado. Puro (sem banco, rede ou relógio). Nada aqui cria lançamento, previsão ou transferência real:
// são projeções de planejamento, recalculadas a cada leitura.

public sealed record OperationalReceipt(DateOnly Date, decimal Amount, string Description);

/// <summary>Faturamento mensal típico (mediana) e dia típico de recebimento, inferidos de meses com recebimento operacional.</summary>
public sealed record InferredIncome(decimal MonthlyAmount, int PaymentDay, int MonthsUsed);

public static class IncomeInference
{
    public const int WindowMonths = 6;
    public const int MinimumMonths = 3;

    // Rendimentos de aplicação, resgates, estornos, devoluções, juros e cashback não são faturamento profissional. Transferências próprias são excluídas pelo chamador
    // (evidência de saída espelhada em outra conta do próprio orçamento).
    private static readonly string[] NonOperational = ["REND", "APLIC", "RESGATE", "ESTORNO", "EST DEBITO", "EST CREDITO", "DEVOL", "JUROS", "CASHBACK", "REEMBOLSO", "IOF"];

    public static bool IsOperational(string description)
    {
        var text = description.ToUpperInvariant();
        return !NonOperational.Any(word => text.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// Mediana, nos últimos <see cref="WindowMonths"/> meses COMPLETOS anteriores a <paramref name="currentMonth"/>, do faturamento operacional mensal, exigindo ao menos
    /// <see cref="MinimumMonths"/> meses válidos (sem isso, dado insuficiente: nada é inferido). O dia de recebimento é a mediana do dia do maior recebimento de cada mês.
    /// </summary>
    public static InferredIncome? Infer(IEnumerable<OperationalReceipt> receipts, DateOnly currentMonth)
    {
        var start = currentMonth.AddMonths(-WindowMonths); var end = currentMonth.AddDays(-1);
        var months = receipts.Where(r => r.Amount > 0m && r.Date >= start && r.Date <= end && IsOperational(r.Description))
            .GroupBy(r => new DateOnly(r.Date.Year, r.Date.Month, 1)).ToList();
        if (months.Count < MinimumMonths) return null;
        var amount = Median(months.Select(g => g.Sum(r => r.Amount)).ToList());
        var day = (int)Median(months.Select(g => (decimal)g.OrderByDescending(r => r.Amount).ThenBy(r => r.Date).First().Date.Day).ToList());
        return new(decimal.Round(amount, Money.MaxScale, MidpointRounding.ToZero), Math.Clamp(day, 1, 31), months.Count);
    }

    private static decimal Median(IReadOnlyList<decimal> values)
    {
        var ordered = values.OrderBy(v => v).ToList();
        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2m;
    }
}

/// <summary>Repasse projetado: data, valor e se depende de receita inferida (nunca apresentada como garantida).</summary>
public sealed record ProjectedPjTransfer(DateOnly Date, decimal Amount, bool DependsOnInferredIncome);

/// <summary>
/// Repasse PJ → PF projetado. Em cada data de recebimento previsto (e hoje), transfere 100% do que a PJ pode ceder: o MENOR saldo projetado da PJ daqui até o fim do horizonte
/// (o saldo já reflete recebimentos, despesas pendentes, repasses previstos e os repasses projetados anteriores, então nada é subtraído duas vezes e os compromissos até o
/// próximo recebimento e depois dele estão cobertos) menos a reserva de tributos (do mês e do anterior, quando não há obrigação tributária cadastrada) e a reserva operacional.
/// Nunca consome reserva, nunca deixa a PJ negativa, nunca é negativo e usa centavos inteiros (truncados). Sem saldo conhecido num dia, não projeta.
/// </summary>
public static class PjTransferPlanner
{
    public static IReadOnlyList<ProjectedPjTransfer> Plan(DateOnly today, DateOnly through, IReadOnlyDictionary<DateOnly, decimal> pjClosingByDate,
        IReadOnlyList<IncomeForecastOccurrence> occurrences, decimal operationalReserve, IReadOnlySet<DateOnly> taxObligationMonths, bool incomeInferred)
    {
        var triggers = new SortedSet<DateOnly> { today };
        foreach (var o in occurrences.Where(o => o.ProjectsCash && o.GrossAmount > 0m && o.PaymentDate > today && o.PaymentDate <= through)) triggers.Add(o.PaymentDate);

        var result = new List<ProjectedPjTransfer>();
        var cumulative = 0m;
        foreach (var date in triggers)
        {
            decimal? minimum = null;
            for (var day = date; day <= through; day = day.AddDays(1))
            {
                if (!pjClosingByDate.TryGetValue(day, out var closing)) { minimum = null; break; }
                minimum = Math.Min(minimum ?? decimal.MaxValue, closing - cumulative);
            }
            if (minimum is not { } floor) continue;
            var transferable = decimal.Round(floor - TaxReserveAt(occurrences, taxObligationMonths, date) - operationalReserve, Money.MaxScale, MidpointRounding.ToZero);
            if (transferable <= 0m) continue;
            result.Add(new(date, transferable, incomeInferred && occurrences.Any(o => o.ProjectsCash && o.GrossAmount > 0m && o.PaymentDate <= date)));
            cumulative += transferable;
        }
        return result;
    }

    private static decimal TaxReserveAt(IReadOnlyList<IncomeForecastOccurrence> occurrences, IReadOnlySet<DateOnly> taxObligationMonths, DateOnly date)
    {
        var month = new DateOnly(date.Year, date.Month, 1); var previous = month.AddMonths(-1);
        return occurrences.Where(o => o.Status != IncomeForecastStatus.Dismissed && o.PaymentDate <= date)
            .Where(o => { var m = new DateOnly(o.PaymentDate.Year, o.PaymentDate.Month, 1); return (m == month || m == previous) && !taxObligationMonths.Contains(m); })
            .Sum(o => o.TaxReserve);
    }
}

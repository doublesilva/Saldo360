using Saldo360.Domain.Common;

namespace Saldo360.Application.MonthlyPlan;

// Entrega 2: regras puras dos cenários da projeção consolidada. Nenhum acesso a banco, rede ou relógio; nada é gravado.

/// <summary>Fatos de um mês da conta PF, já calculados pela Agenda (cenário Atual), que alimentam o cenário Tetos.</summary>
public sealed record ScenarioMonthFacts(
    DateOnly Month, decimal? Opening, decimal? Closing, decimal CardProjected, decimal OutsideKnown, decimal? CardTarget, decimal? TotalTarget, DateOnly? CardPaymentMonth,
    decimal FoodComplement = 0m);

/// <summary>Resultado do cenário Tetos de um mês: complementos, excessos e o saldo acumulado após o efeito de caixa dos complementos.</summary>
public sealed record CeilingMonth(
    DateOnly Month, decimal? Opening, decimal? Closing, decimal? OutsideTarget, decimal OutsideComplement, decimal OutsideExcess, decimal CardComplement, decimal CardExcess,
    decimal CashComplementInMonth, bool TargetsDefined, decimal? CurrentOpening = null, decimal? CurrentClosing = null);

/// <summary>Uso do limite de cheque especial (T10.4): <c>usado = max(0, −saldo)</c>. Recuperação é a redução do uso e pode ser negativa; o saldo positivo é mostrado à parte.</summary>
public sealed record OverdraftView(decimal UsedStart, decimal UsedEnd, decimal? Available, decimal MonthlyRecovery, decimal CumulativeRecovery, decimal PositiveBalance);

public static class ProjectionScenarios
{
    /// <summary><c>complemento = max(0, teto − consumo)</c>; consumo acima do teto é preservado e vira excesso (nunca reduzido).</summary>
    public static (decimal Complement, decimal Excess) Complement(decimal target, decimal projected) =>
        (Math.Max(0m, target - projected), Math.Max(0m, projected - target));

    /// <summary>Provisão de alimentação já dentro dos cartões: só a diferença para a provisão é estimada, nunca a provisão inteira de novo.</summary>
    public static decimal FoodComplement(decimal provision, decimal known) => Math.Max(0m, provision - known);

    /// <summary>
    /// Cenário Tetos. Parte do saldo do cenário Atual e subtrai, de forma acumulada, o efeito de caixa dos complementos: fora dos cartões no próprio mês; cartões na competência do
    /// pagamento da fatura (compra no cartão reduz o orçamento na competência da fatura, mas o saldo só cai no pagamento). Meta fora dos cartões = total − cartões. Sem metas definidas,
    /// nada é complementado e o mês é sinalizado (<c>TargetsDefined</c> falso), nunca tratado como meta zero.
    /// </summary>
    public static IReadOnlyList<CeilingMonth> Ceilings(IReadOnlyList<ScenarioMonthFacts> months)
    {
        var cash = months.ToDictionary(m => m.Month, _ => 0m); var foodCash = months.ToDictionary(m => m.Month, _ => 0m);
        var computed = new List<(ScenarioMonthFacts Facts, decimal? OutsideTarget, decimal OutsideComplement, decimal OutsideExcess, decimal CardComplement, decimal CardExcess, bool Defined)>();
        foreach (var m in months)
        {
            var defined = m.CardTarget is not null && m.TotalTarget is not null;
            decimal? outsideTarget = defined ? Math.Max(0m, m.TotalTarget!.Value - m.CardTarget!.Value) : null;
            var (outsideComplement, outsideExcess) = outsideTarget is { } ot ? Complement(ot, m.OutsideKnown) : (0m, 0m);
            var (cardComplement, cardExcess) = m.CardTarget is { } ct ? Complement(ct, m.CardProjected + m.FoodComplement) : (0m, 0m); // a provisão de alimentação já está dentro do teto dos cartões
            computed.Add((m, outsideTarget, outsideComplement, outsideExcess, cardComplement, cardExcess, defined));
            if (cash.ContainsKey(m.Month)) cash[m.Month] += outsideComplement;
            var payment = m.CardPaymentMonth ?? m.Month.AddMonths(1);
            if (foodCash.ContainsKey(payment)) foodCash[payment] += m.FoodComplement;
            if (cash.ContainsKey(payment)) cash[payment] += cardComplement; // pagamento fora da janela: o efeito de caixa está além do horizonte
        }

        var result = new List<CeilingMonth>(); var cumulative = 0m; var cumulativeFood = 0m; decimal? previousClosing = null; decimal? previousCurrent = null; var chainBroken = false;
        foreach (var c in computed)
        {
            cumulativeFood += foodCash[c.Facts.Month]; cumulative += cash[c.Facts.Month] + foodCash[c.Facts.Month];
            // O saldo acumulado do Tetos é o do Atual menos o caixa acumulado dos complementos; saldo desconhecido continua desconhecido (nunca zero).
            decimal? closing = c.Facts.Closing is { } atual && !chainBroken ? atual - cumulative : null;
            if (c.Facts.Closing is null) chainBroken = true;
            decimal? opening = previousClosing ?? c.Facts.Opening;
            decimal? current = c.Facts.Closing is { } baseline && !(chainBroken && previousCurrent is null) ? baseline - cumulativeFood : null;
            var currentOpening = previousCurrent ?? c.Facts.Opening;
            result.Add(new(c.Facts.Month, opening, closing, c.OutsideTarget, c.OutsideComplement, c.OutsideExcess, c.CardComplement, c.CardExcess, cash[c.Facts.Month], c.Defined, currentOpening, current));
            previousClosing = closing; previousCurrent = current;
        }
        return result;
    }

    /// <summary>Uso e recuperação do limite. <paramref name="referenceOpening"/> é a abertura do primeiro mês consultado (posição de referência). Sem saldo, nada é calculado.</summary>
    public static OverdraftView? Overdraft(decimal? opening, decimal? closing, decimal? referenceOpening, decimal? limit)
    {
        if (opening is not { } start || closing is not { } end || referenceOpening is not { } reference) return null;
        var usedStart = Used(start); var usedEnd = Used(end);
        return new(usedStart, usedEnd, limit is { } l ? Math.Max(0m, l - usedEnd) : null, usedStart - usedEnd, Used(reference) - usedEnd, Math.Max(0m, end));
    }

    /// <summary>Saldo positivo que excede a recuperação: de −3.000 para +2.000 recupera 3.000 e deixa 2.000 positivos (não 5.000 de recuperação).</summary>
    public static decimal Used(decimal balance) => Math.Max(0m, -balance);

}

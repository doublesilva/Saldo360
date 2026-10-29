using System.Globalization;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Dashboard.PlanActual;

/// <summary>Observação de saldo do banco (natureza "saldo contábil") usada para reconstruir a utilização do cheque especial.</summary>
public sealed record OverdraftObservation(DateOnly AsOf, decimal Balance, DateTimeOffset ObservedAt)
{
    /// <summary>Utilização derivada do saldo observado: saldo negativo é cheque especial usado; saldo positivo, nenhum.</summary>
    public decimal Used => Math.Max(0m, -Balance);
}

public sealed record OverdraftInput(
    Guid AccountId, string Name, decimal? ContractedLimit, decimal? UsedLatest, decimal? LatestBalance, DateTimeOffset? ObservedAt, DateOnly? ObservedAsOf,
    IReadOnlyList<OverdraftObservation> History, DateOnly MonthStart, DateOnly Today,
    decimal? LedgerProjectedClosing, decimal? Difference);

public sealed record OverdraftResult(
    OverdraftAccountDto Dto, decimal? UsedStart, decimal UsedLatest, decimal? Effective, decimal? ProjectedEnd, decimal? ProjectedRecovery, decimal? ProjectedFromNow);

/// <summary>Fatias de consumo de um cartão numa competência (mês da fatura, a mesma convenção dos orçamentos e da projeção T09.4).</summary>
public sealed record CardSlice(Guid CardId, string Name, string? Scope, Guid? PayerAccountId, decimal Confirmed, decimal PendingKnown, decimal Estimated, decimal Charges)
{
    public decimal Projected => Confirmed + PendingKnown + Estimated;
}

/// <summary>
/// Fórmulas do Dashboard Planejado × Projetado × Realizado (T10.1). Tudo é puro (sem banco, rede ou relógio). Os totais de caixa, saldos, obrigações
/// de cartão e conciliação já vêm dos serviços existentes; aqui só se comparam e se combinam esses números.
/// </summary>
public static class PlanActualCalculator
{
    /// <summary>Diferença máxima, em dias, entre a data da observação de saldo e o limite do período para a observação servir de início ou de fim.</summary>
    public const int BaselineToleranceDays = 3;

    public static class Reasons
    {
        public const string MovementsInReview = "MovementsInReview";
        public const string PartialPeriod = "PartialPeriod";
        public const string CardCoverageIncomplete = "CardCoverageIncomplete";
        public const string DebtCoverageIncomplete = "DebtCoverageIncomplete";
        public const string PlanNotDefined = "PlanNotDefined";
        public const string PlanNotAttributable = "PlanNotAttributable";
        public const string PartialPlan = "PartialPlan";
        public const string InsufficientHistory = "InsufficientHistory";
        public const string NoBankObservation = "NoBankObservation";
        public const string NotDerivableFromBalance = "OverdraftNotDerivableFromBalance";
        public const string ProjectionUnavailable = "ProjectionUnavailable";
        public const string ProjectionAdjustedByDifference = "ProjectionAdjustedByDifference";
        public const string FuturePeriod = "FuturePeriod";
        public const string NoCards = "NoCards";
        public const string MissingPayerAccount = "MissingPayerAccount";
    }

    public static string Cents(decimal value) => Money.Format(decimal.Round(value, Money.MaxScale, MidpointRounding.AwayFromZero));
    public static string? Cents(decimal? value) => value is { } v ? Cents(v) : null;
    public static string Percent(decimal value) => decimal.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);

    public static string PeriodKind(DateOnly month, DateOnly today) =>
        month.Year == today.Year && month.Month == today.Month ? "Current" : month < today ? "Past" : "Future";

    /// <summary>
    /// Monta um indicador. <paramref name="planned"/> nulo significa "não definido" (nunca zero). Diferenças são realizado/projetado − planejado
    /// (positivo = acima do planejado); o percentual de execução é realizado ÷ planejado.
    /// </summary>
    public static IndicatorDto Indicator(decimal? planned, string? plannedSource, decimal realized, decimal pending, IReadOnlyList<ComponentDto> components, IReadOnlyList<string> reasons)
    {
        var projected = realized + pending;
        var defined = planned is not null;
        var percentBase = planned is > 0m ? planned : null;
        var caveats = reasons.Where(r => r is not (Reasons.PlanNotDefined or Reasons.PlanNotAttributable)).ToList();
        return new(caveats.Count == 0 ? PlanActualStates.Complete : PlanActualStates.Partial, defined ? PlanActualStates.Defined : PlanActualStates.NotDefined,
            Cents(planned), Cents(projected), Cents(realized), Cents(pending),
            defined ? Cents(realized - planned!.Value) : null, defined ? Cents(projected - planned!.Value) : null,
            percentBase is { } b ? Percent(realized * 100m / b) : null, percentBase is { } p ? Percent(projected * 100m / p) : null,
            defined ? plannedSource : null, components, reasons);
    }

    // ---- Cheque especial -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Observação que serve de início do período: a última até o dia anterior ao início (a diferença não passa de <see cref="BaselineToleranceDays"/>)
    /// ou, na falta dela, a primeira dentro dos primeiros dias do período. Aproximada quando não é a do dia anterior. Nada é inventado: sem observação
    /// comparável o resultado é nulo.
    /// </summary>
    public static (OverdraftObservation? Observation, bool Approximate) PickStart(IReadOnlyList<OverdraftObservation> history, DateOnly monthStart)
    {
        var before = history.Where(o => o.AsOf < monthStart).OrderByDescending(o => o.AsOf).ThenByDescending(o => o.ObservedAt).FirstOrDefault();
        if (before is not null && monthStart.DayNumber - before.AsOf.DayNumber <= BaselineToleranceDays)
            return (before, before.AsOf != monthStart.AddDays(-1));
        var after = history.Where(o => o.AsOf >= monthStart && o.AsOf < monthStart.AddDays(BaselineToleranceDays)).OrderBy(o => o.AsOf).ThenBy(o => o.ObservedAt).FirstOrDefault();
        return after is null ? (null, false) : (after, true);
    }

    /// <summary>Observação que serve de fim de um período já encerrado: a última até o último dia, sem ficar defasada além da tolerância.</summary>
    public static OverdraftObservation? PickEnd(IReadOnlyList<OverdraftObservation> history, DateOnly monthEnd)
    {
        var last = history.Where(o => o.AsOf <= monthEnd).OrderByDescending(o => o.AsOf).ThenByDescending(o => o.ObservedAt).FirstOrDefault();
        return last is not null && monthEnd.DayNumber - last.AsOf.DayNumber <= BaselineToleranceDays ? last : null;
    }

    public static OverdraftResult Overdraft(OverdraftInput input)
    {
        var reasons = new List<string>();
        var latestUsed = input.UsedLatest ?? 0m;
        var monthEnd = input.MonthStart.AddMonths(1).AddDays(-1);
        var kind = PeriodKind(input.MonthStart, input.Today);
        var hasObservation = input.UsedLatest is not null && input.ObservedAt is not null;
        if (!hasObservation) reasons.Add(Reasons.NoBankObservation);

        // O histórico só é comparável quando o saldo observado reproduz a utilização informada pelo banco (a utilização atual confere com o saldo negativo).
        var derivable = hasObservation && input.LatestBalance is { } balance && Math.Abs(latestUsed - Math.Max(0m, -balance)) <= 0.01m;
        if (hasObservation && !derivable) reasons.Add(Reasons.NotDerivableFromBalance);
        var history = derivable ? input.History : [];

        decimal? usedStart = null; DateOnly? startAsOf = null; var approximate = false; decimal? effective = null;
        if (kind != "Future" && derivable)
        {
            var (start, approx) = PickStart(history, input.MonthStart);
            if (start is not null)
            {
                usedStart = start.Used; startAsOf = start.AsOf; approximate = approx;
                // Mês corrente: a última observação; mês encerrado: a do fim do mês.
                // Início e fim precisam ser observações diferentes: uma única observação não compara nada com ela mesma.
                var end = kind == "Current" ? new OverdraftObservation(input.ObservedAsOf ?? input.Today, -latestUsed, input.ObservedAt!.Value) : PickEnd(history, monthEnd);
                if (end is not null && end.ObservedAt > start.ObservedAt) effective = start.Used - end.Used;
            }
        }
        if (kind == "Future") reasons.Add(Reasons.FuturePeriod);
        else if (effective is null && hasObservation) reasons.Add(Reasons.InsufficientHistory);

        // Projeção: o saldo projetado do Saldo360 no fim do mês, corrigido pela diferença atual entre o banco e o calculado (sem a diferença não há ponto de partida confiável).
        decimal? projectedBalance = null; decimal? projectedEnd = null; decimal? projectedRecovery = null; decimal? fromNow = null;
        if (kind != "Past" && hasObservation)
        {
            if (input.LedgerProjectedClosing is { } closing && input.Difference is { } difference)
            {
                projectedBalance = closing + difference;
                projectedEnd = Math.Max(0m, -projectedBalance.Value);
                fromNow = latestUsed - projectedEnd.Value;
                if (usedStart is { } start) projectedRecovery = start - projectedEnd.Value;
                if (Math.Abs(difference) > 0.01m) reasons.Add(Reasons.ProjectionAdjustedByDifference);
            }
            else reasons.Add(Reasons.ProjectionUnavailable);
        }

        var fully = hasObservation && latestUsed == 0m && (input.ContractedLimit ?? 0m) > 0m;
        var exceeds = projectedEnd is { } used && input.ContractedLimit is { } limit && limit > 0m && used > limit;
        string? direction = effective is null ? null : Math.Abs(effective.Value) < 0.005m ? "Stable" : effective > 0m ? "Recovered" : "Increased";
        var state = !hasObservation ? PlanActualStates.Unavailable
            : kind == "Future" ? (projectedEnd is null ? PlanActualStates.Unavailable : PlanActualStates.Partial)
            : effective is null ? "InsufficientHistory"
            : kind == "Current" && projectedEnd is null ? PlanActualStates.Partial : PlanActualStates.Complete;
        var dto = new OverdraftAccountDto(input.AccountId, input.Name, state, Cents(input.ContractedLimit), Cents(latestUsed), input.ObservedAt, input.ObservedAsOf,
            Cents(usedStart), startAsOf, approximate, Cents(effective), direction, fully, Cents(projectedEnd), Cents(projectedRecovery), Cents(fromNow), Cents(projectedBalance), exceeds, reasons);
        return new(dto, usedStart, latestUsed, effective, projectedEnd, projectedRecovery, fromNow);
    }

    /// <summary>Consolida apenas as contas elegíveis; com alguma conta sem cobertura, os totais são parciais e dizem isso.</summary>
    public static OverdraftDto ConsolidateOverdraft(IReadOnlyList<OverdraftResult> results, string periodKind)
    {
        if (results.Count == 0) return new(false, PlanActualStates.Unavailable, null, null, null, null, null, null, null, periodKind, [], []);
        var reasons = results.SelectMany(r => r.Dto.Reasons).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        var withEffective = results.Where(r => r.Effective is not null).ToList();
        var withProjection = results.Where(r => r.ProjectedEnd is not null).ToList();
        var withStart = results.Where(r => r.UsedStart is not null).ToList();
        decimal? SumOrNull(IEnumerable<OverdraftResult> items, Func<OverdraftResult, decimal?> pick) => items.Any() ? items.Sum(item => pick(item)!.Value) : null;
        var complete = results.All(r => r.Dto.State == PlanActualStates.Complete);
        var anyData = withEffective.Count > 0 || withProjection.Count > 0;
        var state = complete ? PlanActualStates.Complete : anyData ? PlanActualStates.Partial : results.All(r => r.Dto.State == PlanActualStates.Unavailable) ? PlanActualStates.Unavailable : "InsufficientHistory";
        var contracted = results.Where(r => r.Dto.ContractedLimit is not null).Sum(r => decimal.Parse(r.Dto.ContractedLimit!, CultureInfo.InvariantCulture));
        return new(true, state, Cents(SumOrNull(withStart, r => r.UsedStart)), Cents(results.Sum(r => r.UsedLatest)), Cents(SumOrNull(withEffective, r => r.Effective)),
            Cents(SumOrNull(withProjection, r => r.ProjectedEnd)), Cents(SumOrNull(results.Where(r => r.ProjectedRecovery is not null), r => r.ProjectedRecovery)),
            Cents(SumOrNull(results.Where(r => r.ProjectedFromNow is not null), r => r.ProjectedFromNow)), Cents(contracted), periodKind, results.Select(r => r.Dto).ToList(), reasons);
    }

    // ---- Cartões -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Consumo do cartão na competência (mês da fatura). Fatura fechada vale o total do banco; aberta, a composição local confirmada. Compras
    /// pendentes e parcelas já devolvidas pelo banco são "pendentes conhecidas"; as estimadas só existem com evidência. Pagamento de fatura não aparece
    /// aqui (não é consumo) e o que o banco informou de encargos é só destacado, já está dentro do total.
    /// </summary>
    public static CardSlice Slice(CardProjection projection, IReadOnlyList<PluggyBillFact> bills, string competence)
    {
        decimal confirmed = 0, pending = 0, estimated = 0;
        foreach (var invoice in projection.Invoices.Where(i => i.Competence == competence))
        {
            if (invoice.Phase == InvoicePhase.Closed) { confirmed += invoice.Obligation; continue; }
            confirmed += invoice.Composition; pending += invoice.PendingKnown + invoice.KnownInstallments; estimated += invoice.Estimated;
        }
        pending += projection.Items.Where(i => i.Kind == ObligationKind.KnownImported && i.DueDate?.ToString("yyyy-MM", CultureInfo.InvariantCulture) == competence).Sum(i => i.Obligation);
        var charges = bills.Where(b => b.DueDate?.ToString("yyyy-MM", CultureInfo.InvariantCulture) == competence).Sum(b => b.FinanceChargesTotal ?? 0m);
        var card = projection.Card;
        return new(card.Id, card.Name, card.Scope, card.PayerAccountId, confirmed, pending, estimated, charges);
    }

    public static CardTargetDto CardTarget(IReadOnlyList<CardSlice> slices, decimal? target, string? targetSource, bool targetPartial, IReadOnlyList<Guid> payerAccountIds, IReadOnlyList<string> reasons)
    {
        var confirmed = slices.Sum(s => s.Confirmed); var pending = slices.Sum(s => s.PendingKnown); var estimated = slices.Sum(s => s.Estimated);
        var projected = confirmed + pending + estimated; var charges = slices.Sum(s => s.Charges);
        var applicable = slices.Count > 0;
        var defined = target is not null;
        var caveats = reasons.Where(r => r is not (Reasons.PlanNotDefined or Reasons.PlanNotAttributable or Reasons.NoCards)).ToList();
        return new(applicable, applicable ? (caveats.Count == 0 && !targetPartial ? PlanActualStates.Complete : PlanActualStates.Partial) : PlanActualStates.Unavailable,
            defined ? PlanActualStates.Defined : PlanActualStates.NotDefined, Cents(target), defined ? targetSource : null, targetPartial,
            Cents(confirmed), Cents(pending), Cents(estimated), Cents(projected),
            defined ? Cents(Math.Max(0m, target!.Value - projected)) : null, defined ? Cents(Math.Max(0m, projected - target!.Value)) : null,
            defined ? Cents(Math.Max(0m, confirmed - target!.Value)) : null,
            target is > 0m ? Percent(confirmed * 100m / target.Value) : null, target is > 0m ? Percent(projected * 100m / target.Value) : null, Cents(charges),
            slices.Select(s => new CardTargetCardDto(s.CardId, s.Name, s.Scope, s.PayerAccountId, Cents(s.Confirmed), Cents(s.PendingKnown), Cents(s.Estimated), Cents(s.Projected))).ToList(),
            payerAccountIds, reasons);
    }

    // ---- Sincronização -------------------------------------------------------------------------------------------------------

    /// <summary>Quanto tempo sem sincronizar já é "desatualizado": uma vez e meia o intervalo automático, ou 48 h quando a automática está desligada.</summary>
    public static TimeSpan StaleAfter(bool autoEnabled, int intervalHours) => TimeSpan.FromHours(autoEnabled ? Math.Max(1, intervalHours) * 1.5 : 48);

    private static readonly string[] StatePrecedence = ["ConsentExpired", "Failed", "NeverSynced", "Running", "CompletedWithErrors", "SourceNotUpdated", "Completed"];

    public static SyncSummaryDto Sync(IReadOnlyList<SyncConnectionDto> connections, bool autoEnabled, int intervalHours, DateTimeOffset now)
    {
        var successes = connections.Where(c => c.LastSuccessAt is not null).Select(c => c.LastSuccessAt!.Value).ToList();
        var neverSynced = connections.Any(c => c.LastSuccessAt is null);
        var oldest = neverSynced || successes.Count == 0 ? (DateTimeOffset?)null : successes.Min();
        var latest = successes.Count == 0 ? (DateTimeOffset?)null : successes.Max();
        var same = !neverSynced && successes.Count > 0 && (successes.Max() - successes.Min()) <= TimeSpan.FromMinutes(5);
        var stale = connections.Count > 0 && (neverSynced || now - successes.Min() > StaleAfter(autoEnabled, intervalHours));
        var state = StatePrecedence.FirstOrDefault(s => connections.Any(c => c.Status == s)) ?? "NeverSynced";
        var upcoming = connections.Where(c => c.NextAttemptAt is not null).Select(c => c.NextAttemptAt!.Value).ToList();
        DateTimeOffset? next = autoEnabled && upcoming.Count > 0 ? upcoming.Min() : null;
        return new(autoEnabled, intervalHours, oldest, latest, same, stale, state, next, connections);
    }
}

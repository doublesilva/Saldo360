using Saldo360.Application.Accounts;
using Saldo360.Application.Calendar;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Domain.Accounts;

namespace Saldo360.Application.MonthlyPlan;

public sealed partial class MonthlyPlanService
{
    // ---- Projeção integrada de seis meses (PROJ-01) -----------------------------------------------------------------------------------------------
    // Reutiliza a projeção mensal da Agenda (T07.5) das contas PJ e PF: saldos, entradas e saídas já tratam previsões, recorrências, cartões, dívidas e repasses previstos
    // sem duplicar o realizado. Aqui só se compõe a leitura (receita PJ estimada, reservas, disponibilidade, conta PF e recuperação do limite). Nada é gravado.

    private async Task<IntegratedProjectionDto?> IntegratedAsync(Guid workspaceId, Guid userId, DateOnly first, DateOnly last, DateOnly today, IReadOnlyList<FinancialAccountDto> all,
        IReadOnlyList<(Saldo360.Application.CreditCards.Projection.CardProjection Projection, IReadOnlyList<Saldo360.Application.Pluggy.PluggyBillFact> Bills)> allCards, CancellationToken ct)
    {
        var through = last.AddMonths(1).AddDays(-1); // `last` é o primeiro dia do sexto mês
        var forecast = await repository.LoadIncomeForecastAsync(workspaceId, first, through, today, ct);
        var pj = all.FirstOrDefault(a => a.IsActive && a.Id == forecast.Settings?.AccountId) ?? DefaultPjAccount(all);
        var pf = all.FirstOrDefault(a => a.IsActive && a.IsPrimary && a.FinancialScope == FinancialScope.PF) ?? all.FirstOrDefault(a => a.IsActive && a.FinancialScope == FinancialScope.PF);
        if (pj is null || pf is null) return null;

        var pjProjection = await monthlyProjection.GetAsync(workspaceId, userId, new(first, 6, pj.Id), ct, today);
        var pfProjection = await monthlyProjection.GetAsync(workspaceId, userId, new(first, 6, pf.Id), ct, today);
        if (pjProjection.Projection is null || pfProjection.Projection is null) return null;
        var flows = (await repository.TransferFlowsAsync(workspaceId, pj.Id, pf.Id, first, through, ct)).ToDictionary(f => f.Month);
        var fixedPending = await repository.PendingFixedByMonthAsync(workspaceId, pf.Id, first, through, today, ct);
        var autoTransfers = await repository.ProjectedPjTransfersAsync(workspaceId, today, ct);

        var months = new List<IntegratedMonthDto>();
        for (var index = 0; index < pfProjection.Projection.Months.Count; index++)
        {
            var p = pjProjection.Projection.Months[index];
            var f = pfProjection.Projection.Months[index];
            var occurrences = forecast.Occurrences.Where(o => o.PaymentDate.Year == f.Month.Year && o.PaymentDate.Month == f.Month.Month).ToList();
            var income = IncomeFor(forecast, occurrences, p.RealizedIncome);

            var taxObligation = forecast.TaxObligationMonths.Contains(f.Month);
            var tax = forecast.Effective && !taxObligation ? occurrences.Sum(o => o.TaxReserve) : 0m;
            var operational = forecast.Settings?.OperationalReserve ?? 0m;
            var available = p.ClosingBalance is { } pjClosing ? PjAvailability.Available(pjClosing, tax, operational) : (decimal?)null;
            flows.TryGetValue(f.Month, out var flow);
            var plannedTransfer = flow?.PlannedPending ?? 0m; var realizedTransfer = flow?.Realized ?? 0m;
            var monthAuto = autoTransfers.Where(t => t.Date.Year == f.Month.Year && t.Date.Month == f.Month.Month).ToList();
            var autoTransfer = monthAuto.Sum(t => t.Amount); var dependsOnInferred = monthAuto.Any(t => t.DependsOnInferredIncome);
            fixedPending.TryGetValue(f.Month, out var fixedAmount);
            var cards = f.CreditCardOutstanding ?? 0m;
            var other = Math.Max(0m, f.PendingExpense - fixedAmount - cards);
            var entries = f.RealizedIncome + f.PendingIncome; var exits = f.RealizedExpense + f.PendingExpense;

            var overdraft = await OverdraftAsync(workspaceId, pf, f, today, ct);
            // Meta única "Todos os cartões" (consumo por competência da fatura): meta de consumo, separada do fluxo de caixa e nunca usada como fatura prevista.
            var cardSlices = allCards.Select(c => PlanActualCalculator.Slice(c.Projection, c.Bills, Key(f.Month))).ToList();
            var cardTarget = PlanActualService.BuildCardTarget(null, cardSlices, allCards.Select(c => c.Projection).ToList(), (await planActualRepository.LoadBasisAsync(workspaceId, f.Month, null, ct)).CardBudgetTarget, f.CreditCardProjectionComplete);
            var quality = new List<string>();
            if (!forecast.Effective) quality.Add(MonthlyPlanCalculator.CoverageReasons.IncomeForecastNotConfigured);
            if (forecast.Inferred) { quality.Add(MonthlyPlanCalculator.CoverageReasons.IncomeForecastInferred); quality.Add(MonthlyPlanCalculator.CoverageReasons.TaxReserveNotConfigured); }
            if (dependsOnInferred) quality.Add(MonthlyPlanCalculator.CoverageReasons.TransferDependsOnInferredIncome);
            if (income.Source == IncomeForecastStatus.Dismissed) quality.Add(MonthlyPlanCalculator.CoverageReasons.IncomeForecastDismissed);
            if (occurrences.Any(o => o.Ambiguous)) quality.Add(MonthlyPlanCalculator.CoverageReasons.IncomeReceiptAmbiguous);
            if (occurrences.Any(o => o.Overdue)) quality.Add(MonthlyPlanCalculator.CoverageReasons.IncomeReceiptOverdue);
            if (occurrences.Any(o => o.UnidentifiedAmount > 0m)) quality.Add(MonthlyPlanCalculator.CoverageReasons.IncomeReceiptUnidentified);
            if (occurrences.Any(o => o.Excess > 0m)) quality.Add(MonthlyPlanCalculator.CoverageReasons.IncomeReceiptExceedsForecast);
            if (allCards.Count == 0) quality.Add(MonthlyPlanCalculator.CoverageReasons.NoCardsRegistered);
            if (!f.CreditCardProjectionComplete || !p.CreditCardProjectionComplete) quality.Add(PlanActualCalculator.Reasons.CardCoverageIncomplete);
            if (!f.DebtProjectionComplete || !p.DebtProjectionComplete) quality.Add(PlanActualCalculator.Reasons.DebtCoverageIncomplete);
            if (f.PendingBankAmbiguous > 0m) quality.Add(PlanActualCalculator.Reasons.PendingBankAmbiguous);
            foreach (var unavailable in new[] { f.UnavailableReason, p.UnavailableReason }.OfType<BalanceUnavailableReason>().Distinct()) quality.Add(PlanActualCalculator.Reasons.ProjectionUnavailable + ":" + unavailable);

            months.Add(new(Key(f.Month), PlanPeriodKind(f.PeriodKind), income,
                new(MonthlyPlanCalculator.CentsOrNull(p.OpeningBalance), MonthlyPlanCalculator.Cents(p.PendingIncome), MonthlyPlanCalculator.Cents(p.PendingExpense), MonthlyPlanCalculator.Cents(tax), taxObligation,
                    MonthlyPlanCalculator.Cents(operational), MonthlyPlanCalculator.CentsOrNull(p.ClosingBalance), MonthlyPlanCalculator.CentsOrNull(available),
                    available is { } a ? MonthlyPlanCalculator.Cents(PjAvailability.Suggested(a)) : null),
                new(MonthlyPlanCalculator.Cents(plannedTransfer), MonthlyPlanCalculator.Cents(realizedTransfer), MonthlyPlanCalculator.Cents(autoTransfer), dependsOnInferred),
                new(MonthlyPlanCalculator.CentsOrNull(f.OpeningBalance), MonthlyPlanCalculator.CentsOrNull(f.ClosingBalance), MonthlyPlanCalculator.Cents(entries), MonthlyPlanCalculator.Cents(exits),
                    MonthlyPlanCalculator.Cents(entries - exits), MonthlyPlanCalculator.Cents(fixedAmount), MonthlyPlanCalculator.Cents(cards), MonthlyPlanCalculator.Cents(other),
                    MonthlyPlanCalculator.Cents(plannedTransfer + realizedTransfer + autoTransfer), MonthlyPlanCalculator.Cents(f.PendingBankUncovered), MonthlyPlanCalculator.Cents(f.PendingBankAmbiguous)),
                overdraft, cardTarget, quality.Count == 0, quality));
        }

        string? period = null;
        var firstMonth = pfProjection.Projection.Months[0]; var lastMonth = pfProjection.Projection.Months[^1];
        if (firstMonth.OpeningBalance is { } opening && lastMonth.ClosingBalance is { } closing)
            period = MonthlyPlanCalculator.Cents(OverdraftRecoveryRule.Used(opening) - OverdraftRecoveryRule.Used(closing));
        return new(forecast.Effective, pj.Id, pj.Name, pf.Id, pf.Name, period, months, forecast.Inferred);
    }

    private static IntegratedIncomeDto IncomeFor(IncomeForecastView forecast, IReadOnlyList<IncomeForecastOccurrence> occurrences, decimal realizedIncome)
    {
        var realized = MonthlyPlanCalculator.Cents(realizedIncome);
        if (forecast.Inferred && occurrences.Count > 0) return InferredIncomeFor(occurrences, realized);
        if (!forecast.Effective) return new("NotConfigured", null, null, null, null, null, null, null, realized, false);
        if (occurrences.Count == 0) return new("None", null, null, null, null, null, null, null, realized, false);
        var o = occurrences[0];
        var source = SourceOf(occurrences);
        return new(source, Key(o.WorkedMonth), o.BusinessDays, o.BillableDays, o.HoursPerDay.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), MonthlyPlanCalculator.Cents(o.HourlyRate),
            MonthlyPlanCalculator.Cents(occurrences.Sum(x => x.GrossAmount)), o.PaymentDate, realized, occurrences.Any(x => x.DaysAdjusted || x.AmountAdjusted || x.DateAdjusted));
    }

    private static string SourceOf(IReadOnlyList<IncomeForecastOccurrence> occurrences) =>
        occurrences.All(x => x.Status == IncomeForecastStatus.Dismissed) ? IncomeForecastStatus.Dismissed
        : occurrences.Any(x => x.Status == IncomeForecastStatus.Forecast) ? IncomeForecastStatus.Forecast
        : occurrences.Any(x => x.Status == IncomeForecastStatus.PartiallyReceived) ? IncomeForecastStatus.PartiallyReceived : IncomeForecastStatus.Realized;

    /// <summary>Receita inferida do histórico: sem valor/hora nem horas por dia (não foram configurados) e nunca apresentada como garantida; a origem é sempre sinalizada.</summary>
    private static IntegratedIncomeDto InferredIncomeFor(IReadOnlyList<IncomeForecastOccurrence> occurrences, string realized)
    {
        var o = occurrences[0];
        var source = SourceOf(occurrences);
        return new(source, Key(o.WorkedMonth), null, null, null, null, MonthlyPlanCalculator.Cents(occurrences.Sum(x => x.GrossAmount)), o.PaymentDate, realized,
            occurrences.Any(x => x.DaysAdjusted || x.AmountAdjusted || x.DateAdjusted), true);
    }

    /// <summary>Limite da conta PF no mês, a partir do saldo de abertura e do fechamento projetado. Meta já gravada é preservada; sem ela, a provisória é só calculada (nenhum snapshot é criado em leitura).</summary>
    private async Task<IntegratedOverdraftDto?> OverdraftAsync(Guid workspaceId, FinancialAccountDto pf, FinancialCalendarMonthlyProjectionMonth month, DateOnly today, CancellationToken ct)
    {
        if (month.OpeningBalance is not { } opening || month.ClosingBalance is not { } closing) return null;
        var usedStart = OverdraftRecoveryRule.Used(opening); var usedEnd = OverdraftRecoveryRule.Used(closing);
        var recovery = usedStart - usedEnd;
        var limit = pf.OverdraftLimit;
        var available = limit is { } l ? Math.Max(0m, l - usedEnd) : (decimal?)null;
        var excess = limit is { } c ? Math.Max(0m, usedEnd - c) : 0m;
        decimal? target = null; var state = "None";
        if (usedStart > 0m)
        {
            if (await recoveryRepository.GetAsync(workspaceId, pf.Id, month.Month, ct) is { } snapshot) { target = snapshot.RecoveryTarget; state = "Fixed"; }
            else { target = OverdraftRecoveryRule.Target(OverdraftRecoveryRule.Capacity(usedStart, closing)); state = "Provisional"; }
        }
        return new(MonthlyPlanCalculator.CentsOrNull(limit), MonthlyPlanCalculator.Cents(usedStart), MonthlyPlanCalculator.Cents(usedEnd), MonthlyPlanCalculator.CentsOrNull(available),
            MonthlyPlanCalculator.Cents(excess), MonthlyPlanCalculator.Cents(recovery), MonthlyPlanCalculator.CentsOrNull(target), state, OverdraftRecoveryRule.Achievement(recovery, target));
    }
}

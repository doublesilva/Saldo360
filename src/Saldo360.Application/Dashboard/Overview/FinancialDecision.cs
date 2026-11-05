using System.Globalization;
using Saldo360.Application.Calendar;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Dashboard.Overview;

// T11.1: decisão financeira da Visão Geral. Tudo é calculado aqui (decimal) a partir da evolução diária e do snapshot da Agenda; nada cria lançamento nem altera dado.
// Valores monetários são texto decimal exato; "indisponível" nunca vira zero. Cartões não são contas: só entram contas bancárias cadastradas.

public sealed record DecisionAccountRefDto(Guid AccountId, string Name, string Scope, bool IsPrimary);

/// <summary>
/// Contexto da análise. <c>Period</c>: Current, Future ou Closed. <c>BankPositionDate</c>/<c>BankObservedAt</c> = a posição bancária mais antiga do conjunto (conservador);
/// <c>LastSyncedAt</c> = a sincronização mais antiga das contas integradas do conjunto. Nulos quando não há integração (nunca inventados).
/// </summary>
public sealed record DecisionContextDto(string Competence, string CurrentCompetence, string Period, string ScopeKind, Guid? AccountId, string? AccountName, string? AccountScope,
    DateOnly ReferenceDate, DateOnly? BankPositionDate, DateTimeOffset? BankObservedAt, DateTimeOffset? LastSyncedAt, IReadOnlyList<DecisionAccountRefDto> Accounts,
    string? BankBalance = null, string? OverdraftUsed = null);

/// <summary><c>Nature</c>: Expenses, CardInvoices, Debts ou PlannedTransfers. <c>Overdue</c> = vencido e ainda não liquidado; <c>InCompetence</c> = vence de hoje (ou do início) até o fim da competência.</summary>
public sealed record DecisionCommitmentNatureDto(string Nature, string Overdue, string InCompetence, string BeforeCompetence, string Total, int Count);

public sealed record DecisionDueDateDto(DateOnly Date, string Amount);

/// <summary>
/// Compromissos restantes. <c>Period</c> Current/Future: <c>Overdue</c> (vencidos até ontem), <c>InCompetence</c> e <c>BeforeCompetence</c> (mês futuro: vencem entre hoje e o início da competência).
/// Closed: só o que AINDA está em aberto hoje com vencimento na competência (não se afirma que estava pendente na época) e <c>PaidInMonth</c> = débitos efetivos do mês.
/// <c>Reliability</c>: Complete ou Preliminary (<c>Caveats</c>: CardCoverageIncomplete, DebtCoverageIncomplete, PendingBankAmbiguous).
/// </summary>
public sealed record DecisionCommitmentsDto(string Period, string Reliability, string Total, string Overdue, string InCompetence, string BeforeCompetence, string? PaidInMonth,
    IReadOnlyList<DecisionCommitmentNatureDto> ByNature, IReadOnlyList<DecisionDueDateDto> ByDueDate, IReadOnlyList<string> Caveats);

/// <summary><c>Certainty</c>: Registered (previsão cadastrada) ou Inferred (estimada: receita PJ por horas e repasse dela derivado).</summary>
public sealed record DecisionExpectedInflowDto(DateOnly Date, string Amount, string Kind, string Certainty);

/// <summary>
/// Cobertura de UMA conta. <c>State</c>: Covered (recursos próprios), CoveredWithCredit (só com cheque especial), Insufficient (nem o limite cobre) ou Unavailable.
/// <c>GuaranteedState</c> = o mesmo cálculo sem receitas inferidas; <c>DependsOnInferredIncome</c> quando ele é pior. <c>Deficit</c> = quanto passa do limite permitido.
/// </summary>
public sealed record DecisionAccountCoverageDto(Guid AccountId, string Name, string Scope, string State, string Reliability, string GuaranteedState, bool DependsOnInferredIncome,
    string? MinimumBalance, DateOnly? MinimumDate, DateOnly? FirstNegativeDate, DateOnly? FirstBeyondLimitDate, string? Limit, string? CreditNeeded, string? Deficit,
    string? UnavailableReason, IReadOnlyList<string> Caveats, string? ProjectedClosing = null);

/// <summary>Cobertura. No consolidado os valores são os da pior conta e <c>Accounts</c> traz o risco de cada uma: saldos nunca se compensam entre contas.</summary>
public sealed record DecisionCoverageDto(string State, string Reliability, string Period, string? MinimumBalance, DateOnly? FirstNegativeDate, DateOnly? FirstBeyondLimitDate, string? Deficit,
    string ObligationsConsidered, IReadOnlyList<DecisionExpectedInflowDto> ExpectedInflows, IReadOnlyList<string> Caveats, string? UnavailableReason, IReadOnlyList<DecisionAccountCoverageDto> Accounts,
    string? ProjectedClosing = null);

/// <summary><c>Kind</c>: DailyCoverage (menor folga diária sob o limite contratado ou, sem limite, sob o saldo próprio), RecoveryTarget, MinimumReserve, BudgetMargin.</summary>
public sealed record DecisionMarginConstraintDto(string Kind, bool Applied, string? Slack, bool Binding, string? Reason);

/// <summary>
/// Margem segura para UM novo gasto à vista na conta, na data de referência. <c>State</c>: Calculated, Preliminary (há ressalvas em <c>Reasons</c>) ou Unavailable.
/// Nunca é o limite disponível, nem a margem do orçamento. Gasto no cartão tem outro impacto (no vencimento da fatura) e não é coberto: <c>Notes</c> traz CardSpendNotCovered.
/// </summary>
public sealed record DecisionSafeMarginDto(string State, string? Amount, string Scope, DateOnly ReferenceDate, string? BindingConstraint, IReadOnlyList<DecisionMarginConstraintDto> Constraints,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes);

/// <summary>
/// Meta de recuperação unificada (somente leitura sobre a meta já gravada). <c>State</c>: Provisional, Fixed, Pending, NotRegistered, NotApplicable (a conta não abriu negativa) ou Unavailable.
/// <c>GapToTarget</c> = recuperação projetada (ou final, no mês encerrado) − meta; negativo = abaixo da meta.
/// </summary>
public sealed record DecisionRecoveryDto(string State, string? Reason, string? OpeningUsed, string? Target, DateTimeOffset? TargetFixedAt, bool LegacyTarget, string? EffectiveRecovery,
    string? ProjectedRecovery, string? GapToTarget, string? ExpectedClosingUsed, string? Band);

public sealed record FinancialDecisionDto(string State, DecisionContextDto Context, DecisionCommitmentsDto Commitments, DecisionCoverageDto Coverage, DecisionSafeMarginDto SafeMargin,
    DecisionRecoveryDto Recovery, bool SnapshotConsistent, IReadOnlyList<string> Notes);

public sealed record DecisionAccountInput(Guid AccountId, string Name, string Scope, decimal? Limit, FinancialCalendarBalanceSnapshot Snapshot);

public static class FinancialDecisionCalculator
{
    public const string Covered = "Covered", CoveredWithCredit = "CoveredWithCredit", Insufficient = "Insufficient", Unavailable = "Unavailable";
    private static readonly string[] NatureOrder = ["Expenses", "CardInvoices", "Debts", "PlannedTransfers"];

    private static string F(decimal value) => Money.Format(value);
    private static decimal Cents(decimal value) => decimal.Round(value, Money.MaxScale, MidpointRounding.AwayFromZero);

    public static string Period(DateOnly first, DateOnly last, DateOnly today) => today > last ? "Closed" : today < first ? "Future" : "Current";

    /// <summary>Ressalvas de completude das fontes. Cartão/dívida sem conta pagadora ou responsável NUNCA é atribuído a uma conta: apenas sinaliza cobertura incompleta.</summary>
    public static List<string> SourceCaveats(FinancialCalendarBalanceSnapshot snapshot)
    {
        var caveats = new List<string>();
        if (snapshot.HasCardObligationsWithoutPayer) caveats.Add("CardCoverageIncomplete");
        if (snapshot.HasDebtObligationsWithoutResponsible) caveats.Add("DebtCoverageIncomplete");
        if (snapshot.PendingBank?.Any(item => item.State == PendingBankCoverageState.Ambiguous) == true) caveats.Add("PendingBankAmbiguous");
        return caveats;
    }

    // ---- Compromissos restantes ------------------------------------------------------------------------------------------------------------------------------------

    public static DecisionCommitmentsDto Commitments(FinancialCalendarBalanceSnapshot snapshot, DateOnly first, DateOnly last, DateOnly today, decimal? paidInMonth)
    {
        // Receita e repasse inferidos não são obrigações; transferências previstas cadastradas são "outros pagamentos conhecidos".
        var source = snapshot.WithoutInferred();
        var period = Period(first, last, today);
        var rows = new List<(string Nature, DateOnly Date, decimal Amount)>();
        // Cada fonte entra uma vez: despesas/recorrências/débitos pendentes (Pending), faturas (CreditCardObligations) e parcelas (DebtObligations) são listas disjuntas.
        // Pending soma várias fontes por dia e a remoção do inferido é feita por linhas negativas: agrega por data ANTES de filtrar.
        var pendingByDate = (source.Pending ?? []).GroupBy(r => r.Date).Select(g => (Date: g.Key, Expense: g.Sum(r => r.Expense), Out: g.Sum(r => r.TransferOut))).ToList();
        rows.AddRange(pendingByDate.Where(r => r.Expense > 0m).Select(r => ("Expenses", r.Date, r.Expense)));
        rows.AddRange(pendingByDate.Where(r => r.Out > 0m).Select(r => ("PlannedTransfers", r.Date, r.Out)));
        rows.AddRange((source.CreditCardObligations ?? []).GroupBy(r => r.Date).Select(g => (Date: g.Key, Amount: g.Sum(r => r.Expense))).Where(r => r.Amount > 0m).Select(r => ("CardInvoices", r.Date, r.Amount)));
        rows.AddRange((source.DebtObligations ?? []).GroupBy(r => r.Date).Select(g => (Date: g.Key, Amount: g.Sum(r => r.Expense))).Where(r => r.Amount > 0m).Select(r => ("Debts", r.Date, r.Amount)));

        var windowStart = first > today ? first : today;
        // Mês encerrado: só o vencimento dentro da competência (o que ainda está em aberto hoje). Corrente/futuro: tudo até o fim da competência, separado por vencimento.
        string Bucket(DateOnly date) => period == "Closed" ? date >= first && date <= last ? "In" : "None"
            : date < today ? "Overdue" : date < first ? "Before" : date <= last ? "In" : "None";
        var classified = rows.Select(r => (r.Nature, r.Date, r.Amount, Bucket: Bucket(r.Date))).Where(r => r.Bucket != "None").ToList();

        decimal Sum(IEnumerable<(string Nature, DateOnly Date, decimal Amount, string Bucket)> items, string? bucket = null) => items.Where(i => bucket is null || i.Bucket == bucket).Sum(i => i.Amount);
        var byNature = NatureOrder.Select(nature =>
        {
            var items = classified.Where(r => r.Nature == nature).ToList();
            return new DecisionCommitmentNatureDto(nature, F(Sum(items, "Overdue")), F(Sum(items, "In")), F(Sum(items, "Before")), F(Sum(items)), items.Count);
        }).Where(n => n.Count > 0).ToList();
        var byDate = classified.Where(r => r.Bucket != "Overdue" && r.Date >= (period == "Closed" ? first : windowStart)).GroupBy(r => r.Date).OrderBy(g => g.Key)
            .Select(g => new DecisionDueDateDto(g.Key, F(g.Sum(r => r.Amount)))).ToList();
        var caveats = SourceCaveats(snapshot);
        return new(period, caveats.Count == 0 ? "Complete" : "Preliminary", F(Sum(classified)), F(Sum(classified, "Overdue")), F(Sum(classified, "In")), F(Sum(classified, "Before")),
            period == "Closed" && paidInMonth is { } paid ? F(paid) : null, byNature, byDate, caveats);
    }

    // ---- Cobertura -----------------------------------------------------------------------------------------------------------------------------------------------

    public sealed record PathEvaluation(string State, FinancialCalendarBalances Balances, decimal? Minimum, DateOnly? MinimumDate, DateOnly? FirstNegative, DateOnly? FirstBeyondLimit,
        decimal? CreditNeeded, decimal? Deficit, string? UnavailableReason);

    /// <summary>
    /// Avalia a evolução diária de hoje ao fim da competência. O limite NÃO é dinheiro próprio: saldo negativo é "só com crédito" e passar do limite é insuficiência.
    /// Sem limite informado (inclusive PJ), qualquer saldo negativo ultrapassa o limite. O saldo mínimo é o de qualquer dia, não só o do fechamento.
    /// </summary>
    public static PathEvaluation Evaluate(FinancialCalendarBalanceSnapshot snapshot, DateOnly today, DateOnly last, decimal? limit)
    {
        var balances = FinancialCalendarBalanceService.CalculateBalances(new(today, last, null), today, snapshot);
        var days = balances.Days.Where(day => day.ClosingBalance is not null).ToList();
        if (balances.UnavailableReason is { } reason || days.Count == 0 || days.Count != balances.Days.Count)
            return new(Unavailable, balances, null, null, null, null, null, null, balances.UnavailableReason?.ToString() ?? "BalanceUnavailable");
        var allowed = Math.Max(0m, limit ?? 0m);
        var minimum = days.Min(day => day.ClosingBalance!.Value);
        var minimumDate = days.First(day => day.ClosingBalance == minimum).Date;
        var firstNegative = days.FirstOrDefault(day => day.ClosingBalance < 0m)?.Date;
        var firstBeyond = days.FirstOrDefault(day => day.ClosingBalance < -allowed)?.Date;
        var state = firstBeyond is not null ? Insufficient : firstNegative is not null ? CoveredWithCredit : Covered;
        return new(state, balances, minimum, minimumDate, firstNegative, firstBeyond, minimum < 0m ? -minimum : 0m, state == Insufficient ? -allowed - minimum : null, null);
    }

    private static int Severity(string state) => state switch { Insufficient => 3, Unavailable => 2, CoveredWithCredit => 1, _ => 0 };

    public static DecisionAccountCoverageDto AccountCoverage(DecisionAccountInput account, DateOnly today, DateOnly last, bool bankDivergent)
    {
        var expected = Evaluate(account.Snapshot, today, last, account.Limit);
        var guaranteed = account.Snapshot.InferredPending is { Count: > 0 } ? Evaluate(account.Snapshot.WithoutInferred(), today, last, account.Limit) : expected;
        var dependsOnInferred = expected.State != Unavailable && guaranteed.State != Unavailable && Severity(guaranteed.State) > Severity(expected.State);
        var caveats = SourceCaveats(account.Snapshot);
        if (bankDivergent) caveats.Add("BalanceDivergentFromBank");
        if (dependsOnInferred) caveats.Add("DependsOnInferredIncome");
        if (account.Limit is null && (expected.Minimum ?? 0m) < 0m) caveats.Add("OverdraftLimitNotInformed");
        var reliability = expected.State == Unavailable ? Unavailable : caveats.Count == 0 ? "Complete" : "Preliminary";
        return new(account.AccountId, account.Name, account.Scope, expected.State, reliability, guaranteed.State, dependsOnInferred,
            expected.Minimum is { } min ? F(min) : null, expected.MinimumDate, expected.FirstNegative, expected.FirstBeyondLimit, account.Limit is { } limit ? F(limit) : null,
            expected.CreditNeeded is { } credit and > 0m ? F(credit) : null, expected.Deficit is { } deficit ? F(deficit) : null, expected.UnavailableReason, caveats,
            expected.State == Unavailable ? null : expected.Balances.Risk.EndingProjectedBalance is { } ending ? F(ending) : null);
    }

    public static DecisionCoverageDto Coverage(IReadOnlyList<DecisionAccountInput> inputs, IReadOnlyList<DecisionAccountCoverageDto> accounts, DateOnly today, DateOnly first, DateOnly last, decimal obligationsConsidered)
    {
        var period = Period(first, last, today);
        if (period == "Closed") return new(Unavailable, Unavailable, period, null, null, null, null, F(0m), [], [], "ClosedCompetence", accounts);
        if (accounts.Count == 0) return new(Unavailable, Unavailable, period, null, null, null, null, F(0m), [], [], "NoAccounts", accounts);
        var worst = accounts.OrderByDescending(a => Severity(a.State)).ThenBy(a => a.FirstBeyondLimitDate ?? DateOnly.MaxValue).First();
        var caveats = accounts.SelectMany(a => a.Caveats).Distinct().ToList();
        var reliability = worst.State == Unavailable ? Unavailable : accounts.Any(a => a.Reliability != "Complete") ? "Preliminary" : "Complete";
        // Entradas esperadas: previsões cadastradas e inferidas (marcadas), datas futuras da janela. Repasses cadastrados entram como transferência.
        var inflows = new List<DecisionExpectedInflowDto>();
        foreach (var input in inputs)
        {
            var inferred = (input.Snapshot.InferredPending ?? []).GroupBy(r => r.Date).ToDictionary(g => g.Key, g => (Income: g.Sum(r => r.Income), In: g.Sum(r => r.TransferIn)));
            foreach (var row in (input.Snapshot.Pending ?? []).GroupBy(r => r.Date).Where(g => g.Key >= today && g.Key <= last))
            {
                inferred.TryGetValue(row.Key, out var guess);
                var income = row.Sum(r => r.Income) - guess.Income; var transfer = row.Sum(r => r.TransferIn) - guess.In;
                if (income > 0m) inflows.Add(new(row.Key, F(income), "Income", "Registered"));
                if (transfer > 0m) inflows.Add(new(row.Key, F(transfer), "Transfer", "Registered"));
                if (guess.Income > 0m) inflows.Add(new(row.Key, F(guess.Income), "Income", "Inferred"));
                if (guess.In > 0m) inflows.Add(new(row.Key, F(guess.In), "Transfer", "Inferred"));
            }
        }
        var ordered = inflows.OrderBy(i => i.Date).ThenBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Certainty, StringComparer.Ordinal).Take(12).ToList();
        // Fechamento projetado só faz sentido para UMA conta: somar saldos de contas diferentes seria compensá-los.
        return new(worst.State, reliability, period, worst.MinimumBalance, worst.FirstNegativeDate, worst.FirstBeyondLimitDate, worst.Deficit, F(obligationsConsidered), ordered, caveats, worst.UnavailableReason, accounts,
            accounts.Count == 1 ? worst.ProjectedClosing : null);
    }

    // ---- Margem segura ---------------------------------------------------------------------------------------------------------------------------------------------

    private static DecisionSafeMarginDto NoMargin(DateOnly today, params string[] reasons) =>
        new(Unavailable, null, "CashSpend", today, null, [], reasons, ["CardSpendNotCovered"]);

    /// <summary>
    /// Margem para um gasto à vista HOJE: o gasto reduz todos os saldos diários de hoje ao fim da competência, então a margem é a MENOR folga entre (a) o menor saldo diário
    /// sob o limite contratado (ou sob zero, sem limite) e (b) a meta de recuperação, quando a conta abriu negativa. Usa só entradas registradas (inferidas ficam de fora).
    /// </summary>
    public static DecisionSafeMarginDto SafeMargin(DecisionAccountInput? account, DateOnly today, DateOnly first, DateOnly last, decimal? opening, OverviewRecoveryDto? recovery,
        IReadOnlyList<string> extraCaveats)
    {
        if (account is null) return NoMargin(today, "ConsolidatedScope");
        if (Period(first, last, today) != "Current") return NoMargin(today, "CompetenceNotCurrent");
        if (opening is null) return NoMargin(today, "OpeningBalanceMissing");
        var path = Evaluate(account.Snapshot.WithoutInferred(), today, last, account.Limit);
        if (path.State == Unavailable) return NoMargin(today, "BalanceUnavailable");

        var constraints = new List<(string Kind, decimal? Slack, string? Reason)>();
        // O cheque especial nunca é dinheiro próprio: só serve de TETO para quem já abriu a competência usando-o (o gasto adicional aprofunda um uso que já existe, e a meta de
        // recuperação limita o fechamento). Quem abriu sem usá-lo tem folga apenas até o saldo zero.
        var allowed = opening < 0m ? Math.Max(0m, account.Limit ?? 0m) : 0m;
        constraints.Add(("DailyCoverage", path.Minimum!.Value + allowed, allowed > 0m ? "ContractedLimit" : "OwnFundsOnly"));
        var reasons = new List<string>(extraCaveats);
        if (opening < 0m)
        {
            // A meta fixada é referência histórica: só é lida. Sem meta verificável não se inventa uma (nem se trata ausência como zero).
            if (recovery is not { Target: { } targetText } || recovery.TargetState is not ("Fixed" or "Provisional")) return NoMargin(today, "RecoveryTargetUnavailable");
            var allowedUsed = Math.Max(0m, -opening.Value - decimal.Parse(targetText, CultureInfo.InvariantCulture));
            constraints.Add(("RecoveryTarget", path.Balances.Days[^1].ClosingBalance!.Value + allowedUsed, recovery.TargetState));
            if (recovery.TargetState == "Provisional") reasons.Add("ProvisionalRecoveryTarget");
        }
        else constraints.Add(("RecoveryTarget", null, "AccountDidNotOpenNegative"));
        constraints.Add(("MinimumReserve", null, "NotConfigured"));
        constraints.Add(("BudgetMargin", null, "SpendNotCategorised"));

        var applied = constraints.Where(c => c.Slack is not null).ToList();
        var tightest = applied.MinBy(c => c.Slack)!;
        var margin = Math.Max(0m, decimal.Floor(tightest.Slack!.Value * 100m) / 100m);
        var dtos = constraints.Select(c => new DecisionMarginConstraintDto(c.Kind, c.Slack is not null, c.Slack is { } s ? F(Cents(s)) : null, c.Slack is not null && c.Kind == tightest.Kind, c.Reason)).ToList();
        return new(reasons.Count == 0 ? "Calculated" : "Preliminary", F(margin), "CashSpend", today, tightest.Kind, dtos, reasons.Distinct().ToList(), ["CardSpendNotCovered", "InferredIncomeExcluded"]);
    }

    // ---- Meta de recuperação ---------------------------------------------------------------------------------------------------------------------------------------

    public static DecisionRecoveryDto RecoverySummary(bool singleAccount, decimal? opening, OverviewRecoveryDto? recovery)
    {
        if (!singleAccount) return new("NotApplicable", "ConsolidatedScope", null, null, null, false, null, null, null, null, null);
        if (opening is null) return new(Unavailable, "OpeningBalanceMissing", null, null, null, false, null, null, null, null, null);
        if (opening >= 0m) return new("NotApplicable", "AccountDidNotOpenNegative", null, null, null, false, null, null, null, null, null);
        if (recovery is null) return new(Unavailable, "RecoveryUnavailable", F(-opening.Value), null, null, false, null, null, null, null, null);
        var parse = (string? value) => value is null ? (decimal?)null : decimal.Parse(value, CultureInfo.InvariantCulture);
        var measured = parse(recovery.ProjectedRecovery ?? recovery.FinalRecovery);
        var target = parse(recovery.Target);
        var gap = measured is { } m && target is { } t ? F(m - t) : null;
        var expectedClosing = parse(recovery.ProjectedClosing) is { } pc ? F(OverdraftRecoveryRule.Used(pc)) : recovery.FinalRecovery is not null ? F(OverdraftRecoveryRule.Used(parse(recovery.CurrentBalance)!.Value)) : null;
        return new(recovery.TargetState, recovery.PendingReason, recovery.OpeningUsed, recovery.Target, recovery.TargetFixedAt, recovery.LegacyTarget, recovery.RecoveryCurrent,
            recovery.ProjectedRecovery, gap, expectedClosing, recovery.Band);
    }
}

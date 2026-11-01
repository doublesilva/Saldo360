using System.Globalization;
using Saldo360.Application.Accounts;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Dashboard.Overview;

// Recuperação do cheque especial nos cards "Saldo de abertura" / "Saldo de fechamento" da Visão Geral. Tudo é calculado aqui (decimal); o cliente só formata.
// Reutiliza a projeção do plano x realizado e a tabela overdraft_recovery_snapshots (T10.4). Regras em docs/T10.4-recuperacao-limite-cheque-especial.md.

/// <summary>
/// Recuperação da conta (aparece só quando ela inicia a competência com saldo NEGATIVO). <c>TargetState</c>: Provisional (dias 1–9, recalculada), Fixed (gravada),
/// Pending (dado essencial ausente: <c>PendingReason</c>) ou NotRegistered (mês encerrado sem meta gravada; nada é inventado retroativamente).
/// <c>Band</c>: Exceeded (&gt;100%), Met (80–100%), Attention (&gt;50% e &lt;80%), Critical (≤50%, inclusive negativo) ou NoTarget (meta zero ou indisponível).
/// <c>Projected*</c> só no mês em andamento; <c>Final*</c> só no mês encerrado. Recuperação negativa é preservada (piora).
/// </summary>
public sealed record OverviewRecoveryDto(
    string OpeningUsed, string? Target, string TargetState, string? TargetFactor, string? Capacity, DateTimeOffset? TargetFixedAt, DateOnly FixDate, bool LegacyTarget, string? PendingReason,
    string CurrentBalance, DateOnly CurrentAsOf, string RecoveryCurrent,
    string? ProjectedClosing, string? ProjectedRecovery, string? ProjectedAchievementPercent, string Band,
    string? FinalRecovery, string? FinalAchievementPercent);

/// <summary>Projeção do fechamento da conta (plano x realizado). <c>BlockReason</c> impede FIXAR a meta (ex.: faturas de cartão ainda não fechadas).</summary>
public sealed record RecoveryProjectionInput(decimal? ProjectedClosing, decimal Income, decimal Expense, string Quality, string Composition, string? BlockReason);

public sealed record RecoveryBuild(OverviewRecoveryDto Dto, bool ShouldFix, decimal Capacity, decimal Target);

public static class OverdraftRecoveryRule
{
    /// <summary>Fator da meta nova: 80% da capacidade projetada (os 20% restantes são margem). Metas históricas guardam outro fator (ou nenhum) e nunca são reescritas.</summary>
    public const decimal TargetFactor = 0.80m;
    /// <summary>Dia da competência (America/Sao_Paulo) em que a meta é fixada.</summary>
    public const int FixDay = 10;

    public static DateOnly FixDate(DateOnly first) => new(first.Year, first.Month, FixDay);
    public static decimal Used(decimal balance) => Math.Max(0m, -balance);
    /// <summary>Recuperação integral projetada, nunca negativa: o que a conta deixaria de usar do cheque especial até o fechamento projetado.</summary>
    public static decimal Capacity(decimal openingUsed, decimal projectedClosing) => Math.Max(0m, openingUsed - Used(projectedClosing));
    /// <summary>Mesmo modo de arredondamento do restante do dinheiro (centavos, metade afastada de zero).</summary>
    public static decimal Target(decimal capacity) => decimal.Round(capacity * TargetFactor, Money.MaxScale, MidpointRounding.AwayFromZero);

    /// <summary>Atingimento em % (uma casa, como o plano x realizado). Meta zero ou nula não tem percentual.</summary>
    public static string? Achievement(decimal recovery, decimal? target) => target is > 0m ? PlanActualCalculator.Percent(recovery / target.Value * 100m) : null;

    /// <summary>Faixa pelo percentual EXIBIDO: &gt;100 Exceeded; 80–100 Met; &gt;50 e &lt;80 Attention; ≤50 (inclusive negativo) Critical.</summary>
    public static string Band(string? percent)
    {
        if (percent is null) return "NoTarget";
        var value = decimal.Parse(percent, CultureInfo.InvariantCulture);
        return value > 100m ? "Exceeded" : value >= 80m ? "Met" : value > 50m ? "Attention" : "Critical";
    }

    public static RecoveryBuild? Build(DateOnly first, DateOnly last, DateOnly today, decimal opening, decimal closing, OverdraftRecoverySnapshot? snapshot, RecoveryProjectionInput? projection)
    {
        var openingUsed = Used(opening);
        if (openingUsed == 0m || today < first) return null;
        var inProgress = today <= last;
        var asOf = inProgress ? today : last;
        var fixDate = FixDate(first);
        var state = "NotRegistered"; string? pending = null, factor = null, capacityText = null;
        decimal? target = null; decimal capacity = 0m; DateTimeOffset? fixedAt = null; var legacy = false; var shouldFix = false;
        var projectedClosing = projection?.ProjectedClosing;
        if (snapshot is not null)
        {
            state = "Fixed"; target = snapshot.RecoveryTarget; legacy = snapshot.TargetFactor is null; fixedAt = snapshot.FixedAt ?? snapshot.CapturedAt;
            factor = snapshot.TargetFactor is { } f ? f.ToString("0.00", CultureInfo.InvariantCulture) : null;
            capacityText = snapshot.TargetCapacity is { } c ? Money.Format(c) : null;
        }
        else if (inProgress)
        {
            if (projectedClosing is null) { state = "Pending"; pending = "ProjectionUnavailable"; }
            else
            {
                capacity = Capacity(openingUsed, projectedClosing.Value); target = Target(capacity); capacityText = Money.Format(capacity);
                factor = TargetFactor.ToString("0.00", CultureInfo.InvariantCulture);
                if (today < fixDate) state = "Provisional";
                else if (projection!.BlockReason is { } reason) { state = "Pending"; pending = reason; target = null; capacityText = null; factor = null; }
                else { state = "Fixed"; shouldFix = true; }
            }
        }
        var recoveryCurrent = openingUsed - Used(closing);
        decimal? recoveryProjected = inProgress && projectedClosing is { } p ? openingUsed - Used(p) : null;
        var measure = inProgress ? recoveryProjected : recoveryCurrent; // mês encerrado: o fechamento efetivo; em andamento: o previsto para o encerramento
        var percent = measure is { } m ? Achievement(m, target) : null;
        var dto = new OverviewRecoveryDto(Money.Format(openingUsed), target is { } t ? Money.Format(t) : null, state, factor, capacityText, fixedAt, fixDate, legacy, pending,
            Money.Format(closing), asOf, Money.Format(recoveryCurrent),
            inProgress && projectedClosing is { } pc ? Money.Format(pc) : null, recoveryProjected is { } rp ? Money.Format(rp) : null, inProgress ? percent : null, Band(percent),
            inProgress ? null : Money.Format(recoveryCurrent), inProgress ? null : percent);
        return new(dto, shouldFix, capacity, target ?? 0m);
    }
}

public sealed partial class DashboardOverviewService
{
    private async Task<OverviewRecoveryDto?> BuildRecoveryAsync(Guid workspaceId, Guid userId, string month, Guid? accountId, DateOnly first, DateOnly last, DateOnly today,
        AccountPeriodCashDto? account, CancellationToken ct)
    {
        if (accountId is not { } id || account is null || account.Opening is not { } opening || account.Closing is not { } closing || opening >= 0m || today < first) return null;
        try
        {
            var snapshot = await recoveryRepository.GetAsync(workspaceId, id, first, ct);
            // Projeção só é necessária no mês em andamento e quando a meta ainda não foi gravada ou para a recuperação prevista.
            RecoveryProjectionInput? projection = null;
            if (today <= last)
            {
                var plan = await planActual.GetAsync(workspaceId, userId, month, PlanActualScopes.Account, id, ct);
                if (plan.Value is { } dashboard)
                {
                    var reasons = dashboard.Coverage.Reasons.Concat(dashboard.Overdraft.Accounts.SelectMany(a => a.Reasons)).Distinct().ToArray();
                    var block = dashboard.Coverage.Reasons.Contains(PlanActualCalculator.Reasons.CardCoverageIncomplete) ? PlanActualCalculator.Reasons.CardCoverageIncomplete : null;
                    projection = new(dashboard.Situation.ProjectedClosing is { } c ? Num(c) : null, Num(dashboard.Income.Projected), Num(dashboard.Expenses.CashFlow.Projected),
                        dashboard.Coverage.Complete && dashboard.SnapshotConsistent ? "Complete" : "Partial", string.Join(",", reasons), block);
                }
            }
            var build = OverdraftRecoveryRule.Build(first, last, today, opening, closing, snapshot, projection);
            if (build is not { ShouldFix: true }) return build?.Dto;
            // Fixação lazy no dia 10 (ou depois, enquanto a meta ainda não existe): AddIfAbsent é serializado por workspace + índice único, então é idempotente e race-safe.
            var p = projection!;
            var fixedSnapshot = await recoveryRepository.AddIfAbsentAsync(OverdraftRecoverySnapshot.CreateFixed(workspaceId, id, first, opening, first, p.Income, p.Expense, p.ProjectedClosing!.Value,
                build.Capacity, OverdraftRecoveryRule.TargetFactor, build.Target, p.Quality, p.Composition, time.GetUtcNow()), ct);
            return OverdraftRecoveryRule.Build(first, last, today, opening, closing, fixedSnapshot, projection)?.Dto;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // a recuperação é complementar: nunca derruba o fluxo de caixa
        }
    }
}

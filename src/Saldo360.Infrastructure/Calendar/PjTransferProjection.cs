using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Calendar;
using Saldo360.Application.MonthlyPlan;
using Saldo360.Domain.Accounts;
using Saldo360.Infrastructure.MonthlyPlan;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Calendar;

internal sealed record AutoTransferPlan(Guid PjAccountId, Guid PfAccountId, IReadOnlyList<ProjectedPjTransfer> Transfers, IncomeForecastView Forecast);

/// <summary>
/// PROJ-06: fonte ÚNICA do repasse PJ → PF projetado. Agenda (saldo diário e mensal), planejamento de seis meses e recuperação do limite leem o mesmo plano. O horizonte é FIXO
/// (mês atual + 5), independente do período consultado, para que a mesma data tenha o mesmo repasse em qualquer tela. Somente leitura: nenhuma transferência, lançamento ou
/// previsão é gravado, e nenhuma transferência bancária é executada.
/// </summary>
internal static class PjTransferProjection
{
    public const int HorizonMonths = 6;

    public static DateOnly Horizon(DateOnly today) => new DateOnly(today.Year, today.Month, 1).AddMonths(HorizonMonths).AddDays(-1);

    /// <summary>Dados lidos além do horizonte (um mês), para que a janela de proteção do último repasse seja a mesma em qualquer horizonte e o resultado de uma data não dependa dele.</summary>
    private static DateOnly DataThrough(DateOnly horizon) => new DateOnly(horizon.Year, horizon.Month, 1).AddMonths(2).AddDays(-1);

    public static async Task<AutoTransferPlan?> PlanAsync(Saldo360DbContext db, Guid workspaceId, DateOnly today, CancellationToken ct) =>
        await PlanAsync(db, workspaceId, today, Horizon(today), ct);

    public static async Task<AutoTransferPlan?> PlanAsync(Saldo360DbContext db, Guid workspaceId, DateOnly today, DateOnly horizon, CancellationToken ct)
    {
        horizon = horizon > Horizon(today) ? horizon : Horizon(today); // nunca abaixo do horizonte padrão; o resultado de uma data não depende dele
        var dataThrough = DataThrough(horizon);
        var forecast = await IncomeForecastLoader.LoadAsync(db, workspaceId, new DateOnly(today.Year, today.Month, 1), dataThrough, today, ct);
        if (!forecast.Effective) return null;
        var pjId = forecast.Settings!.AccountId;
        var pfId = await db.FinancialAccounts.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.IsActive && a.Scope == FinancialScope.PF)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Id).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        if (pfId is null || pfId == pjId) return null;

        // Saldo diário da PJ SEM o repasse projetado (já líquido de despesas pendentes, recebimentos previstos e repasses previstos).
        var snapshot = await new FinancialCalendarBalanceRepository(db).ReadWithoutAutoTransfersAsync(workspaceId, pjId, dataThrough, today, ct);
        if (snapshot is null) return null;
        var balances = FinancialCalendarBalanceService.CalculateBalances(new(today, dataThrough, pjId), today, snapshot);
        if (balances.UnavailableReason is not null) return new(pjId, pfId.Value, [], forecast);
        var closing = balances.Days.Where(d => d.ClosingBalance is not null).ToDictionary(d => d.Date, d => d.ClosingBalance!.Value);
        var transfers = PjTransferPlanner.Plan(today, horizon, closing, forecast.Occurrences, forecast.Settings.OperationalReserve, forecast.TaxObligationMonths, forecast.Inferred);
        return new(pjId, pfId.Value, transfers, forecast);
    }
}

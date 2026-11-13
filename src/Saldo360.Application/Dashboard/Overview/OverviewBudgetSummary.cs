using Saldo360.Application.Dashboard.PlanActual;

namespace Saldo360.Application.Dashboard.Overview;

/// <summary>
/// Resumo enxuto do orçamento PF de uma competência (tela de Orçamento): só o total (meta, realizado, comprometido, estimado, previsto, margem, excesso) com estado, origem da meta e notas.
/// É o MESMO cálculo de <c>pfBudgetControl.total</c> da Visão Geral (mesma rotina, mesmas contas PF e mesmo resolvedor de metas), sem montar os demais blocos do dashboard.
/// <c>Total</c> nulo = indisponível (estado "Unavailable"): nunca zerado.
/// </summary>
public sealed record BudgetSummaryDto(string State, string Competence, string ScopeKind, BudgetTotalDto? Total, IReadOnlyList<string> Notes, string? TotalTargetOrigin, string? TotalTargetFrom);

public sealed partial class DashboardOverviewService
{
    public async Task<PlanActualResult<BudgetSummaryDto>> GetBudgetSummaryAsync(Guid workspaceId, Guid userId, string? competence, CancellationToken ct)
    {
        var resolved = await ResolveAsync(workspaceId, userId, competence, null, null, ct);
        if (resolved.Error is { } error) return new(error.Access, null, error.Errors);
        var (month, today) = (resolved.Month!, resolved.Today);
        var pfAccounts = (await accountService.ListAsync(workspaceId, userId, includeInactive: true, ct) ?? resolved.All).Where(a => a.FinancialScope == Saldo360.Domain.Accounts.FinancialScope.PF).ToList();
        // Metas excedidas (fixas e categorias) e pendências de classificação só alimentam pressões e recomendações, não o total: ficam de fora do resumo.
        var control = await BuildBudgetControlAsync(workspaceId, userId, month, PlanActualScopes.All, null, "pf", today, new("Complete", false, null, month, null, null, NoCategories()), new("Unavailable", 0, 0), null, ct, pfAccounts);
        return PlanActualResult<BudgetSummaryDto>.Ok(control is null
            ? new("Unavailable", month, "pf", null, [], null, null)
            : new(control.State, control.Competence, control.ScopeKind, control.Total, control.Notes, control.TotalTargetOrigin, control.TotalTargetFrom));
    }
}

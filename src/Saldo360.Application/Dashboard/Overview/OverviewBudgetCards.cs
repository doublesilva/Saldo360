using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Dashboard.Overview;

// Detalhamento dos cartões do orçamento PF, sob demanda. A base é a mesma de pfBudgetControl.cards.projected: a fatia do Plano × Realizado de cada cartão (PlanActualCalculator.Slice, compras pela
// competência da fatura) mais a provisão alimentar, que não pertence a um cartão. O pagamento da fatura é só estado de pagamento: não reduz o gasto. Somente leitura.

/// <summary>
/// Um cartão do orçamento PF na competência. <c>InvoiceState</c>: Closed (fechamento confirmado pela instituição), Open (ciclo em andamento), Future (ciclo futuro) ou ClosingUnconfirmed (fechamento
/// ultrapassado sem confirmação: nunca tratado como fechada). <c>Quality</c>: Confirmed (total do banco), Projected ou Incomplete (ressalvas da projeção). <c>PaymentState</c> (Unpaid, Partial, Paid,
/// Overpaid) é separado do estado da fatura. <c>DatesEstimated</c> = datas pelo calendário configurado, sem ciclo confirmado pelo histórico do banco.
/// </summary>
public sealed record BudgetCardItemDto(Guid CardId, string Name, string Projected, string Confirmed, string PendingKnown, string Estimated, string InvoiceState, DateOnly? Closing, DateOnly? Due,
    bool DatesEstimated, string Quality, string? PaymentState);

/// <summary>
/// <c>Total</c> = <c>Allocated</c> (soma dos cartões) + <c>FoodApplied</c> (previsão alimentar adicional que coube dentro da meta; a reserva nominal <c>FoodReserve</c> é submeta da meta e não é somada); <c>Official</c> = <c>pfBudgetControl.cards.projected</c> calculado pela mesma rotina. Se
/// diferirem, <c>State</c> é Partial com <c>UnexplainedDifference</c>, e o total oficial é preservado. <c>MissingPayerAccount</c>: há cartão sem pagador, não atribuído a nenhum escopo.
/// </summary>
public sealed record BudgetCardsDto(string State, string Competence, string Total, string Allocated, string FoodApplied, string? FoodReserve, string? Official, IReadOnlyList<BudgetCardItemDto> Cards, IReadOnlyList<string> Reasons);

public sealed partial class DashboardOverviewService
{
    public async Task<PlanActualResult<BudgetCardsDto>> GetBudgetCardsAsync(Guid workspaceId, Guid userId, string? competence, CancellationToken ct)
    {
        var resolved = await ResolveAsync(workspaceId, userId, competence, null, null, ct);
        if (resolved.Error is { } error) return new(error.Access, null, error.Errors);
        var (month, today) = (resolved.Month!, resolved.Today);
        var first = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
        var pfAccounts = (await accountService.ListAsync(workspaceId, userId, includeInactive: true, ct) ?? resolved.All).Where(a => a.FinancialScope == Saldo360.Domain.Accounts.FinancialScope.PF).ToList();
        var pfIds = pfAccounts.Select(a => a.Id).ToHashSet();

        var projections = await cardProjection.ComputeWithBillsAsync(workspaceId, today, ct);
        var reasons = new List<string>();
        if (projections.Any(p => p.Projection.Card.PayerAccountId is null)) reasons.Add("MissingPayerAccount");
        var cards = new List<(BudgetCardItemDto Item, decimal Projected, Guid Id)>();
        foreach (var (projection, bills) in projections.Where(p => p.Projection.Card.PayerAccountId is { } payer && pfIds.Contains(payer)))
        {
            var slice = PlanActualCalculator.Slice(projection, bills, month);
            var invoice = projection.Invoices.FirstOrDefault(i => i.Competence == month);
            if (invoice is null && slice.Projected == 0m) continue; // o cartão não tem fatura nem gasto nesta competência
            var state = invoice is null ? "Future" : invoice.Phase == InvoicePhase.Closed ? "Closed" : invoice.AwaitingBankBill ? "ClosingUnconfirmed" : invoice.Phase == InvoicePhase.Open ? "Open" : "Future";
            var incomplete = !projection.Coverage.Complete || invoice is { Flags.Count: > 0 };
            var quality = state == "Closed" && !incomplete ? "Confirmed" : incomplete ? "Incomplete" : "Projected";
            var estimated = invoice is { Phase: not InvoicePhase.Closed } && invoice.Flags.Contains(CardCoverageReasons.WeakSchedule);
            cards.Add((new(slice.CardId, slice.Name, PlanActualCalculator.Cents(slice.Projected), PlanActualCalculator.Cents(slice.Confirmed), PlanActualCalculator.Cents(slice.PendingKnown), PlanActualCalculator.Cents(slice.Estimated),
                state, invoice?.Closing, invoice?.Due, estimated, quality, invoice?.PaymentState.ToString()), slice.Projected, slice.CardId));
        }
        var ordered = cards.OrderByDescending(c => c.Projected).ThenBy(c => c.Item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id).Select(c => c.Item).ToList();

        var allocated = cards.Sum(c => c.Projected);
        var cardTarget = (await planBasis.LoadBasisAsync(workspaceId, first, null, ct)).CardBudgetTarget;
        var (food, reserve) = await FoodForecastAsync(workspaceId, first, today, allocated, cardTarget, ct);
        var official = (await BuildBudgetControlAsync(workspaceId, userId, month, PlanActualScopes.All, null, "pf", today, new("Complete", false, null, month, null, null, NoCategories()),
            new("Unavailable", 0, 0), null, ct, pfAccounts))?.Cards.Projected;
        if (official is null) reasons.Add("OfficialTotalUnavailable");
        else if (Dec(official) != allocated + food) reasons.Add("UnexplainedDifference");
        return PlanActualResult<BudgetCardsDto>.Ok(new(reasons.Any(r => r is "UnexplainedDifference" or "OfficialTotalUnavailable") ? "Partial" : "Complete", month,
            PlanActualCalculator.Cents(allocated + food), PlanActualCalculator.Cents(allocated), PlanActualCalculator.Cents(food), PlanActualCalculator.Cents(reserve), official, ordered, reasons));
    }
}

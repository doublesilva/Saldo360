using System.Globalization;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Dashboard.Overview;

// T14.3: resultado mensal da Visão Geral. Só compõe leituras que já existem (plan-actual, transferências do mês e decisão financeira): nenhuma regra de orçamento, cartão, transferência
// ou recuperação é recalculada aqui. Duas perspectivas separadas: CAIXA (entradas − saídas de dinheiro; o pagamento da fatura é saída, as compras que a compõem não) e ORÇAMENTÁRIA
// (receitas − consumo da competência; o pagamento da fatura não é novo consumo). Valores nulos significam indisponível, nunca zero.

/// <summary><c>Realized</c> nulo em mês futuro (nada foi realizado ainda). <c>Projected</c> = realizado + pendente.</summary>
public sealed record MonthlyAmountsDto(string? Realized, string? Pending, string? Projected);

/// <summary>Parte de um total. <c>Side</c>: Inflow/Outflow. <c>Status</c>: Realized/Pending. <c>Target</c>: onde conferir a origem (Calendar, Entries, Cards).</summary>
public sealed record MonthlyComponentDto(string Code, string Side, string Status, string Amount, string Target);

/// <summary>Resultado pela perspectiva orçamentária: receitas − consumo da competência. Não é resultado de caixa.</summary>
public sealed record MonthlyBudgetResultDto(string? Income, string? Consumption, string? Realized, string? Projected);

/// <summary>
/// Ponte do saldo observado ao fechamento projetado. <c>Unreconciled</c> é a diferença que os dados existentes não explicam: é exibida como está, nunca absorvida por um ajuste.
/// <c>State</c>: Complete (a ponte fecha), Partial (não fecha ou falta dado) ou Unavailable.
/// </summary>
public sealed record MonthlyBridgeDto(string State, string? ObservedBalance, DateOnly? ObservedDate, string? InflowsAfter, string? OutflowsAfter, string? ProjectedClosing, string? Unreconciled, IReadOnlyList<string> Reasons);

/// <summary>
/// Limite do cheque especial no escopo: contratado, utilizado, disponível (nunca negativo) e utilização percentual (uma casa; nulo sem limite). Limite disponível não é saldo próprio.
/// <c>ProjectedUsed</c>/<c>ProjectedAvailable</c> = no fechamento da competência; nulos quando a projeção não é confiável. Só existe quando a conta tem limite contratado.
/// </summary>
public sealed record MonthlyOverdraftDto(string ContractedLimit, string Used, string Available, string? UtilizationPercent, bool ExceedsLimit, string? ProjectedUsed, string? ProjectedAvailable, string? ProjectedPercent);

/// <summary>
/// <c>Highlight</c>: Realized (mês passado) ou Projected (mês corrente e futuro). <c>Status</c> do resultado destacado: Positive, Negative, Neutral ou Unavailable.
/// <c>TransfersReceived</c>/<c>TransfersSent</c> só existem com uma conta no escopo; no consolidado as transferências internas são neutras.
/// </summary>
public sealed record OverviewMonthlyResultDto(string State, string PeriodKind, string Highlight, string Status, MonthlyAmountsDto Inflows, MonthlyAmountsDto Outflows, MonthlyAmountsDto Result,
    string? TransfersReceived, string? TransfersSent, IReadOnlyList<MonthlyComponentDto> Components, MonthlyBudgetResultDto Budget, MonthlyBridgeDto Bridge, IReadOnlyList<string> Reasons, MonthlyOverdraftDto? Overdraft = null);

public static class MonthlyResultCalculator
{
    private static decimal Num(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static string Fmt(decimal value) => Money.Format(decimal.Round(value, Money.MaxScale, MidpointRounding.AwayFromZero));

    public static OverviewMonthlyResultDto Build(PlanActualDashboardDto plan, OverviewTransfersDto? transfers, FinancialDecisionDto? decision)
    {
        var future = plan.PeriodKind == "Future";
        var past = plan.PeriodKind == "Past";
        var reasons = new List<string>();
        if (plan.Income.State != PlanActualStates.Complete || plan.Expenses.CashFlow.State != PlanActualStates.Complete) reasons.Add("PlanPartial");

        // Transferências só alteram o caixa de uma conta. No consolidado elas se anulam (o servidor já classifica pela perspectiva do conjunto selecionado).
        var single = plan.Scope.Kind != PlanActualScopes.All;
        decimal received = 0m, sent = 0m;
        string? receivedText = null, sentText = null;
        if (single && !future)
        {
            if (transfers is { State: "Complete" }) { received = Num(transfers.Received); sent = Num(transfers.Sent); receivedText = transfers.Received; sentText = transfers.Sent; }
            else reasons.Add("TransfersUnavailable");
        }

        var income = plan.Income; var cash = plan.Expenses.CashFlow;
        var inflows = new MonthlyAmountsDto(future ? null : Fmt(Num(income.Realized) + received), Fmt(Num(income.Pending)), Fmt(Num(income.Projected) + received));
        var outflows = new MonthlyAmountsDto(future ? null : Fmt(Num(cash.Realized) + sent), Fmt(Num(cash.Pending)), Fmt(Num(cash.Projected) + sent));
        var result = new MonthlyAmountsDto(future ? null : Fmt(Num(inflows.Realized!) - Num(outflows.Realized!)), Fmt(Num(inflows.Pending!) - Num(outflows.Pending!)), Fmt(Num(inflows.Projected!) - Num(outflows.Projected!)));

        var components = new List<MonthlyComponentDto>
        {
            new("IncomeRealized", "Inflow", "Realized", income.Realized, "Entries"),
            new("IncomePending", "Inflow", "Pending", income.Pending, "Calendar"),
        };
        if (received != 0m) components.Add(new("TransfersReceived", "Inflow", "Realized", Fmt(received), "Entries"));
        foreach (var component in cash.Components)
        {
            // PendingBankAmbiguous fica de fora: pode já estar na agenda e não entra no total, então não pode virar parcela.
            var (status, target) = component.Code switch
            {
                "BankExpenses" or "CardInvoicePayments" => ("Realized", "Entries"),
                "PendingPlanned" or "PendingBank" => ("Pending", "Calendar"),
                "PendingCardInvoices" => ("Pending", "Cards"),
                _ => ((string?)null, (string?)null),
            };
            if (status is null || target is null) continue;
            components.Add(new(component.Code, "Outflow", status, component.Amount, target));
        }
        if (sent != 0m) components.Add(new("TransfersSent", "Outflow", "Realized", Fmt(sent), "Entries"));

        var consumption = plan.Expenses.Consumption;
        var budget = new MonthlyBudgetResultDto(income.Realized, consumption.Realized, future ? null : Fmt(Num(income.Realized) - Num(consumption.Realized)), Fmt(Num(income.Projected) - Num(consumption.Projected)));

        var highlighted = past ? result.Realized : result.Projected;
        var status2 = highlighted is null ? "Unavailable" : Num(highlighted) > 0m ? "Positive" : Num(highlighted) < 0m ? "Negative" : "Neutral";
        return new(reasons.Count == 0 ? "Complete" : "Partial", plan.PeriodKind, past ? "Realized" : "Projected", status2, inflows, outflows, result, receivedText, sentText, components, budget,
            Bridge(plan, decision, past), reasons, Overdraft(plan.Overdraft));
    }

    private static MonthlyOverdraftDto? Overdraft(OverdraftDto overdraft)
    {
        if (!overdraft.Eligible || overdraft.ContractedLimit is null || overdraft.UsedLatest is null) return null;
        var limit = Num(overdraft.ContractedLimit);
        if (limit <= 0m) return null;
        var used = Num(overdraft.UsedLatest);
        string? Percent(decimal value) => Fmt1(value / limit * 100m);
        var projected = overdraft.ProjectedUsedAtEnd is null ? (decimal?)null : Num(overdraft.ProjectedUsedAtEnd);
        return new(overdraft.ContractedLimit, Fmt(used), Fmt(Math.Max(limit - used, 0m)), Percent(used), used > limit,
            projected is null ? null : Fmt(projected.Value), projected is null ? null : Fmt(Math.Max(limit - projected.Value, 0m)), projected is null ? null : Percent(projected.Value));
    }

    private static string Fmt1(decimal value) => decimal.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);

    private static MonthlyBridgeDto Bridge(PlanActualDashboardDto plan, FinancialDecisionDto? decision, bool past)
    {
        if (past) return new("Unavailable", null, null, null, null, null, null, ["ClosedMonth"]);
        var bank = decision?.Context.BankBalance;
        var situation = plan.Situation;
        if (bank is null) return new("Unavailable", null, null, null, null, situation.ProjectedClosing, null, ["NoBankBalance"]);
        var date = decision!.Context.BankPositionDate;
        if (situation.ProjectedClosing is null) return new("Partial", bank, date, null, null, null, null, ["ProjectionUnavailable"]);
        // Tudo que a projeção ainda espera depois da referência: o pendente do mês mais o que ficou de períodos anteriores (já embutido no fechamento projetado).
        var inflows = Num(situation.PendingIncome) + Num(situation.CarryoverIncome);
        var outflows = Num(situation.PendingExpense) + Num(situation.CarryoverExpense);
        var gap = Num(situation.ProjectedClosing) - (Num(bank) + inflows - outflows);
        return new(gap == 0m ? "Complete" : "Partial", bank, date, Fmt(inflows), Fmt(outflows), situation.ProjectedClosing, gap == 0m ? null : Fmt(gap), gap == 0m ? [] : ["BridgeNotReconciled"]);
    }
}

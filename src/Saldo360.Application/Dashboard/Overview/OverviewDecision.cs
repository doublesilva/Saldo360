using System.Globalization;
using Saldo360.Application.Accounts;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Dashboard.Overview;

// T11.1: monta o bloco "Decision" da Visão Geral na MESMA leitura (mesma data de referência, mesmo conjunto de contas e o mesmo fluxo de caixa já calculado), reutilizando a Agenda
// (evolução diária), o saldo por conta e a regra de recuperação. Somente leitura: nada aqui grava lançamento nem recalcula meta (a meta é lida de cashFlow.Recovery).

public sealed partial class DashboardOverviewService
{
    private async Task<FinancialDecisionDto?> BuildDecisionAsync(Guid workspaceId, string month, FinancialAccountDto? selected, IReadOnlyList<FinancialAccountDto> all, string kind,
        DateOnly today, OverviewCashFlowDto? cashFlow, CancellationToken ct)
    {
        try
        {
            var first = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
            var last = first.AddMonths(1).AddDays(-1);
            var period = FinancialDecisionCalculator.Period(first, last, today);
            var scoped = selected is not null ? [selected] : all.ToList();
            var single = selected is not null;

            var inputs = new List<DecisionAccountInput>();
            if (period != "Closed")
            {
                foreach (var account in scoped)
                {
                    var read = await calendarBalances.ReadSnapshotAsync(workspaceId, account.Id, last, today, ct);
                    if (read is { } value) inputs.Add(new(account.Id, account.Name, account.FinancialScope.ToString(), account.OverdraftLimit, value.Snapshot));
                }
            }
            // Compromissos: o mesmo conjunto da análise (uma conta ou o consolidado). Em conta única reaproveita a leitura já feita.
            var commitmentSnapshot = single && inputs.Count == 1 ? inputs[0].Snapshot
                : (await calendarBalances.ReadSnapshotAsync(workspaceId, single ? selected!.Id : null, last, today, ct))?.Snapshot;
            if (commitmentSnapshot is null) return null;

            decimal? paid = cashFlow is null ? null : Num(cashFlow.Outflows.Debits);
            var commitments = FinancialDecisionCalculator.Commitments(commitmentSnapshot, first, last, today, paid);
            var divergent = cashFlow?.Reconciliation.Status == "Divergent";
            var coverageAccounts = period == "Closed" ? [] : inputs.Select(input => FinancialDecisionCalculator.AccountCoverage(input, today, last, divergent)).ToList();
            var coverage = FinancialDecisionCalculator.Coverage(inputs, coverageAccounts, today, first, last, Num(commitments.Total));

            decimal? opening = single && cashFlow?.Opening is { } text ? Num(text) : null;
            var margin = single && inputs.Count == 1
                ? FinancialDecisionCalculator.SafeMargin(inputs[0], today, first, last, opening, cashFlow?.Recovery, coverageAccounts.Count == 1 ? MarginCaveats(coverageAccounts[0]) : [])
                : FinancialDecisionCalculator.SafeMargin(null, today, first, last, null, null, []);
            var recovery = FinancialDecisionCalculator.RecoverySummary(single, opening, cashFlow?.Recovery);

            var positions = cashFlow?.BankPosition.Accounts ?? [];
            var integrated = scoped.Select(a => a.Integration?.LastSyncedAt).Where(v => v is not null).Select(v => v!.Value).ToList();
            var currentCompetence = $"{today.Year:0000}-{today.Month:00}";
            // Posição do banco (observada, não calculada). O cheque especial usado soma o uso de CADA conta: saldos positivos de uma não reduzem o uso de outra.
            var bankComplete = cashFlow?.BankPosition is { State: "Complete" } && positions.Count > 0;
            var bankBalance = bankComplete ? positions.Sum(p => Num(p.Amount)) : (decimal?)null;
            var overdraftUsed = bankComplete ? positions.Sum(p => OverdraftRecoveryRule.Used(Num(p.Amount))) : (decimal?)null;
            var context = new DecisionContextDto(month, currentCompetence, period, kind, selected?.Id, selected?.Name, selected?.FinancialScope.ToString(), today,
                positions.Count == 0 ? null : positions.Min(p => p.AsOfDate), positions.Count == 0 ? null : positions.Min(p => p.ObservedAt), integrated.Count == 0 ? null : integrated.Min(),
                all.Select(a => new DecisionAccountRefDto(a.Id, a.Name, a.FinancialScope.ToString(), a.IsPrimary)).ToList(),
                bankBalance is { } balance ? Cents(balance) : null, overdraftUsed is { } used ? Cents(used) : null, BankSourceDto.From(scoped));

            var notes = new List<string>();
            if (cashFlow is { SnapshotConsistent: false }) notes.Add("PositionsInconsistent");
            if (cashFlow?.Reconciliation.Status == "NotVerifiable") notes.Add("BalanceNotVerifiedWithBank");
            var partial = coverage.State == FinancialDecisionCalculator.Unavailable || margin.State == FinancialDecisionCalculator.Unavailable;
            return new(partial ? "Partial" : "Complete", context, commitments, coverage, margin, recovery, cashFlow?.SnapshotConsistent ?? false, notes);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // complementar: nunca derruba os demais blocos
        }
    }

    // Ressalvas que tornam a margem preliminar (o resto, como "depende de receita inferida", não a afeta: a margem já exclui entradas inferidas).
    private static IReadOnlyList<string> MarginCaveats(DecisionAccountCoverageDto coverage) =>
        coverage.Caveats.Where(c => c is not ("DependsOnInferredIncome")).ToList();
}

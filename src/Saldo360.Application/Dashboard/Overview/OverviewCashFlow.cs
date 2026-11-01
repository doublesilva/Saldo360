using System.Globalization;
using Saldo360.Application.Accounts;
using Saldo360.Application.Transfers;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Dashboard.Overview;

// Fluxo de caixa da Visão Geral (caixa x competência). Tudo aqui é CAIXA: movimentações efetivas da perspectiva escolhida. Tipo (receita/despesa/transferência), direção
// (entrada/saída), perspectiva (conta ou conjunto) e regime (caixa x competência) são conceitos separados. Valores monetários são texto decimal exato; "indisponível" nunca vira zero.

/// <summary>Conta que impede um valor de ser apurado (ex.: sem saldo inicial). <c>Reason</c>: OpeningBalanceNotConfigured, BeforeOpeningBalanceDate, NoBankPosition, ...</summary>
public sealed record OverviewCashAccountIssueDto(Guid AccountId, string Name, string Reason);

/// <summary>
/// Entradas = todos os créditos efetivos (receitas externas, devoluções, outros créditos, inclusive sem classificação) + transferências recebidas de fora do conjunto.
/// <c>Credits</c> já inclui <c>Refunds</c> (informativo: parcela de devoluções/estornos, nunca receita econômica).
/// </summary>
public sealed record OverviewCashInflowsDto(string Total, string Credits, string? Refunds, string TransfersReceived);

/// <summary>Saídas = todos os débitos efetivos (despesas pagas, pagamento de fatura, juros/tarifas, inclusive sem classificação) + transferências enviadas para fora do conjunto.</summary>
public sealed record OverviewCashOutflowsDto(string Total, string Debits, string TransfersSent);

/// <summary>Transferência do período na perspectiva: <c>Relation</c> Inbound (entrada), Outbound (saída) ou Internal (neutralizada); <c>Effect</c> = efeito assinado no conjunto.</summary>
public sealed record OverviewCashTransferDto(Guid Id, DateOnly Date, string Description, string Source, string Destination, string Amount, string Relation, string Effect);

public sealed record OverviewBankPositionAccountDto(Guid AccountId, string Name, string Amount, string Kind, DateTimeOffset ObservedAt, DateOnly AsOfDate, string Reliability);

/// <summary>Posição informada pelo banco (integração), com data/hora da observação. <c>Total</c> só existe quando TODAS as contas do conjunto têm posição; senão <c>Missing</c> diz quais faltam.</summary>
public sealed record OverviewBankPositionDto(string State, string? Total, DateTimeOffset? ObservedAt, IReadOnlyList<OverviewBankPositionAccountDto> Accounts, IReadOnlyList<OverviewCashAccountIssueDto> Missing);

/// <summary>
/// Conciliação do saldo calculado com o saldo bancário. <c>Status</c>: Verified (diferença zero, mesma data de corte, histórico completo), Divergent (diferença não nula) ou
/// NotVerifiable (falta dado: <c>Reasons</c>). A comparação é feita na data de corte do banco (<c>CutoffDate</c>), que pode ser anterior ao último dia do período.
/// </summary>
public sealed record OverviewReconciliationDto(string Status, string? Bank, string? Calculated, string? Difference, DateOnly? CutoffDate, IReadOnlyList<string> Reasons, IReadOnlyList<OverviewCashAccountIssueDto> Accounts);

/// <summary>Orçamento por COMPETÊNCIA: independente do caixa e nunca usado para conciliar o saldo bancário.</summary>
public sealed record OverviewCompetenceDto(string? ClassifiedIncome, string? Expenses, string? Difference);

/// <summary>
/// Ponte de saldo: <c>Closing</c> = <c>Opening</c> + <c>Inflows.Total</c> − <c>Outflows.Total</c>. <c>State</c>: Complete (abertura conhecida) ou Partial (sem abertura: fluxo
/// exibido, mas não é possível conciliar; <c>Opening</c>/<c>Closing</c> nulos e <c>OpeningMissing</c> lista as contas). <c>IdentityHolds</c> compara o fechamento calculado com a
/// soma independente dos saldos por conta (nulo sem abertura); <c>SnapshotConsistent</c> falso = os dados mudaram durante a leitura.
/// </summary>
public sealed record OverviewCashFlowDto(string State, string Competence, DateOnly From, DateOnly Through, bool InProgress, int AccountCount,
    string? Opening, IReadOnlyList<OverviewCashAccountIssueDto> OpeningMissing, OverviewCashInflowsDto Inflows, OverviewCashOutflowsDto Outflows, string? Closing,
    bool? IdentityHolds, bool SnapshotConsistent, OverviewBankPositionDto BankPosition, OverviewReconciliationDto Reconciliation, string InternalMoved,
    IReadOnlyList<OverviewCashTransferDto> Transfers, OverviewCompetenceDto ByCompetence, OverviewRecoveryDto? Recovery = null);

public sealed partial class DashboardOverviewService
{
    private const int MaxCashTransfers = 10;
    private static decimal Num(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static string Cents(decimal value) => Money.Format(decimal.Round(value, Money.MaxScale, MidpointRounding.AwayFromZero));

    private async Task<OverviewCashFlowDto?> BuildCashFlowAsync(Guid workspaceId, Guid userId, string month, Guid? accountId, DateOnly today, OverviewCompetenceDto competence, CancellationToken ct)
    {
        try
        {
            var first = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
            var last = first.AddMonths(1).AddDays(-1);
            var period = await accountService.GetPeriodCashAsync(workspaceId, userId, first, last, ct);
            if (period is null) return null;
            // Conjunto: a conta escolhida (mesmo inativa) ou todas as contas do orçamento com saldo/movimento. Só ele define o que neutraliza.
            var set = accountId is { } one ? period.Where(p => p.AccountId == one).ToList() : period.ToList();
            var ids = set.Select(p => p.AccountId).ToHashSet();
            if (accountId is { } wanted && ids.Count == 0) ids.Add(wanted);

            var transfers = await LoadTransfersAsync(workspaceId, first, last, ct);
            decimal received = 0m, sent = 0m, internalMoved = 0m;
            var lines = new List<OverviewCashTransferDto>();
            foreach (var t in transfers.OrderBy(t => t.TransferDate).ThenBy(t => t.Id))
            {
                var relation = TransferPerspective.Classify(t.SourceAccount.Id, t.DestinationAccount.Id, ids);
                if (relation == TransferRelation.Outside) continue;
                switch (relation)
                {
                    case TransferRelation.Inbound: received += t.Amount; break;
                    case TransferRelation.Outbound: sent += t.Amount; break;
                    default: internalMoved += t.Amount; break;
                }
                var effect = relation == TransferRelation.Inbound ? t.Amount : relation == TransferRelation.Outbound ? -t.Amount : 0m;
                lines.Add(new(t.Id, t.TransferDate, t.Description, t.SourceAccount.Name, t.DestinationAccount.Name, Money.Format(t.Amount), relation.ToString(), Money.Format(effect)));
            }

            var credits = set.Sum(p => p.Totals.Income); var debits = set.Sum(p => p.Totals.Expense);
            var inflows = credits + received; var outflows = debits + sent;
            string? refunds = null;
            try
            {
                var agenda = await calendar.ListAsync(workspaceId, userId, new(first, last, accountId, null), ct);
                if (agenda.Calendar is { } data) refunds = Money.Format(data.Summary.RealizedRefunds);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* devoluções são informativas */ }

            var missing = set.Where(p => p.Opening is null).Select(p => new OverviewCashAccountIssueDto(p.AccountId, p.Name, p.OpeningReason ?? "OpeningBalanceNotConfigured")).ToList();
            string? opening = null, closing = null; bool? identity = null;
            if (set.Count > 0 && missing.Count == 0)
            {
                var openingSum = set.Sum(p => p.Opening!.Value);
                opening = Money.Format(openingSum);
                var computed = openingSum + inflows - outflows;
                closing = Money.Format(computed);
                identity = computed == set.Sum(p => p.Closing!.Value);
            }
            // A soma das pontas das transferências (mesmo snapshot das contas) tem de bater com a perspectiva: senão algo mudou durante a leitura.
            var snapshotConsistent = set.Sum(p => p.Totals.TransferIn) - set.Sum(p => p.Totals.TransferOut) == received - sent;
            var state = set.Count > 0 && missing.Count == 0 ? "Complete" : "Partial";
            var bank = await BuildBankPositionAndReconciliationAsync(workspaceId, set, ct);
            // Recuperação do cheque especial: só para UMA conta (um conjunto não compensa saldos de contas diferentes) que abre a competência negativa.
            var recovery = set.Count == 1 && accountId is not null && missing.Count == 0 ? await BuildRecoveryAsync(workspaceId, userId, month, accountId, first, last, today, set[0], ct) : null;
            return new(state, month, first, last, last > today, set.Count, opening, missing,
                new(Money.Format(inflows), Money.Format(credits), refunds, Money.Format(received)), new(Money.Format(outflows), Money.Format(debits), Money.Format(sent)), closing,
                identity, snapshotConsistent, bank.Position, bank.Reconciliation, Money.Format(internalMoved), lines.Take(MaxCashTransfers).ToList(), competence, recovery);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<(OverviewBankPositionDto Position, OverviewReconciliationDto Reconciliation)> BuildBankPositionAndReconciliationAsync(Guid workspaceId, IReadOnlyList<AccountPeriodCashDto> set, CancellationToken ct)
    {
        var none = (new OverviewBankPositionDto("Unavailable", null, null, [], []), new OverviewReconciliationDto("NotVerifiable", null, null, null, null, ["BankDataUnavailable"], []));
        try
        {
            var overview = await reconciliation.BuildOverviewAsync(workspaceId, ct);
            var byId = overview.Accounts.ToDictionary(a => a.AccountId);
            var positions = new List<OverviewBankPositionAccountDto>(); var missing = new List<OverviewCashAccountIssueDto>();
            var issues = new List<OverviewCashAccountIssueDto>(); var reasons = new List<string>();
            decimal bankSum = 0m, calcSum = 0m; var comparable = true;
            foreach (var account in set)
            {
                if (!byId.TryGetValue(account.AccountId, out var item) || item.Bank is null)
                {
                    missing.Add(new(account.AccountId, account.Name, "NoBankPosition")); issues.Add(new(account.AccountId, account.Name, "NoBankPosition")); comparable = false; continue;
                }
                var amount = Num(item.Bank.Amount);
                positions.Add(new(account.AccountId, account.Name, item.Bank.Amount, item.Bank.Kind, item.Bank.ObservedAt, item.Bank.AsOfDate, item.Bank.Reliability));
                if (item.Calculated.Amount is null) { issues.Add(new(account.AccountId, account.Name, item.Calculated.UnavailableReason ?? "CalculatedUnavailable")); comparable = false; continue; }
                if (item.Status is nameof(Accounts.Reconciliation.ReconciliationStatus.InsufficientBankReference) or nameof(Accounts.Reconciliation.ReconciliationStatus.IncompleteHistory) or nameof(Accounts.Reconciliation.ReconciliationStatus.MovementsInReview))
                { issues.Add(new(account.AccountId, account.Name, item.Status)); comparable = false; }
                bankSum += amount; calcSum += Num(item.Calculated.Amount);
            }
            var position = positions.Count == 0 ? new OverviewBankPositionDto("Unavailable", null, null, positions, missing)
                : new OverviewBankPositionDto(missing.Count == 0 ? "Complete" : "Partial", missing.Count == 0 ? Cents(positions.Sum(p => Num(p.Amount))) : null, positions.Min(p => p.ObservedAt), positions, missing);
            if (set.Count == 0) return (position, none.Item2);
            var cutoffs = positions.Select(p => p.AsOfDate).Distinct().ToList();
            if (cutoffs.Count > 1) { comparable = false; reasons.Add("CutoffMismatch"); }
            if (missing.Count > 0) reasons.Add("NoBankPosition");
            if (issues.Count > 0 && reasons.Count == 0) reasons.Add("AccountsNotComparable");
            if (!comparable) return (position, new("NotVerifiable", null, null, null, cutoffs.Count == 1 ? cutoffs[0] : null, reasons, issues));
            var difference = bankSum - calcSum;
            var verified = Accounts.Reconciliation.BalanceReconciliation.SameCents(difference, 0m);
            return (position, new(verified ? "Verified" : "Divergent", Cents(bankSum), Cents(calcSum), Cents(difference), cutoffs[0], reasons, issues));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return none;
        }
    }
}

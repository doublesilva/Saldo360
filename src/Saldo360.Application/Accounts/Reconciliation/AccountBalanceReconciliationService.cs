using System.Globalization;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Accounts.Reconciliation;

/// <summary>
/// Saldo informado pelo banco × saldo calculado pelo Saldo360, por conta integrada (T09.5). Somente leitura: nunca grava ajuste, lançamento ou saldo
/// inicial e nunca "zera" uma diferença. Reutiliza o saldo inicial efetivo (manual ou derivado da âncora) e as somas de movimentos do núcleo.
/// </summary>
public sealed class AccountBalanceReconciliationService(
    IFinancialWorkspaceRepository workspaces, IAccountBalanceReconciliationRepository repository, IFinancialAccountRepository accounts, TimeProvider time)
{
    public const int MaxMonths = 24;

    public async Task<ReconciliationResult<ReconciliationOverviewDto>> OverviewAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return ReconciliationResult<ReconciliationOverviewDto>.NotFound;
        return ReconciliationResult<ReconciliationOverviewDto>.Ok(await BuildOverviewAsync(workspaceId, ct));
    }

    /// <summary>Visão para consumidores internos já autorizados (Dashboard).</summary>
    public async Task<ReconciliationOverviewDto> BuildOverviewAsync(Guid workspaceId, CancellationToken ct)
    {
        var today = BusinessCalendar.Today(time.GetUtcNow());
        var items = new List<AccountReconciliationDto>();
        foreach (var facts in await repository.LoadAsync(workspaceId, null, ct)) items.Add(await ReconcileAsync(workspaceId, facts, today, ct));
        var scopes = items.GroupBy(i => i.Scope).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Summarize(g.Key, g.ToList())).ToList();
        return new(today, items, scopes, Summarize("All", items));
    }

    public async Task<ReconciliationResult<AccountReconciliationPeriodDto>> PeriodAsync(Guid workspaceId, Guid userId, Guid accountId, string? from, string? through, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return ReconciliationResult<AccountReconciliationPeriodDto>.NotFound;
        var today = BusinessCalendar.Today(time.GetUtcNow());
        var last = through ?? today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (!TryMonth(last, out var lastMonth)) return ReconciliationResult<AccountReconciliationPeriodDto>.Invalid("through", "Informe o mês final no formato yyyy-MM.");
        var first = from ?? lastMonth.AddMonths(-5).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (!TryMonth(first, out var firstMonth)) return ReconciliationResult<AccountReconciliationPeriodDto>.Invalid("from", "Informe o mês inicial no formato yyyy-MM.");
        var span = (lastMonth.Year - firstMonth.Year) * 12 + lastMonth.Month - firstMonth.Month + 1;
        if (span < 1 || span > MaxMonths) return ReconciliationResult<AccountReconciliationPeriodDto>.Invalid("from", $"O período deve ter de 1 a {MaxMonths} meses.");
        if ((await repository.LoadAsync(workspaceId, accountId, ct)).SingleOrDefault() is not { } facts) return ReconciliationResult<AccountReconciliationPeriodDto>.NotFound;

        var opening = await accounts.GetOpeningBalanceAsync(workspaceId, accountId, ct);
        var current = facts.Anchors.FirstOrDefault(a => a.IsCurrent);
        var months = new List<MonthBalanceDto>();
        for (var i = 0; i < span; i++)
        {
            var start = firstMonth.AddMonths(i);
            var end = start.AddMonths(1).AddDays(-1);
            var (ledger, ledgerReason) = await LedgerStartOfDayAsync(workspaceId, accountId, opening, start, ct);
            var (bank, bankReason) = current is null ? ((decimal?)null, "NoBankObservation") : BalanceReconciliation.BankStartOfDay(current, facts.Posted, start);
            var review = facts.Posted.Where(m => m.InReview && m.Date >= start && m.Date <= end).ToList();
            months.Add(new(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), Fmt(ledger), ledgerReason, Fmt(bank), bankReason,
                ledger is { } l && bank is { } b ? Cents(b - l) : null, review.Count, Cents(review.Sum(m => m.SignedAmount ?? 0m)),
                ledger is null ? facts.Posted.Count(m => m.InReview && m.Date >= start) : 0));
        }

        var observations = new List<ObservationCheckDto>();
        var rangeStart = firstMonth; var rangeEnd = lastMonth.AddMonths(1).AddDays(-1);
        foreach (var anchor in facts.Anchors.Where(a => a.AsOfDate >= rangeStart && a.AsOfDate <= rangeEnd).OrderBy(a => a.ObservedAt))
        {
            var (ledger, _) = await LedgerStartOfDayAsync(workspaceId, accountId, opening, anchor.AsOfDate.AddDays(1), ct);
            var bank = current is null ? null : BalanceReconciliation.BankStartOfDay(current, facts.Posted, anchor.AsOfDate.AddDays(1)).Amount;
            var ledgerDifference = ledger is { } lg ? anchor.Amount - lg : (decimal?)null;
            var bankDifference = bank is { } bk ? anchor.Amount - bk : (decimal?)null;
            var status = anchor.IsCurrent ? (ledgerDifference is null ? "LedgerUnavailable" : BalanceReconciliation.SameCents(ledgerDifference.Value, 0m) ? "Match" : "LedgerDifference")
                : bankDifference is null ? "NotCheckable" : BalanceReconciliation.SameCents(bankDifference.Value, 0m) ? "Match" : "ImportedHistoryChanged";
            observations.Add(new(anchor.AsOfDate, anchor.ObservedAt, Cents(anchor.Amount), Fmt(ledger), Fmt(ledgerDifference), Fmt(bank), Fmt(bankDifference), status));
        }
        return ReconciliationResult<AccountReconciliationPeriodDto>.Ok(new(accountId, facts.Name, facts.Scope, first, last, months, observations));
    }

    private async Task<AccountReconciliationDto> ReconcileAsync(Guid workspaceId, IntegratedAccountFacts facts, DateOnly today, CancellationToken ct)
    {
        var current = facts.Anchors.FirstOrDefault(a => a.IsCurrent);
        var opening = await accounts.GetOpeningBalanceAsync(workspaceId, facts.AccountId, ct);
        var asOf = current?.AsOfDate ?? today;
        var hasOpening = opening is { Amount: not null, Date: not null };
        var ledgerFrom = hasOpening ? opening!.Date : null;
        var (calculated, calculatedReason) = await LedgerStartOfDayAsync(workspaceId, facts.AccountId, opening, asOf.AddDays(1), ct);
        var openingSource = !hasOpening ? "None" : opening!.Source ?? "Manual";

        var bank = current is null ? null : new BankReferenceDto(Cents(current.Amount), current.Kind, current.ObservedAt, current.AsOfDate, current.Source, current.Reliability, current.Reason,
            facts.OverdraftContractedLimit is null && facts.OverdraftUsedLimit is null ? null
                : new OverdraftDto(Fmt(facts.OverdraftContractedLimit), Fmt(facts.OverdraftUsedLimit), "O limite do cheque especial é crédito do banco: não compõe o saldo nem é dinheiro próprio."));
        var hasReference = current is not null && current.Reliability != "Unreliable" && current.Kind != "Unknown";
        var referenceReason = current is null ? "NoBankObservation" : current.Reliability == "Unreliable" ? current.Reason ?? "Unreliable" : current.Kind == "Unknown" ? "BalanceNatureUnknown" : null;
        decimal? difference = current is not null && calculated is { } c ? current.Amount - c : null;

        var review = facts.Posted.Where(m => m.InReview).ToList();
        var reviewNet = review.Sum(m => m.SignedAmount ?? 0m);
        var inRange = review.Where(m => ledgerFrom is { } lf && m.Date >= lf && m.Date <= asOf).ToList();
        var before = review.Count(m => ledgerFrom is null || m.Date < ledgerFrom);
        var bankSideFrom = BalanceReconciliation.BankSideFrom(facts.Posted);
        var quality = ledgerFrom is null ? "None" : before > 0 ? "LimitedByReview" : bankSideFrom is null ? "LimitedByBankData" : "Full";
        var (status, reason) = BalanceReconciliation.Classify(hasReference, referenceReason, difference, inRange.Sum(m => m.SignedAmount ?? 0m), before > 0, quality);

        var checkedCount = 0; var matching = 0;
        if (current is not null)
            foreach (var old in facts.Anchors.Where(a => !a.IsCurrent && a.AsOfDate < current.AsOfDate))
                if (BalanceReconciliation.BankStartOfDay(current, facts.Posted, old.AsOfDate.AddDays(1)).Amount is { } reconstructed)
                { checkedCount++; if (BalanceReconciliation.SameCents(reconstructed, old.Amount)) matching++; }

        var explained = difference is not null && inRange.Count > 0 ? Cents(inRange.Sum(m => m.SignedAmount ?? 0m)) : null;
        var unexplained = difference is { } diff && !BalanceReconciliation.SameCents(diff, 0m) ? Cents(diff - inRange.Sum(m => m.SignedAmount ?? 0m)) : null;
        return new(facts.AccountId, facts.Name, facts.Scope, facts.IsActive, bank, new(Fmt(calculated), asOf, openingSource, ledgerFrom, calculatedReason),
            Fmt(difference), status.ToString(), reason, explained, unexplained, facts.LastSyncedAt, facts.LinkStatus,
            new(facts.Posted.Count == 0 ? null : facts.Posted.Min(m => m.Date), facts.Posted.Count == 0 ? null : facts.Posted.Max(m => m.Date), bankSideFrom, ledgerFrom, quality, facts.Posted.Count,
                quality == "Full" ? null : quality),
            new(review.Count, Cents(reviewNet), review.Count == 0 ? null : review.Min(m => m.Date), review.Count == 0 ? null : review.Max(m => m.Date), before, inRange.Count, facts.PendingCount),
            new(checkedCount, matching, "Observações anteriores confirmadas pelo histórico importado. É evidência, não promove a referência sozinha."));
    }

    /// <summary>Saldo do Saldo360 no INÍCIO do dia: saldo inicial efetivo + movimentos anteriores. Antes do saldo inicial é indisponível, nunca zero.</summary>
    private async Task<(decimal? Amount, string? Reason)> LedgerStartOfDayAsync(Guid workspaceId, Guid accountId, AccountOpeningBalance? opening, DateOnly day, CancellationToken ct)
    {
        if (opening is not { Amount: { } amount, Date: { } date }) return (null, "OpeningBalanceNotConfigured");
        if (day < date) return (null, "BeforeCalculableStart");
        if (day == date) return (amount, null);
        var totals = await accounts.SumMovementsAsync(workspaceId, accountId, date, day.AddDays(-1), ct);
        return (FinancialBalanceCalculator.ApplyMovements(amount, totals.Income, totals.Expense, totals.TransferIn, totals.TransferOut), null);
    }

    private static ScopeSummaryDto Summarize(string scope, IReadOnlyList<AccountReconciliationDto> items)
    {
        var reasons = new List<string>();
        var complete = items.Count > 0;
        foreach (var item in items)
        {
            if (item.Bank is null || item.Calculated.Amount is null) { complete = false; reasons.Add($"{item.Name}: saldo indisponível"); }
            else if (item.Status is nameof(ReconciliationStatus.InsufficientBankReference)) { complete = false; reasons.Add($"{item.Name}: referência bancária insuficiente"); }
        }
        var usable = items.Where(i => i.Bank is not null && i.Calculated.Amount is not null).ToList();
        string? bankTotal = complete ? Cents(usable.Sum(i => Parse(i.Bank!.Amount))) : null;
        string? calcTotal = complete ? Cents(usable.Sum(i => Parse(i.Calculated.Amount!))) : null;
        string? diff = complete ? Cents(usable.Sum(i => Parse(i.Bank!.Amount)) - usable.Sum(i => Parse(i.Calculated.Amount!))) : null;
        return new(scope, items.Count, bankTotal, calcTotal, diff, complete, reasons);
    }

    /// <summary>Valores do lado do banco podem ter casas além dos centavos (a Pluggy informa até 4): são exibidos em centavos, sem alterar o que foi somado.</summary>
    private static string Cents(decimal value) => Money.Format(decimal.Round(value, Money.MaxScale, MidpointRounding.AwayFromZero));

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static string? Fmt(decimal? value) => value is { } v ? Cents(v) : null;
    private static bool TryMonth(string text, out DateOnly month) =>
        DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month) && text.Length == 7;
}

using System.Globalization;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards.Projection;

/// <summary>Lê, no máximo uma vez por consulta, os fatos dos cartões do orçamento. Somente leitura.</summary>
public interface ICardProjectionRepository
{
    Task<IReadOnlyList<CardFacts>> LoadAsync(Guid workspaceId, DateOnly today, Guid? cardId, CancellationToken cancellationToken);
}

public sealed record PayerDto(Guid AccountId, string Name, string? Scope, bool IsPrimary);
public sealed record SyncDto(string Status, DateTimeOffset? LastSyncedAt);
public sealed record ScheduleDto(int ClosingDay, int DueDay, string Source, bool Strong);
public sealed record CoverageDto(bool Complete, IReadOnlyList<string> Reasons);
public sealed record InvoiceComponentsDto(string Composition, string PendingKnown, string KnownInstallments, string Estimated);

/// <summary>
/// Uma fatura com as três dimensões separadas: <c>Cycle</c> (Open/Closed), <c>PaymentState</c> (Unpaid/Partial/Paid/Overpaid) e
/// <c>Reconciliation</c> (NotVerified/Reconciled/Divergent/Incomplete). <c>Obligation</c> é o valor a considerar: o total do banco quando fechada.
/// </summary>
public sealed record CardInvoiceViewDto(
    Guid? InvoiceId, string Competence, DateOnly CycleStart, DateOnly Closing, DateOnly Due, string Cycle, string Phase, string Basis, bool Provisional, bool AwaitingBankBill,
    string? BankTotal, string? BankTotalSource, string ProjectedTotal, string Obligation, string Paid, string Remaining, string PaymentState,
    string Reconciliation, string? ReconciliationReason, string? Difference, string? BankReportedPaid, InvoiceComponentsDto Components,
    IReadOnlyList<string> Flags, int AbsorbedItems, int UnprojectedInstallments);

public sealed record CardOverviewItemDto(
    Guid CardId, string Name, string Issuer, string? LastFour, string? Scope, string Origin, string CreditLimit, string AvailableLimit, string LimitSource,
    PayerDto? Payer, SyncDto Sync, ScheduleDto Schedule, CardInvoiceViewDto? CurrentInvoice, CardInvoiceViewDto? NextDue, string OverdueRemaining,
    IReadOnlyList<CardInvoiceViewDto> Invoices, CoverageDto Coverage);

public sealed record CardInvoiceOverviewDto(string Competence, DateOnly Today, IReadOnlyList<CardOverviewItemDto> Cards, string Obligation, string Paid, string Remaining, CoverageDto Coverage);

public sealed record MonthlyCardDto(Guid CardId, string Name, string? Scope, Guid? PayerAccountId, string Obligation, string Paid, string Remaining, string Confirmed, string Projected, string Estimated);
public sealed record MonthlyPayerDto(Guid? AccountId, string? Name, string? Scope, string Obligation, string Paid, string Remaining, IReadOnlyList<Guid> CardIds);

/// <summary>
/// Um mês da projeção. Os baldes não se sobrepõem: <c>Obligation</c> = confirmadas + abertas projetadas + parcelas futuras conhecidas + comprometimentos
/// estimados + obrigações conhecidas sem fatura. <c>EstimatedIncluded</c> é só informativo (inclui as estimadas dentro da fatura aberta).
/// </summary>
public sealed record MonthlyProjectionDto(
    string Month, string ConfirmedInvoices, string OpenInvoicesProjected, string FutureInstallments, string EstimatedCommitments, string KnownCommitments,
    string EstimatedIncluded, string Obligation, string Paid, string Remaining, int Cards, CoverageDto Coverage,
    IReadOnlyList<MonthlyCardDto> ByCard, IReadOnlyList<MonthlyPayerDto> ByPayer);

public sealed record CardObligationProjectionDto(string From, int Months, DateOnly Today, string Carryover, IReadOnlyList<MonthlyProjectionDto> Items, CoverageDto Coverage);

public sealed record ProjectedInstallmentDto(int Number, int Total, string Amount, DateOnly Closing, DateOnly Due, string Origin, bool Absorbed);
public sealed record InstallmentChainDto(string Key, string Description, int Total, string Amount, string Status, string? Reason, int Estimated, int Unprojected, IReadOnlyList<ProjectedInstallmentDto> Installments);
public sealed record CardInstallmentsDto(Guid CardId, string Name, DateOnly Today, IReadOnlyList<InstallmentChainDto> Chains, CoverageDto Coverage);

public enum CardProjectionAccess { Allowed, NotFound, Invalid }
public sealed record CardProjectionResult<T>(CardProjectionAccess Access, T? Value, IDictionary<string, string[]>? Errors) where T : class
{
    public static CardProjectionResult<T> NotFound { get; } = new(CardProjectionAccess.NotFound, null, null);
    public static CardProjectionResult<T> Ok(T value) => new(CardProjectionAccess.Allowed, value, null);
    public static CardProjectionResult<T> Invalid(string field, string message) => new(CardProjectionAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>Consultas de leitura sobre a projeção das obrigações dos cartões. Nenhum cálculo financeiro acontece no navegador.</summary>
public sealed class CardProjectionService(IFinancialWorkspaceRepository workspaces, ICardProjectionRepository repository, TimeProvider time)
{
    public const int MaxMonths = 12;

    public async Task<CardProjectionResult<CardInvoiceOverviewDto>> InvoiceOverviewAsync(Guid workspaceId, Guid userId, string? competence, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return CardProjectionResult<CardInvoiceOverviewDto>.NotFound;
        var today = BusinessCalendar.Today(time.GetUtcNow());
        var month = competence ?? today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (!TryMonth(month, out _)) return CardProjectionResult<CardInvoiceOverviewDto>.Invalid("competence", "Informe a competência no formato yyyy-MM.");
        // As telas listam só cartões ativos; as obrigações de cartões arquivados seguem na projeção e nas somas da Agenda.
        var projections = (await ComputeAsync(workspaceId, today, null, ct)).Where(p => !p.Card.IsArchived).ToList();

        var cards = new List<CardOverviewItemDto>();
        foreach (var projection in projections)
        {
            var invoices = projection.Invoices.Where(i => i.Competence == month).Select(i => View(i, projection)).ToList();
            var current = projection.Invoices.Where(i => i.Phase == InvoicePhase.Open).OrderBy(i => i.Closing).Select(i => View(i, projection)).FirstOrDefault();
            var payable = projection.Invoices.Where(i => i.Remaining > 0m).OrderBy(i => i.Due).ToList();
            var next = payable.Where(i => i.Due >= today).Select(i => View(i, projection)).FirstOrDefault();
            var overdue = payable.Where(i => i.Due < today).Sum(i => i.Remaining);
            var card = projection.Card;
            cards.Add(new(card.Id, card.Name, card.Issuer, card.LastFour, card.Scope, card.Origin, Money.Format(card.CreditLimit), Money.Format(card.AvailableLimit), card.LimitSource,
                Payer(card), new(card.SyncStatus, card.LastSyncedAt), new(projection.Schedule.ClosingDay, projection.Schedule.DueDay, projection.Schedule.Source, projection.Schedule.Strong),
                current, next, Money.Format(overdue), invoices, new(projection.Coverage.Complete, projection.Coverage.Reasons)));
        }
        var inMonth = projections.SelectMany(p => p.Invoices.Where(i => i.Competence == month)).ToList();
        var reasons = projections.SelectMany(p => p.Coverage.Reasons).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        return CardProjectionResult<CardInvoiceOverviewDto>.Ok(new(month, today, cards, Money.Format(inMonth.Sum(i => i.Obligation)), Money.Format(inMonth.Sum(i => i.Paid)),
            Money.Format(inMonth.Sum(i => i.Remaining)), new(reasons.Count == 0, reasons)));
    }

    public async Task<CardProjectionResult<CardObligationProjectionDto>> ObligationProjectionAsync(Guid workspaceId, Guid userId, string? from, int? months, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return CardProjectionResult<CardObligationProjectionDto>.NotFound;
        var today = BusinessCalendar.Today(time.GetUtcNow());
        var start = from ?? today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (!TryMonth(start, out var first)) return CardProjectionResult<CardObligationProjectionDto>.Invalid("from", "Informe o mês inicial no formato yyyy-MM.");
        var count = months ?? 6;
        if (count is < 1 or > MaxMonths) return CardProjectionResult<CardObligationProjectionDto>.Invalid("months", $"Informe de 1 a {MaxMonths} meses.");
        var projections = await ComputeAsync(workspaceId, today, null, ct);
        var items = Enumerable.Range(0, count).Select(offset => Month(first.AddMonths(offset), projections)).ToList();
        // Faturas vencidas antes do primeiro mês e ainda não pagas: nada some só porque saiu da janela.
        var carryover = projections.SelectMany(p => p.Invoices).Where(i => i.Due < first && i.Remaining > 0m).Sum(i => i.Remaining)
            + projections.SelectMany(p => p.Items).Where(i => i.Kind == ObligationKind.KnownImported && i.DueDate is { } due && due < first).Sum(i => i.Remaining);
        var reasons = items.SelectMany(i => i.Coverage.Reasons).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        return CardProjectionResult<CardObligationProjectionDto>.Ok(new(start, count, today, Money.Format(carryover), items, new(reasons.Count == 0, reasons)));
    }

    public async Task<CardProjectionResult<CardInstallmentsDto>> InstallmentsAsync(Guid workspaceId, Guid userId, Guid cardId, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return CardProjectionResult<CardInstallmentsDto>.NotFound;
        var today = BusinessCalendar.Today(time.GetUtcNow());
        var projection = (await ComputeAsync(workspaceId, today, cardId, ct)).SingleOrDefault();
        if (projection is null) return CardProjectionResult<CardInstallmentsDto>.NotFound;
        var chains = projection.Chains.Select(c => new InstallmentChainDto(c.Key, c.Description, c.Total, Money.Format(c.Amount), c.Status.ToString(), c.Reason, c.Estimated, c.Unprojected,
            c.Installments.Select(i => new ProjectedInstallmentDto(i.Number, i.Total, Money.Format(i.Amount), i.Closing, i.Due, i.Origin.ToString(), i.Absorbed)).ToList())).ToList();
        return CardProjectionResult<CardInstallmentsDto>.Ok(new(cardId, projection.Card.Name, today, chains, new(projection.Coverage.Complete, projection.Coverage.Reasons)));
    }

    /// <summary>Fonte única para os consumidores internos (Agenda, saldo projetado): mesmos fatos, mesmo motor, mesma regra.</summary>
    public async Task<IReadOnlyList<CardProjection>> ComputeAsync(Guid workspaceId, DateOnly today, Guid? cardId, CancellationToken ct) =>
        (await repository.LoadAsync(workspaceId, today, cardId, ct)).Select(facts => CardProjectionEngine.Compute(facts, today)).ToList();

    private static MonthlyProjectionDto Month(DateOnly month, IReadOnlyList<CardProjection> projections)
    {
        var key = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var byCard = new List<MonthlyCardDto>();
        var payerRows = new List<(CardProjection P, decimal Obligation, decimal Paid, decimal Remaining)>();
        decimal confirmed = 0, open = 0, future = 0, estimatedFuture = 0, known = 0, estimatedAll = 0, obligation = 0, paid = 0, remaining = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in projections)
        {
            var invoices = p.Invoices.Where(i => i.Competence == key).ToList();
            var knownHere = p.Items.Where(i => i.Kind == ObligationKind.KnownImported && i.DueDate?.ToString("yyyy-MM", CultureInfo.InvariantCulture) == key).Sum(i => i.Remaining);
            var cConfirmed = invoices.Where(i => i.Phase == InvoicePhase.Closed).Sum(i => i.Obligation);
            var cOpen = invoices.Where(i => i.Phase == InvoicePhase.Open).Sum(i => i.Obligation);
            var cFuture = invoices.Where(i => i.Phase == InvoicePhase.Future).Sum(i => i.Composition + i.PendingKnown + i.KnownInstallments);
            var cEstimatedFuture = invoices.Where(i => i.Phase == InvoicePhase.Future).Sum(i => i.Estimated);
            var cEstimatedAll = invoices.Where(i => i.Phase != InvoicePhase.Closed).Sum(i => i.Estimated);
            var cObligation = cConfirmed + cOpen + cFuture + cEstimatedFuture + knownHere;
            var cPaid = invoices.Sum(i => i.Paid);
            var cRemaining = invoices.Sum(i => i.Remaining) + knownHere;
            confirmed += cConfirmed; open += cOpen; future += cFuture; estimatedFuture += cEstimatedFuture; known += knownHere; estimatedAll += cEstimatedAll;
            obligation += cObligation; paid += cPaid; remaining += cRemaining;
            foreach (var reason in p.Coverage.Reasons) reasons.Add(reason);
            foreach (var flag in invoices.SelectMany(i => i.Flags)) reasons.Add(flag);
            byCard.Add(new(p.Card.Id, p.Card.Name, p.Card.Scope, p.Card.PayerAccountId, Money.Format(cObligation), Money.Format(cPaid), Money.Format(cRemaining),
                Money.Format(cConfirmed), Money.Format(cOpen + cFuture + knownHere), Money.Format(cEstimatedAll)));
            payerRows.Add((p, cObligation, cPaid, cRemaining));
        }
        var byPayer = payerRows.GroupBy(r => r.P.Card.PayerAccountId).Select(g =>
        {
            var first = g.First().P.Card;
            return new MonthlyPayerDto(g.Key, first.PayerName, first.PayerScope, Money.Format(g.Sum(r => r.Obligation)), Money.Format(g.Sum(r => r.Paid)), Money.Format(g.Sum(r => r.Remaining)), g.Select(r => r.P.Card.Id).ToList());
        }).OrderBy(r => r.Name ?? "~", StringComparer.CurrentCultureIgnoreCase).ToList();
        var ordered = reasons.OrderBy(r => r, StringComparer.Ordinal).ToList();
        return new(key, Money.Format(confirmed), Money.Format(open), Money.Format(future), Money.Format(estimatedFuture), Money.Format(known), Money.Format(estimatedAll),
            Money.Format(obligation), Money.Format(paid), Money.Format(remaining), projections.Count(p => !p.Card.IsArchived), new(ordered.Count == 0, ordered), byCard, byPayer);
    }

    private static PayerDto? Payer(CardInfo card) => card.PayerAccountId is { } id ? new(id, card.PayerName ?? string.Empty, card.PayerScope, card.PayerIsPrimary) : null;

    private static CardInvoiceViewDto View(CardInvoiceProjection i, CardProjection projection) => new(
        i.InvoiceId, i.Competence, i.CycleStart, i.Closing, i.Due, i.Phase == InvoicePhase.Closed ? "Closed" : "Open", i.Phase.ToString(), i.Basis.ToString(), i.Provisional, i.AwaitingBankBill,
        i.BankTotal is { } bank ? Money.Format(bank) : null, i.BankTotalSource, Money.Format(i.ProjectedTotal), Money.Format(i.Obligation), Money.Format(i.Paid), Money.Format(i.Remaining), i.PaymentState.ToString(),
        i.Reconciliation, i.ReconciliationReason, i.Difference is { } difference ? Money.Format(difference) : null, i.BankReportedPaid is { } reported ? Money.Format(reported) : null,
        new(Money.Format(i.Composition), Money.Format(i.PendingKnown), Money.Format(i.KnownInstallments), Money.Format(i.Estimated)), i.Flags, i.AbsorbedItems, i.UnprojectedInstallments);

    private static bool TryMonth(string text, out DateOnly month) =>
        DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month) && text.Length == 7;

    private async Task<bool> IsParticipantAsync(Guid workspaceId, Guid userId, CancellationToken ct) => await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is not null;
}

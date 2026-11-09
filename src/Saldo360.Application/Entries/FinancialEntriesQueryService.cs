using System.Globalization;
using Saldo360.Application.Accounts;
using Saldo360.Application.Budgets;
using Saldo360.Application.Calendar;
using Saldo360.Application.Categories;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Recurrences;
using Saldo360.Application.CreditCards;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Transactions;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transactions;

namespace Saldo360.Application.Entries;

// T13.1: leitura unificada dos lançamentos de uma competência. Nada é gravado nem duplicado: cada fonte continua sendo a dona dos seus dados e esta consulta só os reúne em UMA linha por movimento lógico.
//   transações efetivas (inclui importações, devoluções, pagamentos de fatura e de dívida e as pontas das transferências)  → FinancialTransactionService
//   previsões pendentes, recorrências, parcelas de dívida e transferências previstas                                  → FinancialCalendarService (que já resolve realizada × pendente)
//   compras, parcelas e ajustes de cartão pela competência da fatura                                                  → fatos do orçamento (BudgetFactReader)
// Estimativas agregadas sem movimento individual não viram lançamentos.

public sealed record EntryReference(Guid? Id, string Name, string? Scope = null);

public sealed record EntryCategory(Guid? Id, string Name, string? Path);

/// <summary>
/// Um movimento lógico. <c>Source</c>: Transaction, Planned, Recurrence, Debt, Transfer, CardPurchase ou CardAdjustment. <c>Type</c>: Income, Expense, Transfer ou Payment (fatura ou dívida).
/// <c>Direction</c>: In, Out ou Neutral (transferência no consolidado). <c>Status</c>: Realized, Pending, Overdue, NeedsReview ou Cancelled. <c>DateKind</c>: Effective, Due, Invoice (vencimento da fatura) ou Execution.
/// <c>BudgetImpact</c>: Consumes, Reduces, Planned ou None. <c>CashImpact</c>: Immediate, AtInvoicePayment, Pending ou None. <c>Flags</c> são ressalvas de confiabilidade e <c>Actions</c> o que o registro permite (o backend de cada ação revalida).
/// </summary>
public sealed record FinancialEntryDto(
    string Id, string Source, string Competence, DateOnly Date, string DateKind, string Description, string Type, string Direction, string Status, string Amount,
    EntryReference? Account, EntryReference? Counterpart, EntryReference? Card, EntryCategory? Category, string? Nature, string Origin, string BudgetImpact, string CashImpact,
    Guid? TransactionId, Guid? PlannedTransactionId, Guid? TransferId, Guid? PlannedTransferId, Guid? RecurrenceId, string? InvoiceCompetence,
    IReadOnlyList<string> Actions, IReadOnlyList<string> Flags, string Stage = "", InstallmentInfo? Installment = null,
    /// <summary>Grupo ao qual a linha pertence quando a consulta pede agrupamento (T13.4); nulo sem agrupamento.</summary>
    string? GroupKey = null);

/// <summary>Rastreabilidade da parcela: a descrição original do banco, a numeração que ele escreveu e a usada pelo motor. <c>Inconsistent</c> = divergem (nada é renumerado).</summary>
public sealed record InstallmentInfo(string OriginalDescription, int? Number, int? Total, int? OriginalNumber, int? OriginalTotal, bool Inconsistent);

/// <summary>
/// Página de lançamentos. <c>Groups</c> (só com agrupamento) traz o resumo de CADA grupo sobre todos os lançamentos filtrados, na ordem em que os grupos aparecem, com a página inicial de cada um;
/// <c>Totals</c> resume o conjunto filtrado inteiro. Ambos independem da página pedida.
/// </summary>
public sealed record FinancialEntriesPage(IReadOnlyList<FinancialEntryDto> Items, int Page, int PageSize, int TotalCount, string Competence,
    IReadOnlyList<EntryGroupSummary>? Groups = null, EntryGroupSummary? Totals = null, string GroupBy = "none");

public sealed record FinancialEntriesQuery(
    string? Competence, string? Scope, Guid? AccountId, Guid? CategoryId, string? Type, string? Status, string? Source, string? Nature, string? Search, bool IncludeCancelled,
    string? Sort, string? Direction, int? Page, int? PageSize, Guid? CardId = null, bool WithoutCard = false, string? GroupBy = null);

public sealed record FinancialEntriesResult(FinancialEntriesPage? Page, IDictionary<string, string[]>? Errors, bool NotFound)
{
    public static FinancialEntriesResult NotFoundResult { get; } = new(null, null, true);
}

public sealed class FinancialEntriesQueryService(
    IFinancialWorkspaceRepository workspaces,
    FinancialTransactionService transactions,
    FinancialCalendarService calendar,
    FinancialAccountService accounts,
    IFinancialBudgetExecutionRepository budgetFacts,
    IFinancialCategoryRepository categoryRepository,
    IFinancialCreditCardRepository cardRepository,
    CardProjectionService cardProjection,
    TimeProvider time)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    private const int MaxTransactionPages = 30;
    private const int OverdueLookbackMonths = 12;

    private static readonly string[] Scopes = ["all", "pf", "pj"];
    private static readonly string[] Types = ["Income", "Expense", "Transfer", "Payment"];
    private static readonly string[] Statuses = ["Realized", "Pending", "Overdue", "NeedsReview", "Cancelled", "OverduePrevious"];
    private static readonly string[] Sources = ["Transaction", "Planned", "Recurrence", "Debt", "Transfer", "CardPurchase", "CardAdjustment", "CardProjected"];
    private static readonly string[] Sorts = ["date", "amount", "description"];

    public async Task<FinancialEntriesResult> ListAsync(Guid workspaceId, Guid userId, FinancialEntriesQuery query, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return FinancialEntriesResult.NotFoundResult;
        var today = BusinessCalendar.Today(time.GetUtcNow());
        var errors = new Dictionary<string, string[]>();
        var competence = string.IsNullOrWhiteSpace(query.Competence) ? $"{today.Year:0000}-{today.Month:00}" : query.Competence.Trim();
        if (!DateOnly.TryParseExact(competence + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first) || first.Year is < 2000 or > 2100) errors["competence"] = ["Use uma competência válida no formato yyyy-MM."];
        var scope = string.IsNullOrWhiteSpace(query.Scope) ? "all" : query.Scope.Trim().ToLowerInvariant();
        if (!Scopes.Contains(scope)) errors["scope"] = ["Use all, pf ou pj."];
        string? Check(string field, string? value, string[] allowed) { if (string.IsNullOrWhiteSpace(value)) return null; if (!allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)) { errors[field] = [$"Valor inválido: use {string.Join(", ", allowed)}."]; return null; } return allowed.First(a => a.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)); }
        var type = Check("type", query.Type, Types); var status = Check("status", query.Status, Statuses); var source = Check("source", query.Source, Sources);
        var nature = Check("nature", query.Nature, ["Fixed", "Variable", "Undefined"]); var sort = Check("sort", query.Sort, Sorts) ?? "date";
        var descending = !string.Equals(query.Direction, "asc", StringComparison.OrdinalIgnoreCase);
        var groupBy = string.IsNullOrWhiteSpace(query.GroupBy) ? FinancialEntryGrouping.ByNone : query.GroupBy.Trim().ToLowerInvariant();
        if (!FinancialEntryGrouping.Modes.Contains(groupBy)) errors["groupBy"] = ["Use card, account ou none."];
        if (query.CardId is not null && query.WithoutCard) errors["cardId"] = ["Escolha um cartão ou \"sem cartão\", não os dois."];
        var page = query.Page ?? 1; var pageSize = query.PageSize ?? DefaultPageSize;
        if (page < 1) errors["page"] = ["A página deve ser maior que zero."];
        if (pageSize is < 1 or > MaxPageSize) errors["pageSize"] = [$"O tamanho da página deve estar entre 1 e {MaxPageSize}."];
        if (errors.Count > 0) return new(null, errors, false);

        var last = first.AddMonths(1).AddDays(-1);
        var accountList = (await accounts.ListAsync(workspaceId, userId, includeInactive: true, ct)) ?? [];
        var scopeOf = accountList.ToDictionary(a => a.Id, a => a.FinancialScope.ToString());
        var categories = (await categoryRepository.ListAsync(workspaceId, includeInactive: true, ct)).ToDictionary(c => c.Id);
        var cards = (await cardRepository.ListAsync(workspaceId, includeArchived: true, ct)).ToDictionary(c => c.Id);
        if (query.CardId is { } requestedCard && !cards.ContainsKey(requestedCard)) return new(null, new Dictionary<string, string[]> { ["cardId"] = ["Cartão não encontrado."] }, false);

        var entries = new List<FinancialEntryDto>();
        var previous = string.Equals(status, "OverduePrevious", StringComparison.Ordinal);
        if (!previous)
        {
            entries.AddRange(await TransactionEntriesAsync(workspaceId, userId, first, last, query with { IncludeCancelled = query.IncludeCancelled || status == "Cancelled" }, scopeOf, ct));
            var projections = await cardProjection.ComputeAsync(workspaceId, today, null, ct);
            var accountNames = accountList.ToDictionary(a => a.Id, a => a.Name);
            entries.AddRange(ProjectedCardEntries(projections, competence, categories, cards, scopeOf, accountNames));
            entries.AddRange(FinancingEntries(await budgetFacts.ReadInvoiceFinancingAsync(workspaceId, first.AddMonths(-1), first.AddMonths(2), ct), competence, categories, cards, scopeOf, accountList.ToDictionary(a => a.Id, a => a.Name)));
            entries.AddRange(CardEntries(await budgetFacts.ReadFactsAsync(workspaceId, first.AddMonths(-1), first.AddMonths(2), ct), competence, categories, cards, scopeOf, accountList.ToDictionary(a => a.Id, a => a.Name)));
        }
        entries.AddRange(await CalendarEntriesAsync(workspaceId, userId, previous ? first.AddMonths(-OverdueLookbackMonths) : first, previous ? first.AddDays(-1) : last, query.AccountId, previous, scopeOf, ct));

        entries = [.. FinancialEntryPrecedence.Resolve(entries)];

        bool InScope(FinancialEntryDto e) => scope == "all" || (e.Account?.Scope?.Equals(scope, StringComparison.OrdinalIgnoreCase) == true) || (e.Counterpart?.Scope?.Equals(scope, StringComparison.OrdinalIgnoreCase) == true);
        bool CategoryMatches(FinancialEntryDto e) => query.CategoryId is not { } id || e.Category?.Id == id || (e.Category?.Id is { } c && categories.TryGetValue(c, out var cat) && cat.ParentCategoryId == id);
        bool Search(FinancialEntryDto e) => string.IsNullOrWhiteSpace(query.Search) || new[] { e.Description, e.Account?.Name, e.Counterpart?.Name, e.Card?.Name, e.Category?.Path ?? e.Category?.Name }
            .Any(text => text?.Contains(query.Search.Trim(), StringComparison.CurrentCultureIgnoreCase) == true);
        var filtered = entries.Where(InScope).Where(CategoryMatches).Where(Search)
            .Where(e => query.AccountId is not { } a || e.Account?.Id == a || e.Counterpart?.Id == a || (e.Card is not null && e.Account?.Id == a))
            .Where(e => query.CardId is not { } c || e.Card?.Id == c)
            .Where(e => !query.WithoutCard || e.Card?.Id is null)
            .Where(e => type is null || e.Type == type)
            .Where(e => source is null || e.Source == source)
            .Where(e => nature is null || e.Nature == nature)
            .Where(e => status is null || previous || e.Status == status)
            .Where(e => e.Status != "Cancelled" || query.IncludeCancelled || status == "Cancelled")
            .ToList();

        Func<FinancialEntryDto, object> key = sort switch
        {
            "amount" => e => decimal.Parse(e.Amount, CultureInfo.InvariantCulture),
            "description" => e => e.Description.ToLowerInvariant(),
            _ => e => e.Date,
        };
        // Agrupa de forma determinística ANTES de paginar: grupos em ordem estável, e dentro de cada um a ordenação pedida. Sem agrupamento a ordem é a de sempre.
        var keyed = filtered.Select(e => (Entry: e, Group: FinancialEntryGrouping.KeyOf(e, groupBy))).ToList();
        var groupOrder = FinancialEntryGrouping.GroupOrder(keyed.Where(k => k.Group is not null).Select(k => k.Group!));
        int GroupIndex((FinancialEntryDto Entry, EntryGroupKey? Group) k) => k.Group is null ? 0 : groupOrder[k.Group.Key];
        var sorted = (descending ? keyed.OrderBy(GroupIndex).ThenByDescending(k => key(k.Entry)) : keyed.OrderBy(GroupIndex).ThenBy(k => key(k.Entry)))
            .ThenBy(k => k.Entry.Id, StringComparer.Ordinal).ToList();

        // Resumos sobre TODOS os lançamentos filtrados (nunca só a página), depois da precedência e dos filtros.
        IReadOnlyList<EntryGroupSummary> groups = groupBy == FinancialEntryGrouping.ByNone ? [] : sorted.Select((k, index) => (k, index)).GroupBy(x => x.k.Group!.Key, StringComparer.Ordinal)
            .Select(g => FinancialEntryGrouping.Summarize(g.First().k.Group!, g.Select(x => x.k.Entry).ToList(), g.First().index, pageSize)).ToList();
        var totals = FinancialEntryGrouping.Summarize(new("all", "All", null, "Todos os lançamentos filtrados", null, 0), filtered, 0, pageSize);
        var pageItems = sorted.Skip((page - 1) * pageSize).Take(pageSize).Select(k => k.Group is null ? k.Entry : k.Entry with { GroupKey = k.Group.Key }).ToList();
        return new(new(pageItems, page, pageSize, sorted.Count, competence, groups, totals, groupBy), null, false);
    }

    // ---- Transações efetivas e transferências realizadas ---------------------------------------------------------------------------------------------------------------------------------------

    private async Task<List<FinancialEntryDto>> TransactionEntriesAsync(Guid workspaceId, Guid userId, DateOnly first, DateOnly last, FinancialEntriesQuery query, IReadOnlyDictionary<Guid, string> scopeOf, CancellationToken ct)
    {
        var all = new List<FinancialTransactionDto>();
        for (var page = 1; page <= MaxTransactionPages; page++)
        {
            var result = await transactions.ListAsync(workspaceId, userId, new(first, last, query.AccountId, null, null, query.IncludeCancelled, page, FinancialTransactionService.MaxPageSize, IncludeTransfers: true), ct);
            if (result.Page is null) break;
            all.AddRange(result.Page.Items);
            if (all.Count >= result.Page.TotalCount || result.Page.Items.Count == 0) break;
        }
        EntryReference Ref(TransactionReferenceDto a) => new(a.Id, a.Name, scopeOf.GetValueOrDefault(a.Id));
        var list = new List<FinancialEntryDto>();

        // Uma linha por transferência: as duas pontas (consolidado) viram origem e destino.
        foreach (var group in all.Where(t => t.TransferId is not null).GroupBy(t => t.TransferId!.Value))
        {
            var @out = group.FirstOrDefault(t => t.Kind == "TransferOut"); var @in = group.FirstOrDefault(t => t.Kind == "TransferIn"); var any = group.First();
            var both = @out is not null && @in is not null;
            var account = both ? Ref(@out!.Account) : Ref(any.Account);
            var counterpart = both ? Ref(@in!.Account) : any.CounterpartAccountName is { } name ? new EntryReference(null, name) : null;
            var direction = both ? "Neutral" : any.Kind == "TransferIn" ? "In" : "Out";
            // Cada ponta traz o texto do seu sentido ("recebida de…" / "enviada para…") seguido da descrição do usuário: a linha lógica diz origem e destino e preserva a descrição.
            var userText = any.Description.Contains(" · ", StringComparison.Ordinal) ? any.Description[(any.Description.IndexOf(" · ", StringComparison.Ordinal) + 3)..] : null;
            var description = both ? $"Transferência de {@out!.Account.Name} para {@in!.Account.Name}{(userText is null ? "" : " · " + userText)}" : any.Description;
            list.Add(new($"Transfer:{group.Key}", "Transfer", $"{any.TransactionDate.Year:0000}-{any.TransactionDate.Month:00}", any.TransactionDate, "Execution", description, "Transfer", direction, any.IsCancelled ? "Cancelled" : "Realized",
                Money.Format(any.Amount), account, counterpart, null, null, null, "Transfer", "None", "Immediate", null, null, group.Key, null, null, null, ["ViewDetails", "OpenTransfers"], []));
        }

        foreach (var t in all.Where(t => t.TransferId is null))
        {
            var invoicePayment = t.Origin == FinancialTransactionOrigin.CreditCardInvoicePayment; var debtPayment = t.Origin == FinancialTransactionOrigin.DebtInstallmentPayment;
            var refund = t.Origin == FinancialTransactionOrigin.PluggyRefund; var imported = t.Origin == FinancialTransactionOrigin.PluggyImport;
            var entryType = invoicePayment || debtPayment ? "Payment" : t.Type == CategoryType.Income ? "Income" : "Expense";
            var actions = new List<string> { "ViewDetails" };
            if (invoicePayment) actions.Add("OpenCards"); else if (debtPayment) actions.Add("OpenDebts");
            else if (!t.IsCancelled)
            {
                if (imported) actions.Add("Recategorize");
                if (t.Type == CategoryType.Expense && (imported || t.PlannedTransactionId is not null)) actions.Add("ChangeNature");
                if (refund && t.RefundPostingId is not null) actions.Add("UndoRefund");
                if (t.PlannedTransactionId is null && !imported && !refund) actions.Add("Edit");
                actions.Add("Cancel");
            }
            var flags = new List<string>();
            if (imported) flags.Add("Imported"); if (refund) flags.Add("Refund"); if (t.PlannedTransactionId is not null) flags.Add("RealizesPlanned"); if (t.CategoryNeedsReview && t.Category is null) flags.Add("CategoryNeedsReview");
            if (t.RefundedAmount > 0m) flags.Add("PartiallyRefunded");
            list.Add(new($"Transaction:{t.Id}", "Transaction", $"{t.TransactionDate.Year:0000}-{t.TransactionDate.Month:00}", t.TransactionDate, "Effective", t.Description, entryType, entryType == "Income" ? "In" : "Out", t.IsCancelled ? "Cancelled" : "Realized",
                Money.Format(t.Amount), Ref(t.Account), null, null, t.Category is { } c ? new(c.Id, c.Name, c.Path) : null, t.Type == CategoryType.Expense ? t.Nature.ToString() : null, t.Origin.ToString(),
                invoicePayment ? "None" : refund ? "Reduces" : t.Type == CategoryType.Expense ? "Consumes" : "None", "Immediate", t.Id, t.PlannedTransactionId, null, null, null, null, actions, flags));
        }
        return list;
    }

    // ---- Previsões, recorrências, dívidas e transferências previstas ---------------------------------------------------------------------------------------------------------------------------------

    private async Task<List<FinancialEntryDto>> CalendarEntriesAsync(Guid workspaceId, Guid userId, DateOnly from, DateOnly through, Guid? accountId, bool overdueOnly, IReadOnlyDictionary<Guid, string> scopeOf, CancellationToken ct)
    {
        var result = await calendar.ListAsync(workspaceId, userId, new(from, through, accountId, null, IncludeTransfers: true), ct);
        var items = result.Calendar?.Items ?? [];
        EntryReference? Ref(TransactionReferenceDto? a) => a is null ? null : new(a.Id, a.Name, scopeOf.GetValueOrDefault(a.Id));
        string Status(FinancialCalendarStatus s) => s switch { FinancialCalendarStatus.Overdue => "Overdue", FinancialCalendarStatus.NeedsReview => "NeedsReview", FinancialCalendarStatus.Realized => "Realized", _ => "Pending" };
        var list = new List<FinancialEntryDto>();

        // Só o que o calendário tem além das transações efetivas: previsões pendentes, recorrências, parcelas de dívida e transferências previstas (uma linha por transferência).
        foreach (var group in items.Where(i => i.Source == FinancialCalendarSource.Transfer && i.PlannedTransferId is not null).GroupBy(i => i.PlannedTransferId!.Value))
        {
            if (overdueOnly && group.First().Status != FinancialCalendarStatus.Overdue) continue;
            var @out = group.FirstOrDefault(i => i.Type == CategoryType.Expense); var @in = group.FirstOrDefault(i => i.Type == CategoryType.Income); var any = group.First(); var both = @out is not null && @in is not null;
            list.Add(new($"PlannedTransfer:{group.Key}", "Transfer", $"{any.Date.Year:0000}-{any.Date.Month:00}", any.Date, "Due", any.Description, "Transfer", both ? "Neutral" : any.Type == CategoryType.Income ? "In" : "Out", Status(any.Status), Money.Format(any.Amount),
                Ref(both ? @out!.Account : any.Account), both ? Ref(@in!.Account) : null, null, null, null, "PlannedTransfer", "None", "Pending", null, null, null, group.Key, null, null,
                [.. new[] { "ViewDetails", "OpenTransfers" }, .. any.AllowedActions.Where(a => a == "CancelPlannedTransfer")], ["Planned"]));
        }
        foreach (var i in items.Where(i => i.Source is FinancialCalendarSource.PlannedTransaction or FinancialCalendarSource.RecurrenceOccurrence or FinancialCalendarSource.DebtInstallment))
        {
            if (overdueOnly && i.Status != FinancialCalendarStatus.Overdue) continue;
            var (source, idPrefix) = i.Source switch { FinancialCalendarSource.PlannedTransaction => ("Planned", "Planned"), FinancialCalendarSource.RecurrenceOccurrence => ("Recurrence", "Recurrence"), _ => ("Debt", "Debt") };
            var actions = new List<string> { "ViewDetails" }; actions.AddRange(i.AllowedActions);
            if (source == "Debt") actions.Add("OpenDebts"); if (source == "Recurrence") actions.Add("OpenRecurrences");
            var flags = new List<string> { "Planned" }; if (i.Status == FinancialCalendarStatus.NeedsReview) flags.Add("NoPaymentEvidence"); if (i.AmountSource is { } amountSource) flags.Add("Amount:" + amountSource);
            var stable = i.RecurrenceId is { } r && i.OccurrenceNumber is { } n ? $"{idPrefix}:{r}:{n}" : $"{idPrefix}:{i.Id}";
            list.Add(new(stable, source, $"{i.Date.Year:0000}-{i.Date.Month:00}", i.Date, "Due", i.Description, i.Type == CategoryType.Income ? "Income" : "Expense", i.Type == CategoryType.Income ? "In" : "Out", Status(i.Status), Money.Format(i.Amount),
                Ref(i.Account), null, null, i.Category is { } c ? new(c.Id, c.Name, c.Path) : null, null, i.Origin.ToString(), i.Type == CategoryType.Expense ? "Planned" : "None", "Pending", null, i.PlannedTransactionId ?? (source == "Planned" ? i.Id : null), null, null,
                i.RecurrenceId, null, actions, flags));
        }
        return list;
    }

    // ---- Cartões ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static IEnumerable<FinancialEntryDto> CardEntries((IReadOnlyList<BudgetFact> Facts, IReadOnlyDictionary<Guid, string> Paths) read, string competence, IReadOnlyDictionary<Guid, FinancialCategoryDto> categories,
        IReadOnlyDictionary<Guid, FinancialCreditCardDto> cards, IReadOnlyDictionary<Guid, string> scopeOf, IReadOnlyDictionary<Guid, string> accountNames)
    {
        foreach (var f in read.Facts.Where(f => f.CardId is not null && f.Origin is BudgetFactOrigin.CardInstallment or BudgetFactOrigin.CardAdjustment or BudgetFactOrigin.CardKnownInstallment && (f.InvoiceCompetence ?? f.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture)) == competence))
        {
            var card = cards.GetValueOrDefault(f.CardId!.Value);
            var payer = card?.DefaultPaymentAccountId; var credit = f.Amount < 0m;
            var category = f.CategoryId is { } id && categories.TryGetValue(id, out var cat) ? new EntryCategory(id, cat.Name, read.Paths.GetValueOrDefault(id) ?? cat.Name) : null;
            var flags = new List<string>(); if (!f.CompetenceReliable) flags.Add("CompetenceEstimated"); if (payer is null) flags.Add("NoPayerAccount"); if (f.Imported) flags.Add("Imported"); if (f.Origin == BudgetFactOrigin.CardKnownInstallment) flags.Add("KnownImported");
            var cardRef = new EntryReference(f.CardId, f.CardName ?? card?.Name ?? "Cartão");
            var label = f.Origin == BudgetFactOrigin.CardInstallment ? InstallmentLabel.Build(f.Description, null, null) : null;
            if (label is not { Number: not null }) label = null;
            if (label is { Inconsistent: true }) flags.Add("InstallmentInconsistent");
            yield return new($"{(f.Origin == BudgetFactOrigin.CardAdjustment ? "CardAdjustment" : "CardInstallment")}:{f.RecordId}", f.Origin == BudgetFactOrigin.CardAdjustment ? "CardAdjustment" : "CardPurchase", competence, f.Date, "Invoice", label?.Display ?? f.Description,
                credit ? "Income" : "Expense", credit ? "In" : "Out", "Realized", Money.Format(Math.Abs(f.Amount)), payer is { } p ? new(p, accountNames.GetValueOrDefault(p) ?? "Conta", scopeOf.GetValueOrDefault(p)) : null, null, cardRef, category,
                f.Nature.ToString(), f.Origin.ToString(), credit ? "Reduces" : "Consumes", "AtInvoicePayment", null, null, null, null, null, competence, ["ViewDetails", "OpenCards"], flags,
                Installment: label is null ? null : new(label.OriginalDescription, label.Number, label.Total, label.OriginalNumber, label.OriginalTotal, label.Inconsistent));
        }
    }

    /// <summary>
    /// Parcela de financiamento de saldo de fatura já lançada: aparece em Lançamentos como obrigação da fatura (caixa), sem consumir orçamento — o principal já foi contabilizado quando a fatura original foi lançada.
    /// A composição entre principal e encargos não é discriminada pelo banco; fica sinalizada, nunca presumida como zero.
    /// </summary>
    private static IEnumerable<FinancialEntryDto> FinancingEntries(IReadOnlyList<InvoiceFinancingLine> lines, string competence, IReadOnlyDictionary<Guid, FinancialCategoryDto> categories,
        IReadOnlyDictionary<Guid, FinancialCreditCardDto> cards, IReadOnlyDictionary<Guid, string> scopeOf, IReadOnlyDictionary<Guid, string> accountNames)
    {
        foreach (var l in lines.Where(l => l.Competence == competence))
        {
            var payer = cards.GetValueOrDefault(l.CardId)?.DefaultPaymentAccountId;
            var label = InstallmentLabel.Build(l.Description, null, null);
            var flags = new List<string> { "PrincipalInterestUnknown" }; if (payer is null) flags.Add("NoPayerAccount");
            if (label is { Inconsistent: true }) flags.Add("InstallmentInconsistent");
            yield return new($"CardInstallment:{l.InstallmentId}", "CardPurchase", competence, l.DueDate, "Invoice", label.Number is null ? l.Description : label.Display, "Expense", "Out", "Realized", Money.Format(l.Amount),
                payer is { } p ? new(p, accountNames.GetValueOrDefault(p) ?? "Conta", scopeOf.GetValueOrDefault(p)) : null, null, new EntryReference(l.CardId, l.CardName),
                l.CategoryId is { } id && categories.TryGetValue(id, out var cat) ? new EntryCategory(id, cat.Name, cat.Name) : null,
                "Undefined", "InvoiceFinancing", "None", "AtInvoicePayment", null, null, null, null, null, competence, ["ViewDetails", "OpenCards"], flags,
                Installment: label.Number is null ? null : new(label.OriginalDescription, label.Number, label.Total, label.OriginalNumber, label.OriginalTotal, label.Inconsistent));
        }
    }

    /// <summary>
    /// Ocorrências que o motor de cartões calcula para faturas ainda não fechadas pelo banco (parcelas conhecidas/estimadas, compras pendentes, cobranças recorrentes). São leitura: nunca viram registro,
    /// nunca substituem um fato confirmado (o motor já as descarta quando a fatura fecha) e ficam sempre como Pendente, sinalizadas como estimativa quando não há confirmação.
    /// </summary>
    private static Guid? RecurrenceIdOf(string key) => Guid.TryParse(key.Split(':')[0], out var id) ? id : null;

    private static IEnumerable<FinancialEntryDto> ProjectedCardEntries(IReadOnlyList<CardProjection> projections, string competence, IReadOnlyDictionary<Guid, FinancialCategoryDto> categories, IReadOnlyDictionary<Guid, FinancialCreditCardDto> cards,
        IReadOnlyDictionary<Guid, string> scopeOf, IReadOnlyDictionary<Guid, string> accountNames)
    {
        foreach (var projection in projections)
        {
            var card = cards.GetValueOrDefault(projection.Card.Id);
            var payer = card?.DefaultPaymentAccountId ?? projection.Card.PayerAccountId;
            foreach (var o in projection.Occurrences ?? [])
            {
                if (o.Competence != competence) continue;
                var flags = new List<string> { o.Quality != "Known" ? "Estimated" : o.Kind == "ManualRecurring" ? "Committed" : "KnownImported" };
                if (o.Kind == "InvoiceFinancing") flags.Add("PrincipalInterestUnknown");
                if (!projection.Coverage.Complete) flags.Add("PartialCoverage");
                if (payer is null) flags.Add("NoPayerAccount");
                var label = o.Number is not null ? InstallmentLabel.Build(o.Description, o.Number, o.Total) : null;
                if (label is { Inconsistent: true }) flags.Add("InstallmentInconsistent");
                var description = label?.Display ?? o.Description;
                yield return new($"CardProjected:{o.CardId}:{o.Key}", "CardProjected", competence, o.Date, "Invoice", description, "Expense", "Out", "Pending", Money.Format(o.Amount),
                    payer is { } p ? new(p, accountNames.GetValueOrDefault(p) ?? projection.Card.PayerName ?? "Conta", scopeOf.GetValueOrDefault(p)) : null, null, new EntryReference(o.CardId, projection.Card.Name),
                    o.CategoryId is { } cid && categories.TryGetValue(cid, out var cat) ? new EntryCategory(cid, cat.Name, cat.Name) : null,
                    null, o.Kind, o.Kind == "InvoiceFinancing" ? "None" : "Planned", "AtInvoicePayment", null, null, null, null, o.Kind == "ManualRecurring" ? RecurrenceIdOf(o.Key) : null, competence, o.Kind == "ManualRecurring" ? ["ViewDetails", "OpenRecurrences"] : ["ViewDetails", "OpenCards"], flags, Installment: label is null ? null : new(label.OriginalDescription, label.Number, label.Total, label.OriginalNumber, label.OriginalTotal, label.Inconsistent));
            }
        }
    }
}

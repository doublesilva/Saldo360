using System.Globalization;
using Saldo360.Application.Accounts;
using Saldo360.Application.CreditCards;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

/// <summary>
/// Integra contas externas da Pluggy ao cadastro de contas/cartões do Saldo360 (T09.3). Somente o dono do orçamento, com a integração
/// habilitada. O identificador da conta externa nunca é confiado ao navegador: é conferido contra as contas do Item vinculado ao orçamento.
/// </summary>
public sealed class PluggyAccountLinkService(
    IFinancialWorkspaceRepository workspaces,
    IPluggyItemLinkRepository items,
    IPluggyFinancialRepository repository,
    IPluggyClient client,
    PluggyOptions options,
    FinancialAccountService accountService,
    IFinancialAccountRepository accounts,
    IFinancialCreditCardPurchaseRepository cardPurchases,
    Saldo360.Application.CreditCards.FinancialCreditCardService cardService,
    IPluggyPostingEngine engine,
    TimeProvider time)
{
    public bool IsEnabled => options.Enabled;

    public async Task<bool> CanAccessAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        options.Enabled && await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is not null;

    // ---- Seleção da conta externa ------------------------------------------------------------------------------------

    public async Task<PluggyResult<IReadOnlyList<PluggyAvailableAccountView>>> ListAvailableAsync(Guid workspaceId, Guid userId, Guid connectionId, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct) || await items.FindAsync(workspaceId, connectionId, ct) is not { } connection)
            return PluggyResult<IReadOnlyList<PluggyAvailableAccountView>>.NotFound();
        try
        {
            var external = await client.ListAccountsAsync(connection.PluggyItemId, ct);
            var linked = (await repository.ListLinksAsync(workspaceId, connectionId, ct)).ToDictionary(l => l.PluggyAccountId, StringComparer.Ordinal);
            var views = external.Select(a => ToAvailable(a, linked.GetValueOrDefault(a.Id))).ToArray();
            return PluggyResult<IReadOnlyList<PluggyAvailableAccountView>>.Ok(views);
        }
        catch (PluggyException error) { return Failure<IReadOnlyList<PluggyAvailableAccountView>>(error); }
    }

    // ---- Vínculo -------------------------------------------------------------------------------------------------------

    public async Task<PluggyResult<PluggyAccountLinkDetailView>> LinkAsync(Guid workspaceId, Guid userId, PluggyLinkRequest request, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyResult<PluggyAccountLinkDetailView>.NotFound();
        if (request.ConnectionId is not { } connectionId || await items.FindAsync(workspaceId, connectionId, ct) is not { } connection)
            return PluggyResult<PluggyAccountLinkDetailView>.NotFound();
        if (string.IsNullOrWhiteSpace(request.PluggyAccountId)) return PluggyResult<PluggyAccountLinkDetailView>.Invalid("Selecione a conta da Pluggy.");

        PluggyItem item;
        IReadOnlyList<PluggyAccount> external;
        try
        {
            item = await client.GetItemAsync(connection.PluggyItemId, ct);
            external = await client.ListAccountsAsync(connection.PluggyItemId, ct);
        }
        catch (PluggyException error) { return Failure<PluggyAccountLinkDetailView>(error); }

        // O ID vem do navegador: só vale se pertencer a uma conta devolvida pela Pluggy para o Item deste orçamento.
        var account = external.FirstOrDefault(a => string.Equals(a.Id, request.PluggyAccountId.Trim(), StringComparison.Ordinal));
        if (account is null) return PluggyResult<PluggyAccountLinkDetailView>.Invalid("A conta informada não pertence a esta conexão.");
        if (PluggyFinancialMapper.KindOf(account.Type) is not { } kind) return PluggyResult<PluggyAccountLinkDetailView>.Invalid("Este tipo de conta da Pluggy não é suportado para integração.");
        if (request.Mode is not (PluggyLinkModes.CreateNew or PluggyLinkModes.LinkExisting)) return PluggyResult<PluggyAccountLinkDetailView>.Invalid("Informe se a conta do Saldo360 será criada ou vinculada.");
        if (await repository.FindLinkByExternalAsync(workspaceId, connectionId, account.Id, ct) is not null)
            return new(PluggyAccess.Conflict, Message: "Esta conta da Pluggy já está vinculada.");

        var now = time.GetUtcNow();
        var today = BusinessCalendar.Today(now);
        var errors = new List<string>();
        if (request.ImportFrom is { } from && from > today) errors.Add("A data de início da importação não pode ser futura.");
        if (request.Mode == PluggyLinkModes.LinkExisting && request.ImportFrom is null)
            errors.Add("Informe a data de início da importação: o histórico anterior a ela não é importado, para não duplicar lançamentos já feitos nesta conta.");

        AccountDetails? newAccount = null; CreditCardDetails? newCard = null;
        Guid? existingAccountId = null, existingCardId = null;
        var display = DisplayName(item, account);
        if (kind == PluggyLinkedAccountKind.Bank)
        {
            if (request.Mode == PluggyLinkModes.LinkExisting)
            {
                existingAccountId = request.ExistingAccountId;
                if (existingAccountId is null || existingAccountId == Guid.Empty) errors.Add("Selecione a conta do Saldo360.");
            }
            else
            {
                var suggested = PluggyFinancialMapper.AccountTypeOf(account.Subtype).Type.ToString();
                newAccount = AccountDetails.TryCreate(request.Name ?? SuggestedName(account), Clip(item.ConnectorName, FinancialAccount.InstitutionMaxLength), request.AccountType ?? suggested, request.FinancialScope,
                    request.ResponsibleUserId, null, null, out var accountErrors);
                errors.AddRange(accountErrors.Values.SelectMany(v => v));
                if (request.ResponsibleUserId is { } responsible && !accountErrors.ContainsKey("responsibleUserId") && !await accounts.IsWorkspaceOwnerAsync(workspaceId, responsible, ct))
                    errors.Add("Responsável inválido.");
            }
        }
        else if (request.Mode == PluggyLinkModes.LinkExisting)
        {
            existingCardId = request.ExistingCardId;
            if (existingCardId is null || existingCardId == Guid.Empty) errors.Add("Selecione o cartão do Saldo360.");
        }
        else
        {
            var suggestion = CardSuggestion(account);
            newCard = CreditCardDetails.TryCreate(request.Name ?? SuggestedName(account), Clip(item.ConnectorName, FinancialCreditCard.IssuerMaxLength) ?? "Cartão", LastFour(account.Number), null,
                request.CreditLimit ?? account.Credit?.CreditLimit ?? 0m, request.ClosingDay ?? suggestion.ClosingDay, request.DueDay ?? suggestion.DueDay,
                request.PaymentAccountId, out var cardErrors);
            errors.AddRange(cardErrors.Values.SelectMany(v => v));
            if (request.PaymentAccountId is { } paymentAccount && !cardErrors.ContainsKey("defaultPaymentAccountId")
                && await accounts.GetAsync(workspaceId, paymentAccount, ct) is not { IsActive: true, Type: AccountType.Checking or AccountType.Savings }) errors.Add("A conta de pagamento precisa ser uma conta bancária ativa do orçamento.");
        }
        if (errors.Count > 0) return PluggyResult<PluggyAccountLinkDetailView>.Invalid(string.Join(" ", errors.Distinct()));

        var plan = new PluggyLinkPlan(connectionId, account.Id, kind, display, item.ConnectorName, account.Type, account.Subtype, request.ImportFrom, Observed(account),
            newAccount, newCard, existingAccountId, existingCardId);
        var creation = await repository.CreateLinkAsync(workspaceId, userId, plan, now, ct);
        switch (creation.Outcome)
        {
            case PluggyLinkOutcome.DuplicateExternal: return new(PluggyAccess.Conflict, Message: "Esta conta da Pluggy já está vinculada.");
            case PluggyLinkOutcome.DuplicateTarget: return new(PluggyAccess.Conflict, Message: "Esta conta/cartão do Saldo360 já está integrado a outra conta da Pluggy.");
            case PluggyLinkOutcome.TargetNotFound: return PluggyResult<PluggyAccountLinkDetailView>.Invalid("A conta/cartão do Saldo360 informado não foi encontrado.");
            case PluggyLinkOutcome.TargetInactive: return PluggyResult<PluggyAccountLinkDetailView>.Invalid("A conta/cartão do Saldo360 informado está inativo.");
            case PluggyLinkOutcome.Failed: return PluggyResult<PluggyAccountLinkDetailView>.Invalid("Não foi possível criar o vínculo.");
        }

        // Transações já importadas (T09.2) passam a alimentar o realizado assim que a conta é vinculada.
        // Uma falha ao processar o que já foi importado não desfaz o vínculo: o reprocessamento é idempotente e roda de novo na próxima sincronização.
        try { await engine.ApplyAsync(workspaceId, userId, now, ct); }
        catch (Exception error) when (error is not OperationCanceledException) { }
        return await GetDetailAsync(workspaceId, userId, creation.Link!.Id, ct);
    }

    public async Task<PluggyResult<IReadOnlyList<PluggyAccountLinkView>>> ListAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyResult<IReadOnlyList<PluggyAccountLinkView>>.NotFound();
        var counts = await repository.CountPostingsAsync(workspaceId, ct);
        var list = (await repository.ListLinksAsync(workspaceId, null, ct)).Select(l => ToView(l, counts.GetValueOrDefault(l.Id) ?? new(0, 0, 0, 0, 0))).ToArray();
        return PluggyResult<IReadOnlyList<PluggyAccountLinkView>>.Ok(list);
    }

    public async Task<PluggyResult<PluggyAccountLinkDetailView>> GetDetailAsync(Guid workspaceId, Guid userId, Guid linkId, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct) || await repository.FindLinkAsync(workspaceId, linkId, ct) is not { } link)
            return PluggyResult<PluggyAccountLinkDetailView>.NotFound();
        var counts = (await repository.CountPostingsAsync(workspaceId, ct)).GetValueOrDefault(link.Id) ?? new(0, 0, 0, 0, 0);
        return PluggyResult<PluggyAccountLinkDetailView>.Ok(new(ToView(link, counts), await ReconcileAsync(workspaceId, userId, link, counts, ct)));
    }

    /// <summary>Reprocessa as transações já importadas (sem consultar a Pluggy). Idempotente.</summary>
    public async Task<PluggyResult<PluggyApplySummary>> ReprocessAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyResult<PluggyApplySummary>.NotFound();
        return PluggyResult<PluggyApplySummary>.Ok(await engine.ApplyAsync(workspaceId, userId, time.GetUtcNow(), ct));
    }

    /// <summary>
    /// Aplica o saldo inicial sugerido, SOMENTE por pedido explícito. O valor é recalculado aqui (o navegador não informa número). Se a conta
    /// já tem saldo inicial, exige <paramref name="replaceExisting"/>.
    /// </summary>
    public async Task<PluggyResult<PluggyAccountLinkDetailView>> ApplyOpeningBalanceSuggestionAsync(Guid workspaceId, Guid userId, Guid linkId, bool replaceExisting, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct) || await repository.FindLinkAsync(workspaceId, linkId, ct) is not { Kind: PluggyLinkedAccountKind.Bank, FinancialAccountId: { } accountId } link)
            return PluggyResult<PluggyAccountLinkDetailView>.NotFound();
        var counts = (await repository.CountPostingsAsync(workspaceId, ct)).GetValueOrDefault(link.Id) ?? new(0, 0, 0, 0, 0);
        var reconciliation = await ReconcileAsync(workspaceId, userId, link, counts, ct);
        if (reconciliation.SuggestedOpeningBalance is not { } amountText || reconciliation.SuggestedOpeningBalanceDate is not { } date)
            return new(PluggyAccess.Conflict, Message: "Não há base consistente para sugerir o saldo inicial: " + (reconciliation.SuggestionUnavailableReason ?? "dados insuficientes") + ".");
        var account = await accountService.GetAsync(workspaceId, accountId, userId, ct);
        if (account is null) return PluggyResult<PluggyAccountLinkDetailView>.NotFound();
        if (account.OpeningBalance is not null && !replaceExisting)
            return new(PluggyAccess.Conflict, Message: "A conta já tem saldo inicial. Confirme a substituição para aplicar o sugerido.");
        var input = new AccountInput(account.Name, account.Institution, account.Type.ToString(), account.FinancialScope.ToString(), account.Responsible?.UserId,
            decimal.Parse(amountText, CultureInfo.InvariantCulture), date);
        var updated = await accountService.UpdateAsync(workspaceId, accountId, userId, input, account.IsActive, ct);
        if (updated.Access != AccountAccess.Allowed) return PluggyResult<PluggyAccountLinkDetailView>.Invalid("Não foi possível aplicar o saldo inicial sugerido.");
        return await GetDetailAsync(workspaceId, userId, linkId, ct);
    }

    /// <summary>
    /// Aplica ao cartão o dia habitual de fechamento e/ou vencimento sugerido pelo histórico de faturas do banco, SOMENTE com confirmação explícita.
    /// O valor é recalculado aqui a partir das faturas guardadas (o navegador não informa número) e só o que o histórico sustenta é alterado.
    /// </summary>
    public async Task<PluggyResult<PluggyAccountLinkDetailView>> ApplyCardScheduleSuggestionAsync(Guid workspaceId, Guid userId, Guid linkId, bool confirm, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct) || await repository.FindLinkAsync(workspaceId, linkId, ct) is not { Kind: PluggyLinkedAccountKind.Card, CreditCardId: { } cardId } link)
            return PluggyResult<PluggyAccountLinkDetailView>.NotFound();
        if (!confirm) return PluggyResult<PluggyAccountLinkDetailView>.Invalid("Confirme para alterar o fechamento e o vencimento habituais do cartão.");
        var suggestion = PluggyCardSchedule.Suggest(await repository.ListBillsAsync(workspaceId, linkId, ct), BusinessCalendar.Today(time.GetUtcNow()));
        var card = await cardService.GetAsync(workspaceId, cardId, userId, ct);
        if (card is null) return PluggyResult<PluggyAccountLinkDetailView>.NotFound();
        var closing = suggestion.ClosingDay ?? card.ClosingDay; var due = suggestion.DueDay ?? card.DueDay;
        if (suggestion.ClosingDay is null && suggestion.DueDay is null)
            return new(PluggyAccess.Conflict, Message: "O histórico de faturas do banco não é consistente o bastante para sugerir o fechamento e o vencimento.");
        if (closing == card.ClosingDay && due == card.DueDay) return await GetDetailAsync(workspaceId, userId, linkId, ct);
        var result = await cardService.UpdateAsync(workspaceId, cardId, userId, new Saldo360.Application.CreditCards.CreditCardInput(card.Name, card.Issuer, card.LastFourDigits, card.HolderUserId,
            decimal.Parse(card.CreditLimit, CultureInfo.InvariantCulture), closing, due, card.DefaultPaymentAccountId, card.GmailQuery), ct);
        if (result.Access != Saldo360.Application.CreditCards.CreditCardAccess.Allowed) return PluggyResult<PluggyAccountLinkDetailView>.Invalid("Não foi possível atualizar o cartão.");
        return await GetDetailAsync(workspaceId, userId, linkId, ct);
    }

    // ---- Saldos (observado × calculado) ----------------------------------------------------------------------------------

    private async Task<PluggyReconciliationView> ReconcileAsync(Guid workspaceId, Guid userId, PluggyAccountLink link, PluggyPostingCounts counts, CancellationToken ct)
    {
        var notes = new List<string>();
        if (link.Kind == PluggyLinkedAccountKind.Card)
        {
            var limit = link.CreditCardId is { } cardId ? await cardPurchases.GetLimitAsync(workspaceId, cardId, ct) : null;
            notes.Add("Cartão não soma saldo de caixa: limite e saldo devedor são exibidos separadamente do saldo bancário.");
            var current = link.CreditCardId is { } id ? await cardService.GetAsync(workspaceId, id, userId, ct) : null;
            var schedule = PluggyCardSchedule.Suggest(await repository.ListBillsAsync(workspaceId, link.Id, ct), BusinessCalendar.Today(time.GetUtcNow()));
            return new(null, link.ObservedAt, null, null, null, "CardHasNoCashBalance", counts.NeedsReview, counts.Waiting, null, null, "CardHasNoCashBalance",
                new PluggyCardFiguresView(limit?.CreditLimit, limit?.UsedLimit, limit?.AvailableLimit, Format(link.ObservedBalance), Format(link.AvailableCreditLimit),
                    await BillsAsync(workspaceId, link, ct))
                {
                    CurrentClosingDay = current?.ClosingDay, CurrentDueDay = current?.DueDay,
                    // Só sugere o que difere do cadastrado; sem consistência no histórico não há sugestão.
                    SuggestedClosingDay = schedule.ClosingDay is { } sc && sc != current?.ClosingDay ? sc : null,
                    SuggestedDueDay = schedule.DueDay is { } sd && sd != current?.DueDay ? sd : null,
                    ClosingScheduleReason = schedule.ClosingReason, DueScheduleReason = schedule.DueReason,
                }, notes);
        }

        var accountId = link.FinancialAccountId!.Value;
        var balance = await accountService.GetBalanceAsync(workspaceId, accountId, userId, null, ct);
        decimal? calculated = balance.Balance?.Balance;
        string? reason = balance.Balance?.UnavailableReason?.ToString();
        // Com saldo inicial derivado da referência bancária (âncora), o calculado reproduz o observado por construção: não há diferença a mostrar.
        var anchored = balance.Balance?.OpeningSource == "BalanceAnchor";
        var difference = !anchored && calculated is { } c && link.ObservedBalance is { } o ? o - c : (decimal?)null;
        if (anchored) notes.Add("O saldo calculado parte da referência bancária (saldo observado no banco) e dos movimentos consistentes; não houve cadastro manual de saldo inicial.");
        if (link.OverdraftContractedLimit is not null) notes.Add("O limite de cheque especial informado pelo banco não compõe o saldo.");
        if (counts.NeedsReview > 0) notes.Add($"{counts.NeedsReview} movimento(s) em revisão ainda não entram no saldo calculado.");
        if (counts.Waiting > 0) notes.Add($"{counts.Waiting} movimento(s) pendentes ou futuros ainda não entram no saldo calculado.");

        // Sugestão de saldo inicial: só com base consistente. Nunca é aplicada automaticamente.
        string? suggested = null; DateOnly? suggestedDate = null; string? suggestionReason = null;
        var syncedDate = link.LastSyncedAt is { } synced ? BusinessCalendar.Today(synced) : (DateOnly?)null;
        var start = link.ImportFrom ?? await repository.EarliestAppliedDateAsync(workspaceId, link.Id, ct);
        if (link.ObservedBalance is not { } observed) suggestionReason = "NoObservedBalance";
        else if (ExactCents(observed) is not { } observedCents) suggestionReason = "ObservedBalancePrecision";
        else if (syncedDate is null) suggestionReason = "NeverSynced";
        else if (start is null) suggestionReason = "NoImportedHistory";
        else if (counts.NeedsReview > 0) suggestionReason = "ReviewPending";
        else
        {
            var totals = await accounts.SumMovementsAsync(workspaceId, accountId, start.Value, syncedDate.Value, ct);
            suggested = Money.Format(FinancialBalanceCalculator.ApplyMovements(observedCents, -totals.Income, -totals.Expense, -totals.TransferIn, -totals.TransferOut));
            suggestedDate = start;
            notes.Add($"Sugestão calculada com os movimentos de {start:dd/MM/yyyy} a {syncedDate:dd/MM/yyyy}; confira antes de aplicar.");
        }

        return new(Format(link.ObservedBalance), link.ObservedAt, calculated is { } v ? Money.Format(v) : null, balance.Balance?.AsOf, difference is { } d ? Format(d) : null,
            reason, counts.NeedsReview, counts.Waiting, suggested, suggestedDate, suggestionReason, null, notes);
    }

    /// <summary>
    /// Faturas do banco (últimas 12) em três dimensões independentes: ciclo (aberta/fechada pela instituição), pagamento (não paga/parcial/paga) e
    /// conciliação (não verificada/conciliada/divergente/incompleta). O total bancário é preservado como veio; a diferença é só informada.
    /// </summary>
    private async Task<IReadOnlyList<PluggyBillReconciliationView>> BillsAsync(Guid workspaceId, PluggyAccountLink link, CancellationToken ct)
    {
        var today = BusinessCalendar.Today(time.GetUtcNow());
        var rows = await repository.ListBillStatesAsync(workspaceId, link.Id, 12, ct);
        return rows.Select(row =>
        {
            var bill = row.Bill;
            var closed = bill.ClosingDate is { } closing && closing <= today && bill.Total is not null;
            var effectivePaid = Math.Max(row.RecordedPaid, row.ReportedPaid);
            var payment = bill.Total is not { } total || !closed ? "Unpaid"
                : total > 0m && effectivePaid >= total ? "Paid" : effectivePaid > 0m ? "PartiallyPaid" : total == 0m ? "Paid" : "Unpaid";
            string? difference = bill.Total is { } b && row.ComponentsTotal is { } ours ? Format(b - ours) : null;
            return new PluggyBillReconciliationView(bill.ClosingDate, bill.DueDate, Format(bill.Total), Format(row.ComponentsTotal), difference, closed ? "Closed" : "Open", payment,
                row.Reconciliation.ToString(), row.ReconciliationReason, Format(row.RecordedPaid), Format(row.ReportedPaid), Format(bill.FinanceChargesTotal), Format(bill.MinimumPayment));
        }).ToList();
    }

    // ---- Normalização ---------------------------------------------------------------------------------------------------

    private static PluggyAvailableAccountView ToAvailable(PluggyAccount a, PluggyAccountLink? linked)
    {
        var kind = PluggyFinancialMapper.KindOf(a.Type);
        var (type, recognized) = PluggyFinancialMapper.AccountTypeOf(a.Subtype);
        var linkable = kind is not null && linked is null;
        var reason = kind is null ? "UnsupportedType" : linked is not null ? "AlreadyLinked" : null;
        return new(a.Id, kind?.ToString() ?? "Unsupported", SuggestedName(a) + (PluggyPocService.MaskTail(a.Number) is { } mask ? " " + mask : string.Empty), PluggyPocService.MaskTail(a.Number),
            a.Type, a.Subtype, kind == PluggyLinkedAccountKind.Bank ? type.ToString() : null, kind != PluggyLinkedAccountKind.Bank || recognized,
            Format(a.Balance), a.CurrencyCode, kind == PluggyLinkedAccountKind.Card ? CardSuggestionView(a) : null, linked?.Id, linkable, reason);
    }

    private static PluggyCardSuggestion CardSuggestionView(PluggyAccount a)
    {
        var s = CardSuggestion(a);
        return new(s.ClosingDay, s.DueDay, Format(a.Credit?.CreditLimit), Format(a.Credit?.AvailableCreditLimit), a.Credit?.Brand);
    }

    private static (int? ClosingDay, int? DueDay) CardSuggestion(PluggyAccount a) => (a.Credit?.BalanceCloseDate?.Day, a.Credit?.BalanceDueDate?.Day);

    internal static string SuggestedName(PluggyAccount a) => (!string.IsNullOrWhiteSpace(a.MarketingName) ? a.MarketingName : !string.IsNullOrWhiteSpace(a.Name) ? a.Name : a.Type ?? "Conta").Trim();

    private static string DisplayName(PluggyItem item, PluggyAccount a)
    {
        var name = SuggestedName(a);
        var mask = PluggyPocService.MaskTail(a.Number);
        return $"{item.ConnectorName ?? "Instituição"} · {name}{(mask is null ? string.Empty : " " + mask)}";
    }

    private static string? LastFour(string? number)
    {
        var digits = number is null ? string.Empty : new string(number.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 ? digits[^4..] : null;
    }

    internal static PluggyObservedFigures Observed(PluggyAccount a) => new(a.Balance, a.UpdatedAt, a.Bank?.OverdraftContractedLimit, a.Bank?.OverdraftUsedLimit,
        a.Credit?.CreditLimit, a.Credit?.AvailableCreditLimit);

    internal static PluggyAccountLinkView ToView(PluggyAccountLink l, PluggyPostingCounts counts) => new(l.Id, l.ConnectionId, "Pluggy", l.Kind.ToString(), l.FinancialAccountId, l.CreditCardId,
        l.DisplayName, l.InstitutionName, l.PluggyType, l.PluggySubtype, l.Status.ToString(), l.ImportFrom, l.LinkedAt, l.LastSyncedAt,
        new PluggyObservedView(Format(l.ObservedBalance), l.ObservedAt, Format(l.OverdraftContractedLimit), Format(l.OverdraftUsedLimit), Format(l.CreditLimit), Format(l.AvailableCreditLimit)), counts);

    /// <summary>Texto decimal de exibição (duas casas) para valores informados pelo banco.</summary>
    internal static string? Format(decimal? value) => value is { } v ? decimal.Round(v, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture) : null;

    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();

    private static decimal? ExactCents(decimal value) => PluggyFinancialMapper.ExactCents(value);

    private static PluggyResult<T> Failure<T>(PluggyException error) =>
        error.Kind == PluggyFailureKind.NotFound ? PluggyResult<T>.NotFound() : new(PluggyAccess.IntegrationFailure, default, error.Kind, error.Message);
}

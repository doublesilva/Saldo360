using System.Globalization;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

/// <summary>
/// Laboratório somente leitura: consulta a Pluggy e normaliza o resultado para inspeção. Não escreve em contas,
/// lançamentos, saldos, previsões, cartões ou faturas; a única escrita é o vínculo local do Item.
/// </summary>
public sealed class PluggyPocService(IFinancialWorkspaceRepository workspaces, IPluggyItemLinkRepository links, IPluggyClient client,
    PluggyOptions options, TimeProvider time)
{
    public bool IsEnabled => options.Enabled;

    /// <summary>O laboratório só responde a quem é dono do orçamento e quando está habilitado na configuração.</summary>
    public async Task<bool> CanAccessAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        options.Enabled && await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is not null;

    public async Task<PluggyResult<IReadOnlyList<PluggyConnectionView>>> ListConnectionsAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyResult<IReadOnlyList<PluggyConnectionView>>.NotFound();
        var views = new List<PluggyConnectionView>();
        try
        {
            foreach (var link in await links.ListAsync(workspaceId, ct))
            {
                try { views.Add(ToView(link, await client.GetItemAsync(link.PluggyItemId, ct), null)); }
                catch (PluggyException error) when (error.Kind != PluggyFailureKind.NotConfigured)
                {
                    // Um Item com falha não impede a listagem dos demais.
                    views.Add(ToView(link, null, error.Kind.ToString()));
                }
            }
        }
        catch (PluggyException error) { return Failure<IReadOnlyList<PluggyConnectionView>>(error); }
        return PluggyResult<IReadOnlyList<PluggyConnectionView>>.Ok(views);
    }

    public async Task<PluggyResult<PluggyConnectionView>> AddConnectionAsync(Guid workspaceId, Guid userId, string? itemId, string? label, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyResult<PluggyConnectionView>.NotFound();
        PluggyItemLink link;
        try { link = PluggyItemLink.Create(workspaceId, userId, itemId ?? string.Empty, label, time.GetUtcNow()); }
        catch (ArgumentException) { return PluggyResult<PluggyConnectionView>.Invalid("Informe o identificador do Item (UUID) e um rótulo válido."); }
        try
        {
            // O Item precisa existir para as credenciais configuradas antes de ser vinculado.
            var item = await client.GetItemAsync(link.PluggyItemId, ct);
            if (!await links.TryAddAsync(link, ct))
                return new(PluggyAccess.Conflict, Message: "Este Item já está vinculado.");
            return PluggyResult<PluggyConnectionView>.Ok(ToView(link, item, null));
        }
        catch (PluggyException error) when (error.Kind is PluggyFailureKind.NotFound or PluggyFailureKind.Forbidden or PluggyFailureKind.Rejected)
        {
            return PluggyResult<PluggyConnectionView>.Invalid("A Pluggy não reconhece este Item para as credenciais configuradas.");
        }
        catch (PluggyException error) { return Failure<PluggyConnectionView>(error); }
    }

    public async Task<PluggyResult<IReadOnlyList<PluggyAccountView>>> ListAccountsAsync(Guid workspaceId, Guid userId, Guid connectionId, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct) || await links.FindAsync(workspaceId, connectionId, ct) is not { } link)
            return PluggyResult<IReadOnlyList<PluggyAccountView>>.NotFound();
        try
        {
            var accounts = await client.ListAccountsAsync(link.PluggyItemId, ct);
            return PluggyResult<IReadOnlyList<PluggyAccountView>>.Ok(accounts.Select(a => ToView(link.Id, a)).ToArray());
        }
        catch (PluggyException error) { return Failure<IReadOnlyList<PluggyAccountView>>(error); }
    }

    public async Task<PluggyResult<PluggyTransactionsView>> ListTransactionsAsync(Guid workspaceId, Guid userId, string accountId,
        DateOnly? from, DateOnly? to, string? after, CancellationToken ct)
    {
        if (from is { } f && to is { } t && f > t) return PluggyResult<PluggyTransactionsView>.Invalid("O início do período não pode ser posterior ao fim.");
        try
        {
            if (await ResolveAccountAsync(workspaceId, userId, accountId, ct) is null) return PluggyResult<PluggyTransactionsView>.NotFound();
            var page = await client.ListTransactionsAsync(accountId, from, to, after, ct);
            return PluggyResult<PluggyTransactionsView>.Ok(new(page.Items.Select(ToView).ToArray(), page.NextCursor));
        }
        catch (PluggyException error) { return Failure<PluggyTransactionsView>(error); }
    }

    public async Task<PluggyResult<PluggyBillsView>> ListBillsAsync(Guid workspaceId, Guid userId, string accountId, int page, int pageSize, CancellationToken ct)
    {
        try
        {
            if (await ResolveAccountAsync(workspaceId, userId, accountId, ct) is null) return PluggyResult<PluggyBillsView>.NotFound();
            var result = await client.ListBillsAsync(accountId, page, pageSize, ct);
            return PluggyResult<PluggyBillsView>.Ok(new(result.Items.Select(ToView).ToArray(), result.Page, result.TotalPages, result.Total));
        }
        catch (PluggyException error) { return Failure<PluggyBillsView>(error); }
    }

    public async Task<PluggyResult<PluggyDiagnosticsView>> DiagnoseAsync(Guid workspaceId, Guid userId, Guid connectionId, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct) || await links.FindAsync(workspaceId, connectionId, ct) is not { } link)
            return PluggyResult<PluggyDiagnosticsView>.NotFound();
        try { return PluggyResult<PluggyDiagnosticsView>.Ok(await new CoverageRun(client, options, time.GetUtcNow()).RunAsync(link, ct)); }
        catch (PluggyException error) { return Failure<PluggyDiagnosticsView>(error); }
    }

    /// <summary>Resolve a conta apenas se pertencer a um Item vinculado a este orçamento; do contrário, é como se não existisse.</summary>
    private async Task<PluggyAccount?> ResolveAccountAsync(Guid workspaceId, Guid userId, string accountId, CancellationToken ct)
    {
        if (!Guid.TryParse(accountId, out _) || !await CanAccessAsync(workspaceId, userId, ct)) return null;
        PluggyAccount account;
        try { account = await client.GetAccountAsync(accountId, ct); }
        catch (PluggyException error) when (error.Kind is PluggyFailureKind.NotFound or PluggyFailureKind.Forbidden) { return null; }
        if (account.ItemId is null) return null;
        var owned = await links.ListAsync(workspaceId, ct);
        return owned.Any(l => string.Equals(l.PluggyItemId, account.ItemId, StringComparison.OrdinalIgnoreCase)) ? account : null;
    }

    private static PluggyResult<T> Failure<T>(PluggyException error) =>
        error.Kind == PluggyFailureKind.NotFound ? PluggyResult<T>.NotFound() : new(PluggyAccess.IntegrationFailure, default, error.Kind, error.Message);

    // ---- Normalização --------------------------------------------------------------------------------------------

    public static PluggyConnectionState StateOf(PluggyItem item, DateTimeOffset now)
    {
        if (item.ConsentExpiresAt is { } expires && expires <= now) return PluggyConnectionState.ConsentExpired;
        return item.Status?.ToUpperInvariant() switch
        {
            "UPDATED" when item.ErrorCode is null => PluggyConnectionState.Healthy,
            "UPDATING" => PluggyConnectionState.Updating,
            "LOGIN_ERROR" or "OUTDATED" or "WAITING_USER_INPUT" or "WAITING_USER_ACTION" => PluggyConnectionState.ActionRequired,
            null => PluggyConnectionState.Unknown,
            _ => PluggyConnectionState.Error,
        };
    }

    private PluggyConnectionView ToView(PluggyItemLink link, PluggyItem? item, string? integrationError) => new(link.Id, link.Label,
        Mask(link.PluggyItemId, 8), item?.ConnectorName, item?.ConnectorType, item?.IsOpenFinance,
        item is null ? PluggyConnectionState.Unknown : StateOf(item, time.GetUtcNow()), item?.Status, item?.ExecutionStatus,
        item?.LastUpdatedAt, item?.ConsentExpiresAt, item?.ErrorCode, item?.ErrorMessage, integrationError, link.CreatedAt);

    internal static PluggyAccountView ToView(Guid connectionId, PluggyAccount a) => new(a.Id, connectionId, a.Type, a.Subtype, a.Name, a.MarketingName,
        MaskTail(a.Number), MaskTail(a.Bank?.TransferNumber), a.CurrencyCode, Money(a.Balance), a.UpdatedAt,
        Money(a.Bank?.OverdraftContractedLimit), Money(a.Bank?.OverdraftUsedLimit), Money(a.Credit?.CreditLimit), Money(a.Credit?.AvailableCreditLimit),
        Money(a.Credit?.MinimumPayment), a.Credit?.Brand, a.Credit?.Level, a.Credit?.BalanceCloseDate, a.Credit?.BalanceDueDate);

    internal static PluggyTransactionView ToView(PluggyTransaction t) => new(t.Id, t.Date, t.Description, t.DescriptionRaw, Money(t.Amount),
        Money(t.AmountInAccountCurrency), t.CurrencyCode, t.Type, t.Status, t.Category, Money(t.Balance), t.OperationType, t.Payment?.PaymentMethod,
        t.Payment?.HasPayer ?? false, t.Payment?.HasReceiver ?? false, t.Payment?.HasReferenceNumber ?? false, t.Merchant?.Name ?? t.Merchant?.BusinessName,
        t.Merchant?.Category, t.Card?.InstallmentNumber, t.Card?.TotalInstallments, Money(t.Card?.TotalAmount), t.Card?.BillId, t.Card?.CardLast4);

    internal static PluggyBillView ToView(PluggyBill b) => new(b.Id, b.DueDate, b.ClosingDate, Money(b.TotalAmount), b.TotalAmountCurrencyCode, Money(b.MinimumPaymentAmount),
        b.AllowsInstallments, b.FinanceCharges.Select(c => new PluggyFinanceChargeView(c.Type, Money(c.Amount), c.CurrencyCode, c.AdditionalInfo)).ToArray(),
        b.Payments.Select(p => new PluggyBillPaymentView(Money(p.Amount), p.CurrencyCode, p.PaymentDate, p.PaymentMode, p.ValueType)).ToArray());

    internal static string? Money(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
    internal static string Mask(string value, int visible) => value.Length <= visible ? value : $"{value[..visible]}…";

    /// <summary>Exibe apenas os quatro últimos caracteres úteis de agência/conta/cartão.</summary>
    internal static string? MaskTail(string? value)
    {
        var chars = value is null ? [] : value.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? null : chars.Length <= 4 ? "••••" : "••••" + new string(chars[^4..]);
    }
}

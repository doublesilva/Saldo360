using Saldo360.Application.Workspaces;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

/// <summary>
/// Sincronização manual de transações da Pluggy para tabelas próprias (T09.2). A coleta em si só grava/atualiza
/// <see cref="PluggyImportedTransaction"/>, o histórico de mudanças e o registro da coleta. Desde a T09.3, ao terminar, as contas já
/// integradas ao cadastro (<see cref="PluggyAccountLink"/>) têm seus valores observados atualizados e as transações efetivadas
/// alimentam o realizado pelo <see cref="IPluggyPostingEngine"/>; contas não integradas continuam sem nenhum efeito financeiro.
/// </summary>
public sealed class PluggyTransactionSyncService(IFinancialWorkspaceRepository workspaces, IPluggyItemLinkRepository links, IPluggyImportRepository imports,
    IPluggyClient client, PluggyOptions options, TimeProvider time, IPluggyFinancialRepository financial, IPluggyPostingEngine engine,
    IPluggySyncStateRepository states, PluggyAutoSyncOptions auto)
{
    public const int MaxPageSize = 200;

    public async Task<bool> CanImportAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        options.Enabled && options.ImportEnabled && await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is not null;

    public async Task<PluggyResult<PluggySyncRunView>> SyncAsync(Guid workspaceId, Guid userId, Guid connectionId, CancellationToken ct)
    {
        if (!await CanImportAsync(workspaceId, userId, ct) || await links.FindAsync(workspaceId, connectionId, ct) is not { } link)
            return PluggyResult<PluggySyncRunView>.NotFound();
        return await RunConnectionAsync(link, userId, PluggySyncTrigger.Manual, null, ct);
    }

    /// <summary>
    /// Sincroniza uma conexão: o mesmo caminho para o botão "Sincronizar agora" e para o agendador. Só uma execução por conexão de cada vez (índice único no banco),
    /// então manual e automática não processam a mesma conexão ao mesmo tempo. <paramref name="knownItem"/> evita consultar o Item duas vezes quando o agendador já o consultou.
    /// </summary>
    public async Task<PluggyResult<PluggySyncRunView>> RunConnectionAsync(PluggyItemLink link, Guid userId, PluggySyncTrigger trigger, PluggyItem? knownItem, CancellationToken ct)
    {
        var run = await imports.TryStartRunAsync(link.WorkspaceId, link.Id, userId, time.GetUtcNow(), TimeSpan.FromMinutes(Math.Max(1, options.SyncStaleMinutes)), trigger, ct);
        if (run is null) return new(PluggyAccess.Conflict, Message: "Já existe uma sincronização em andamento para esta conexão.");

        PluggyException? fatal = null;
        PluggyItem? observed = knownItem;
        var failed = false;
        try
        {
            await RunAsync(link, run, knownItem, item => observed = item, ct);
            await ApplyFinancialAsync(link, run, userId, ct);
        }
        catch (PluggyException error)
        {
            fatal = error;
            run.AddError(error.Kind.ToString());
            failed = run.AccountsSynced == 0;
        }
        catch (OperationCanceledException)
        {
            run.AddError("Cancelled");
            failed = true;
            throw;
        }
        finally
        {
            // Nenhuma conta sincronizada e com erros: a coleta falhou (ex.: consentimento vencido ou limite de requisições persistente).
            run.Finish(time.GetUtcNow(), failed || (run.AccountsSynced == 0 && run.Errors > 0));
            // A coleta termina registrada mesmo se a requisição foi cancelada.
            await imports.SaveRunAsync(run, CancellationToken.None);
            await RecordStateAsync(link, run, observed, trigger);
        }
        return fatal is not null && failed
            ? new(PluggyAccess.IntegrationFailure, ToView(run), fatal.Kind, fatal.Message)
            : PluggyResult<PluggySyncRunView>.Ok(ToView(run));
    }

    /// <summary>
    /// Monitoramento e agendamento: guarda o resultado da tentativa e calcula a próxima. Uma falha ao gravar o estado nunca invalida a sincronização (os dados já estão salvos).
    /// </summary>
    private async Task RecordStateAsync(PluggyItemLink link, PluggySyncRun run, PluggyItem? item, PluggySyncTrigger trigger)
    {
        try
        {
            var at = time.GetUtcNow();
            var expired = run.ErrorSummary?.Contains("ConsentExpired", StringComparison.Ordinal) == true;
            var outcome = expired ? PluggySyncOutcome.ConsentExpired : run.Status switch
            {
                PluggySyncStatus.Completed => PluggySyncOutcome.Completed,
                PluggySyncStatus.CompletedWithErrors => PluggySyncOutcome.CompletedWithErrors,
                _ => PluggySyncOutcome.Failed,
            };
            var failure = outcome is PluggySyncOutcome.Failed or PluggySyncOutcome.CompletedWithErrors;
            await states.SaveAsync(link.WorkspaceId, link.Id, state =>
            {
                if (item is not null) state.ObserveItem(item.Status, item.ConsentExpiresAt, at);
                var next = outcome switch
                {
                    PluggySyncOutcome.Completed => PluggyAutoSyncPolicy.AfterSuccess(at, auto),
                    PluggySyncOutcome.ConsentExpired => PluggyAutoSyncPolicy.AfterConsentExpired(at, auto),
                    _ => PluggyAutoSyncPolicy.AfterFailure(at, state.ConsecutiveFailures + 1, auto),
                };
                state.RecordAttempt(at, outcome, trigger, run.Id, run.ErrorSummary, failure, next, item?.LastUpdatedAt);
            }, at, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException) { /* o histórico da execução já foi salvo */ }
    }

    public async Task<PluggyResult<PluggyImportedPage>> ListAsync(Guid workspaceId, Guid userId, PluggyImportFilter filter, CancellationToken ct)
    {
        if (!await CanImportAsync(workspaceId, userId, ct)) return PluggyResult<PluggyImportedPage>.NotFound();
        if (filter.ConnectionId is { } connection && await links.FindAsync(workspaceId, connection, ct) is null) return PluggyResult<PluggyImportedPage>.NotFound();
        if (filter.From is { } from && filter.To is { } to && from > to) return PluggyResult<PluggyImportedPage>.Invalid("O início do período não pode ser posterior ao fim.");
        var normalized = filter with { Page = Math.Max(1, filter.Page), PageSize = Math.Clamp(filter.PageSize, 1, MaxPageSize) };
        return PluggyResult<PluggyImportedPage>.Ok(await imports.ListAsync(workspaceId, normalized, time.GetUtcNow(), ct));
    }

    public async Task<PluggyResult<IReadOnlyList<PluggySyncRunView>>> ListRunsAsync(Guid workspaceId, Guid userId, Guid connectionId, CancellationToken ct)
    {
        if (!await CanImportAsync(workspaceId, userId, ct) || await links.FindAsync(workspaceId, connectionId, ct) is null)
            return PluggyResult<IReadOnlyList<PluggySyncRunView>>.NotFound();
        var runs = await imports.ListRunsAsync(workspaceId, connectionId, 20, ct);
        return PluggyResult<IReadOnlyList<PluggySyncRunView>>.Ok(runs.Select(ToView).ToArray());
    }

    public async Task<PluggyResult<PluggyStabilityView>> StabilityAsync(Guid workspaceId, Guid userId, Guid connectionId, CancellationToken ct)
    {
        if (!await CanImportAsync(workspaceId, userId, ct) || await links.FindAsync(workspaceId, connectionId, ct) is null)
            return PluggyResult<PluggyStabilityView>.NotFound();
        return PluggyResult<PluggyStabilityView>.Ok(await imports.GetStabilityAsync(workspaceId, connectionId, time.GetUtcNow(), ct));
    }

    /// <summary>
    /// Depois da coleta, as transações efetivadas das contas integradas viram movimentos do núcleo financeiro. Uma falha aqui não invalida a
    /// coleta (os dados importados já estão salvos e o reprocessamento é idempotente): fica registrada na própria coleta.
    /// </summary>
    private async Task ApplyFinancialAsync(PluggyItemLink link, PluggySyncRun run, Guid userId, CancellationToken ct)
    {
        if (run.AccountsSynced == 0) return;
        try { await engine.ApplyAsync(link.WorkspaceId, userId, time.GetUtcNow(), ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            run.AddError("FinancialApplyFailed");
        }
    }

    // ---- Coleta --------------------------------------------------------------------------------------------------

    private async Task RunAsync(PluggyItemLink link, PluggySyncRun run, PluggyItem? knownItem, Action<PluggyItem> onItem, CancellationToken ct)
    {
        var item = knownItem ?? await client.GetItemAsync(link.PluggyItemId, ct);
        onItem(item);
        if (PluggyPocService.StateOf(item, time.GetUtcNow()) == PluggyConnectionState.ConsentExpired)
        {
            // Consentimento vencido: não consulta nada, para não confundir falta de autorização com ausência de dados.
            run.AddError("ConsentExpired");
            await financial.SetConnectionStatusAsync(link.WorkspaceId, link.Id, PluggyAccountLinkStatus.ConsentExpired, ct);
            return;
        }
        var accounts = await client.ListAccountsAsync(link.PluggyItemId, ct);
        run.SetAccounts(accounts.Count);
        // Valores informados pelo banco ficam separados do saldo calculado; só atualizam as contas integradas.
        await financial.RefreshObservedAsync(link.WorkspaceId, link.Id, accounts.ToDictionary(a => a.Id, PluggyAccountLinkService.Observed, StringComparer.Ordinal),
            PluggyAccountLinkStatus.Active, ct);
        var integratedCards = (await financial.ListLinksAsync(link.WorkspaceId, link.Id, ct)).Where(l => l.Kind == PluggyLinkedAccountKind.Card).ToDictionary(l => l.PluggyAccountId, StringComparer.Ordinal);
        var rateLimited = false;
        foreach (var account in accounts)
        {
            if (rateLimited) { run.AddError("RateLimited:skipped"); continue; }
            rateLimited = await SyncAccountAsync(link, run, account, ct);
            if (!rateLimited && integratedCards.TryGetValue(account.Id, out var cardLink)) await SyncBillsAsync(cardLink, run, account, ct);
        }
    }

    /// <summary>Faturas do cartão integrado: evidência para localizar compras na fatura certa e fechar a fatura oficial. Falha aqui não invalida a coleta de transações.</summary>
    private async Task SyncBillsAsync(PluggyAccountLink cardLink, PluggySyncRun run, PluggyAccount account, CancellationToken ct)
    {
        var facts = new List<PluggyBillFact>();
        try
        {
            for (var page = 1; page <= Math.Max(1, options.BillMaxPages); page++)
            {
                var result = await client.ListBillsAsync(account.Id, page, 50, ct);
                facts.AddRange(result.Items.Select(b => new PluggyBillFact(b.Id, b.ClosingDate is { } c ? PluggyFinancialMapper.BusinessDate(c) : null,
                    b.DueDate is { } d ? PluggyFinancialMapper.BusinessDate(d) : null, b.TotalAmount, b.TotalAmountCurrencyCode)
                {
                    FinanceChargesTotal = b.FinanceCharges.Count == 0 ? null : b.FinanceCharges.Where(x => IsBrl(x.CurrencyCode)).Sum(x => x.Amount ?? 0m),
                    MinimumPayment = b.MinimumPaymentAmount,
                    // Só pagamentos completos (valor positivo com no máximo duas casas, data e moeda BRL): referência estruturada para reconhecer a saída bancária.
                    Payments = b.Payments.Where(p => p.Amount is > 0m && p.PaymentDate is not null && IsBrl(p.CurrencyCode) && PluggyFinancialMapper.ExactCents(p.Amount.Value) is not null)
                        .Select(p => new PluggyBillPaymentFact(PluggyFinancialMapper.ExactCents(p.Amount!.Value)!.Value, PluggyFinancialMapper.BusinessDate(p.PaymentDate!.Value), p.PaymentMode, p.ValueType)).ToList(),
                }));
                if (result.TotalPages is not { } pages || page >= pages) break;
                if (options.SyncPageDelayMilliseconds > 0) await Task.Delay(options.SyncPageDelayMilliseconds, ct);
            }
        }
        catch (PluggyException error) { run.AddError($"{error.Kind}:bills"); }
        await financial.UpsertBillsAsync(cardLink.WorkspaceId, cardLink.Id, facts, time.GetUtcNow(), ct);
    }

    private static bool IsBrl(string? currency) => string.IsNullOrWhiteSpace(currency) || string.Equals(currency, "BRL", StringComparison.OrdinalIgnoreCase);

    /// <returns>Verdadeiro se a Pluggy continuou limitando as requisições após as tentativas.</returns>
    private async Task<bool> SyncAccountAsync(PluggyItemLink link, PluggySyncRun run, PluggyAccount account, CancellationToken ct)
    {
        var label = $"{account.Name ?? account.MarketingName ?? account.Type} {PluggyPocService.MaskTail(account.Number)}".Trim();
        var snapshots = new List<PluggyTransactionSnapshot>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;
        var rateLimited = false;
        string? after = null;
        for (var page = 1; ; page++)
        {
            PluggyCursorPage<PluggyTransaction> result;
            try { result = await FetchPageAsync(account.Id, after, ct); }
            catch (PluggyException error)
            {
                complete = false;
                rateLimited = error.Kind == PluggyFailureKind.RateLimited;
                run.AddError($"{error.Kind}:transactions");
                break;
            }
            snapshots.AddRange(result.Items.Select(t => ToSnapshot(t, account, label)));
            if (result.NextCursor is null) break;
            // Cursor repetido ou limite de páginas: a coleta fica incompleta e nada é dado como ausente.
            if (!cursors.Add(result.NextCursor) || page >= Math.Max(1, options.SyncMaxPagesPerAccount))
            {
                complete = false;
                run.AddError("Truncated:transactions");
                break;
            }
            after = result.NextCursor;
            if (options.SyncPageDelayMilliseconds > 0) await Task.Delay(options.SyncPageDelayMilliseconds, ct);
        }

        if (snapshots.Count == 0 && !complete) return rateLimited;
        var applied = await imports.ApplyAccountAsync(link.WorkspaceId, link.Id, account.Id, snapshots, complete, run.Id, time.GetUtcNow(), ct);
        if (complete) await financial.MarkSyncedAsync(link.WorkspaceId, link.Id, account.Id, time.GetUtcNow(), ct);
        run.AddAccount(applied.Seen, applied.New, applied.Updated, applied.Unchanged, applied.Missing);
        if (applied.DuplicatePayloadIds > 0) run.AddError($"DuplicateIdsInPayload:{applied.DuplicatePayloadIds}");
        return rateLimited;
    }

    /// <summary>Uma página, com espera e nova tentativa em 429 (respeitando Retry-After, limitado) e uma repetição para falhas transitórias.</summary>
    private async Task<PluggyCursorPage<PluggyTransaction>> FetchPageAsync(string accountId, string? after, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await client.ListTransactionsAsync(accountId, null, null, after, ct); }
            catch (PluggyException error) when (CanRetry(error, attempt))
            {
                var wait = error.RetryAfter ?? TimeSpan.FromSeconds(Math.Max(0, options.SyncRateLimitDelaySeconds));
                var cap = TimeSpan.FromSeconds(Math.Max(0, options.SyncMaxRateLimitWaitSeconds));
                await Task.Delay(wait > cap ? cap : wait, ct);
            }
        }
    }

    private bool CanRetry(PluggyException error, int attempt) => error.Kind switch
    {
        PluggyFailureKind.RateLimited => attempt < Math.Max(0, options.SyncRateLimitRetries),
        PluggyFailureKind.Timeout or PluggyFailureKind.Unavailable => attempt < 1,
        _ => false,
    };

    internal static PluggyTransactionSnapshot ToSnapshot(PluggyTransaction t, PluggyAccount account, string accountLabel) => new(t.Id, account.Id, account.Type, accountLabel,
        t.Date, t.Amount, t.CurrencyCode, t.Description, t.DescriptionRaw, t.Type, StatusOf(t.Status), t.OperationType, t.Category,
        t.Card?.InstallmentNumber, t.Card?.TotalInstallments, t.Card?.BillId, t.Card?.CardLast4, t.Payment?.PaymentMethod);

    private static PluggyTransactionStatus StatusOf(string? status) => status?.ToUpperInvariant() switch
    {
        "PENDING" => PluggyTransactionStatus.Pending,
        "POSTED" => PluggyTransactionStatus.Posted,
        _ => PluggyTransactionStatus.Unknown,
    };

    public static PluggySyncRunView ToView(PluggySyncRun r) => new(r.Id, r.ConnectionId, r.StartedAt, r.FinishedAt, r.Status.ToString(), r.AccountsTotal,
        r.AccountsSynced, r.Seen, r.Created, r.Updated, r.Unchanged, r.Missing, r.Errors, r.ErrorSummary, r.Trigger.ToString());
}

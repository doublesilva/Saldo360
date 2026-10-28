using Saldo360.Application.Workspaces;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

/// <summary>
/// Sincronização manual de transações da Pluggy para tabelas próprias (T09.2). Não cria lançamentos, previsões nem altera saldos:
/// o único efeito é gravar/atualizar <see cref="PluggyImportedTransaction"/>, o histórico de mudanças e o registro da coleta.
/// </summary>
public sealed class PluggyTransactionSyncService(IFinancialWorkspaceRepository workspaces, IPluggyItemLinkRepository links, IPluggyImportRepository imports,
    IPluggyClient client, PluggyOptions options, TimeProvider time)
{
    public const int MaxPageSize = 200;

    public async Task<bool> CanImportAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        options.Enabled && options.ImportEnabled && await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is not null;

    public async Task<PluggyResult<PluggySyncRunView>> SyncAsync(Guid workspaceId, Guid userId, Guid connectionId, CancellationToken ct)
    {
        if (!await CanImportAsync(workspaceId, userId, ct) || await links.FindAsync(workspaceId, connectionId, ct) is not { } link)
            return PluggyResult<PluggySyncRunView>.NotFound();
        var run = await imports.TryStartRunAsync(workspaceId, connectionId, userId, time.GetUtcNow(), TimeSpan.FromMinutes(Math.Max(1, options.SyncStaleMinutes)), ct);
        if (run is null) return new(PluggyAccess.Conflict, Message: "Já existe uma sincronização em andamento para esta conexão.");

        PluggyException? fatal = null;
        var failed = false;
        try { await RunAsync(link, run, ct); }
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
        }
        return fatal is not null && failed
            ? new(PluggyAccess.IntegrationFailure, ToView(run), fatal.Kind, fatal.Message)
            : PluggyResult<PluggySyncRunView>.Ok(ToView(run));
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

    // ---- Coleta --------------------------------------------------------------------------------------------------

    private async Task RunAsync(PluggyItemLink link, PluggySyncRun run, CancellationToken ct)
    {
        var item = await client.GetItemAsync(link.PluggyItemId, ct);
        if (PluggyPocService.StateOf(item, time.GetUtcNow()) == PluggyConnectionState.ConsentExpired)
        {
            // Consentimento vencido: não consulta nada, para não confundir falta de autorização com ausência de dados.
            run.AddError("ConsentExpired");
            return;
        }
        var accounts = await client.ListAccountsAsync(link.PluggyItemId, ct);
        run.SetAccounts(accounts.Count);
        var rateLimited = false;
        foreach (var account in accounts)
        {
            if (rateLimited) { run.AddError("RateLimited:skipped"); continue; }
            rateLimited = await SyncAccountAsync(link, run, account, ct);
        }
    }

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
        r.AccountsSynced, r.Seen, r.Created, r.Updated, r.Unchanged, r.Missing, r.Errors, r.ErrorSummary);
}

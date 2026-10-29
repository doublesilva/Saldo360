using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Pluggy;

[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PluggySyncEndpointsTests(PostgresFixture postgres) : PluggyApiTestBase(postgres)
{
    private static PluggyTransaction Tx(string id, string account, string day, decimal amount, string status = "POSTED", string? operation = "PIX", string? description = null, PluggyCardMetadata? card = null, string? type = null) =>
        FakePluggyClient.Transaction(id, account, day, amount, status, card) with { OperationType = operation, Description = description ?? "Mercado", DescriptionRaw = description ?? "MERCADO", Type = type ?? (amount < 0 ? "DEBIT" : "CREDIT") };

    private async Task<(string Session, Guid Workspace, HttpClient Client, Guid Connection)> ArrangeAsync(string user, bool importEnabled = true, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null)
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var client = LabClient(importEnabled, configure);
        var connection = await LinkAsync(client, session, workspace.Id, ItemA, "Itaú PF");
        Pluggy.TransactionPageSize = 2;
        Pluggy.TransactionsByAccount[BankA] = [Tx("b1", BankA, "2026-10-01", -10.25m), Tx("b2", BankA, "2026-10-02", 99.9m, operation: "PIX", description: "SALARIO"), Tx("b3", BankA, "2026-10-03", -1m, "PENDING"),
            Tx("b4", BankA, "2026-10-04", -20m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "PARA POUPANCA"), Tx("b5", BankA, "2026-10-05", -7m)];
        Pluggy.TransactionsByAccount[CardA] = [Tx("c1", CardA, "2026-10-02", 150m, operation: "PAGAMENTO", description: "LOJA", type: "DEBIT"),
            Tx("c2", CardA, "2026-10-03", 80m, operation: "PAGAMENTO", description: "TV", card: new(2, 10, null, null, "bill-1", "2222", null, null), type: "DEBIT"),
            Tx("c3", CardA, "2026-10-10", 1500m, "POSTED", "PAGAMENTO_FATURA", "PAGAMENTO FATURA", type: "CREDIT")];
        return (session, workspace.Id, client, connection);
    }

    private Task<HttpResponseMessage> SyncAsync(HttpClient client, string session, Guid workspace, Guid connection) =>
        Send(client, HttpMethod.Post, $"{Root(workspace)}/connections/{connection}/sync", session);

    private async Task<JsonElement> ListAsync(HttpClient client, string session, Guid workspace, string query = "")
    {
        var response = await Send(client, HttpMethod.Get, $"{Root(workspace)}/imported-transactions{query}", session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOf(response);
    }

    [Fact]
    public async Task ImportIsDisabledByDefaultEvenWhenTheLaboratoryIsEnabled()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-disabled", importEnabled: false);

        Assert.Equal(HttpStatusCode.NotFound, (await SyncAsync(client, session, workspace, connection)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(workspace)}/imported-transactions", session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(workspace)}/connections/{connection}/stability", session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(workspace)}/connections/{connection}/sync-runs", session)).StatusCode);
        var status = await JsonOf(await Send(client, HttpMethod.Get, $"{Root(workspace)}/status", session));
        Assert.False(status.GetProperty("importEnabled").GetBoolean());
        await using var db = Db();
        Assert.Equal(0, await db.PluggyImportedTransactions.CountAsync());
        Assert.Equal(0, await db.PluggySyncRuns.CountAsync());
        Assert.DoesNotContain(Pluggy.Calls, c => c.StartsWith("transactions:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FirstSyncReadsEveryPageOfEveryAccountAndStoresClassifiedTransactions()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-first");

        var response = await SyncAsync(client, session, workspace, connection);
        var run = await JsonOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal((2, 2, 8, 8, 0, 0), (run.GetProperty("accountsTotal").GetInt32(), run.GetProperty("accountsSynced").GetInt32(), run.GetProperty("seen").GetInt32(),
            run.GetProperty("created").GetInt32(), run.GetProperty("updated").GetInt32(), run.GetProperty("missing").GetInt32()));
        Assert.Equal(3, Pluggy.Calls.Count(c => c.StartsWith($"transactions:{BankA}", StringComparison.Ordinal)));
        await using var db = Db();
        var rows = await db.PluggyImportedTransactions.AsNoTracking().ToListAsync();
        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Equal((workspace, connection), (r.WorkspaceId, r.ConnectionId)));
        PluggyTransactionClass Class(string id) => rows.Single(r => r.PluggyTransactionId == id).Classification;
        Assert.Equal(PluggyTransactionClass.PotentialExpense, Class("b1"));
        Assert.Equal(PluggyTransactionClass.PotentialIncome, Class("b2"));
        Assert.Equal(PluggyTransactionClass.PotentialTransfer, Class("b4"));
        Assert.Equal(PluggyTransactionClass.CardPurchase, Class("c1"));
        Assert.Equal(PluggyTransactionClass.CardInstallment, Class("c2"));
        Assert.Equal(PluggyTransactionClass.PotentialTransfer, Class("c3"));
        Assert.Equal(PluggyTransactionStatus.Pending, rows.Single(r => r.PluggyTransactionId == "b3").Status);
        Assert.Equal((2, 10, "bill-1", "2222"), (rows.Single(r => r.PluggyTransactionId == "c2").InstallmentNumber, rows.Single(r => r.PluggyTransactionId == "c2").TotalInstallments,
            rows.Single(r => r.PluggyTransactionId == "c2").BillId, rows.Single(r => r.PluggyTransactionId == "c2").CardLast4));
        Assert.Equal(-10.25m, rows.Single(r => r.PluggyTransactionId == "b1").Amount);
        Assert.Equal("Itaú PF", (await LabLabel(db, connection)));
    }

    private static async Task<string?> LabLabel(Saldo360.Infrastructure.Persistence.Saldo360DbContext db, Guid connection) =>
        (await db.PluggyItemLinks.AsNoTracking().SingleAsync(l => l.Id == connection)).Label;

    [Fact]
    public async Task RepeatedSyncDoesNotDuplicateAndCountsEverythingAsUnchanged()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-repeat");
        await SyncAsync(client, session, workspace, connection);

        var second = await JsonOf(await SyncAsync(client, session, workspace, connection));
        var third = await JsonOf(await SyncAsync(client, session, workspace, connection));

        foreach (var run in new[] { second, third })
            Assert.Equal((8, 0, 0, 8, 0), (run.GetProperty("seen").GetInt32(), run.GetProperty("created").GetInt32(), run.GetProperty("updated").GetInt32(),
                run.GetProperty("unchanged").GetInt32(), run.GetProperty("missing").GetInt32()));
        await using var db = Db();
        Assert.Equal(8, await db.PluggyImportedTransactions.CountAsync());
        Assert.Equal(0, await db.PluggyTransactionChanges.CountAsync());
        Assert.Equal(3, await db.PluggySyncRuns.CountAsync());
        Assert.All(await db.PluggyImportedTransactions.ToListAsync(), t => Assert.True(t.LastObservedAt >= t.FirstImportedAt && t.LastChangedAt is null));
    }

    [Fact]
    public async Task ChangedDataAndPendingToPostedUpdateTheSameRowAndKeepMinimalHistory()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-update");
        await SyncAsync(client, session, workspace, connection);
        Guid idBefore;
        await using (var before = Db()) idBefore = (await before.PluggyImportedTransactions.SingleAsync(t => t.PluggyTransactionId == "b3")).Id;

        Pluggy.TransactionsByAccount[BankA][2] = Tx("b3", BankA, "2026-10-03", -1.5m, "POSTED");
        Pluggy.TransactionsByAccount[BankA][0] = Tx("b1", BankA, "2026-10-01", -10.25m, description: "MERCADO CENTRAL");
        var run = await JsonOf(await SyncAsync(client, session, workspace, connection));

        Assert.Equal((0, 2, 6), (run.GetProperty("created").GetInt32(), run.GetProperty("updated").GetInt32(), run.GetProperty("unchanged").GetInt32()));
        await using var db = Db();
        var updated = await db.PluggyImportedTransactions.AsNoTracking().SingleAsync(t => t.PluggyTransactionId == "b3");
        Assert.Equal(idBefore, updated.Id);
        Assert.Equal((PluggyTransactionStatus.Posted, -1.5m), (updated.Status, updated.Amount));
        Assert.NotNull(updated.LastChangedAt);
        Assert.Equal(8, await db.PluggyImportedTransactions.CountAsync());
        var change = await db.PluggyTransactionChanges.AsNoTracking().SingleAsync(c => c.TransactionId == updated.Id);
        Assert.Equal((PluggyTransactionStatus.Pending, PluggyTransactionStatus.Posted, -1m, -1.5m), (change.PreviousStatus, change.NewStatus, change.PreviousAmount, change.NewAmount));
        Assert.Equal("amount,status", change.ChangedFields);
        Assert.Contains("description", (await db.PluggyTransactionChanges.AsNoTracking().SingleAsync(c => c.TransactionId != updated.Id)).ChangedFields);

        var stability = await JsonOf(await Send(client, HttpMethod.Get, $"{Root(workspace)}/connections/{connection}/stability", session));
        Assert.Equal(2, stability.GetProperty("completedRuns").GetInt32());
        Assert.Equal((8, 8, 0), (stability.GetProperty("previouslyImported").GetInt32(), stability.GetProperty("preservedInLatest").GetInt32(), stability.GetProperty("notSeenInLatest").GetInt32()));
        Assert.Equal((1, 1, 1), (stability.GetProperty("pendingToPostedInLatest").GetInt32(), stability.GetProperty("pendingToPostedTotal").GetInt32(), stability.GetProperty("amountChangesTotal").GetInt32()));
        Assert.Equal(1, stability.GetProperty("descriptionChangesTotal").GetInt32());
        Assert.Equal(0, stability.GetProperty("pendingNow").GetInt32());
    }

    [Fact]
    public async Task TransactionsMissingFromACompleteCollectionAreKeptAndOnlyCountedAsMissing()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-missing");
        await SyncAsync(client, session, workspace, connection);
        var removed = Pluggy.TransactionsByAccount[BankA][1];
        Pluggy.TransactionsByAccount[BankA].RemoveAt(1);

        var run = await JsonOf(await SyncAsync(client, session, workspace, connection));
        Assert.Equal((1, 0), (run.GetProperty("missing").GetInt32(), run.GetProperty("created").GetInt32()));
        await using (var db = Db())
        {
            var row = await db.PluggyImportedTransactions.AsNoTracking().SingleAsync(t => t.PluggyTransactionId == "b2");
            Assert.Equal((1, 99.9m), (row.MissingSyncCount, row.Amount));
            Assert.NotNull(row.FirstMissingAt);
            Assert.Equal(8, await db.PluggyImportedTransactions.CountAsync());
        }
        var stability = await JsonOf(await Send(client, HttpMethod.Get, $"{Root(workspace)}/connections/{connection}/stability", session));
        Assert.Equal((1, 1), (stability.GetProperty("notSeenInLatest").GetInt32(), stability.GetProperty("missingNow").GetInt32()));

        // Voltando a aparecer, a ausência é zerada e nada é duplicado.
        Pluggy.TransactionsByAccount[BankA].Insert(1, removed);
        await SyncAsync(client, session, workspace, connection);
        await using var after = Db();
        var again = await after.PluggyImportedTransactions.AsNoTracking().SingleAsync(t => t.PluggyTransactionId == "b2");
        Assert.Equal(0, again.MissingSyncCount);
        Assert.Null(again.FirstMissingAt);
        Assert.Equal(8, await after.PluggyImportedTransactions.CountAsync());
    }

    [Fact]
    public async Task NewLateArrivalsAndFutureDatedRecordsAreReported()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-late");
        await SyncAsync(client, session, workspace, connection);
        Pluggy.TransactionsByAccount[BankA].Add(Tx("late", BankA, "2026-09-01", -3m));
        Pluggy.TransactionsByAccount[BankA].Add(Tx("future", BankA, "2099-01-01", -4m, "PENDING"));
        var run = await JsonOf(await SyncAsync(client, session, workspace, connection));
        Assert.Equal(2, run.GetProperty("created").GetInt32());

        var stability = await JsonOf(await Send(client, HttpMethod.Get, $"{Root(workspace)}/connections/{connection}/stability", session));
        Assert.Equal((2, 1, 1), (stability.GetProperty("newInLatest").GetInt32(), stability.GetProperty("futureDated").GetInt32(), stability.GetProperty("futureDatedPending").GetInt32()));
        Assert.Equal(1, stability.GetProperty("newWithDateBeforePreviousRun").GetInt32());
    }

    [Fact]
    public async Task IncompleteCollectionNeverMarksAnythingMissing()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-incomplete");
        await SyncAsync(client, session, workspace, connection);
        // Falha na segunda página da conta bancária: só vêm b1 e b2.
        Pluggy.Failures[$"transactions-after:{BankA}"] = new PluggyException(PluggyFailureKind.Unavailable, 503);

        var response = await SyncAsync(client, session, workspace, connection);
        var run = await JsonOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CompletedWithErrors", run.GetProperty("status").GetString());
        Assert.Equal(0, run.GetProperty("missing").GetInt32());
        Assert.Contains("Unavailable:transactions", run.GetProperty("errorSummary").GetString());
        await using var db = Db();
        Assert.Equal(8, await db.PluggyImportedTransactions.CountAsync());
        Assert.All(await db.PluggyImportedTransactions.ToListAsync(), t => Assert.Equal(0, t.MissingSyncCount));
    }

    [Fact]
    public async Task RateLimitIsWaitedOutAndRetriedWithoutLosingData()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-ratelimit");
        Pluggy.TransientFailures[BankA] = new Queue<PluggyException>([new PluggyException(PluggyFailureKind.RateLimited, 429, TimeSpan.Zero), new PluggyException(PluggyFailureKind.RateLimited, 429)]);

        var run = await JsonOf(await SyncAsync(client, session, workspace, connection));

        Assert.Equal(("Completed", 8, 0), (run.GetProperty("status").GetString(), run.GetProperty("created").GetInt32(), run.GetProperty("errors").GetInt32()));
        Assert.Equal(3, Pluggy.Calls.Count(c => c == $"transactions:{BankA}:"));
    }

    [Fact]
    public async Task PersistentRateLimitStopsTheRunReportsItAndKeepsWhatWasAlreadySaved()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-ratelimit-persistent");
        await SyncAsync(client, session, workspace, connection);
        Pluggy.Failures[$"transactions:{BankA}"] = new PluggyException(PluggyFailureKind.RateLimited, 429);

        var callsBefore = Pluggy.Calls.Count;
        var response = await SyncAsync(client, session, workspace, connection);
        var body = await response.Content.ReadAsStringAsync();
        var run = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Failed", run.GetProperty("status").GetString());
        Assert.Contains("RateLimited:transactions", run.GetProperty("errorSummary").GetString());
        Assert.Contains("RateLimited:skipped", run.GetProperty("errorSummary").GetString());
        Assert.DoesNotContain("segredo-que-nao-pode-vazar", body);
        // 1 tentativa + 2 novas tentativas na primeira conta; a conta seguinte nem é consultada.
        var calls = Pluggy.Calls.Skip(callsBefore).Where(c => c.StartsWith("transactions:", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, calls.Count(c => c == $"transactions:{BankA}:"));
        Assert.DoesNotContain(calls, c => c.StartsWith($"transactions:{CardA}", StringComparison.Ordinal));
        await using var db = Db();
        var last = await db.PluggySyncRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).FirstAsync();
        Assert.Equal(PluggySyncStatus.Failed, last.Status);
        Assert.NotNull(last.FinishedAt);
        // O que já estava salvo continua intacto.
        Assert.Equal(8, await db.PluggyImportedTransactions.CountAsync());
        Assert.All(await db.PluggyImportedTransactions.ToListAsync(), t => Assert.Equal(0, t.MissingSyncCount));
    }

    [Fact]
    public async Task SimultaneousSyncOfTheSameConnectionIsRejectedWhileAnotherOneRuns()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-concurrent");
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Pluggy.BeforeTransactions = async _ => { entered.TrySetResult(); await release.Task; };

        var first = SyncAsync(client, session, workspace, connection);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = await SyncAsync(client, session, workspace, connection);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await using (var db = Db()) Assert.Equal(1, await db.PluggySyncRuns.CountAsync(r => r.Status == PluggySyncStatus.Running));

        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Pluggy.BeforeTransactions = null;
        Assert.Equal(HttpStatusCode.OK, (await SyncAsync(client, session, workspace, connection)).StatusCode);
        await using var after = Db();
        Assert.Equal(0, await after.PluggySyncRuns.CountAsync(r => r.Status == PluggySyncStatus.Running));
        Assert.Equal(8, await after.PluggyImportedTransactions.CountAsync());
    }

    [Fact]
    public async Task AbandonedRunDoesNotBlockTheConnectionForever()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-stale");
        await using (var db = Db())
        {
            var link = await db.PluggyItemLinks.AsNoTracking().SingleAsync(l => l.Id == connection);
            db.PluggySyncRuns.Add(PluggySyncRun.Start(workspace, connection, link.UserId, DateTimeOffset.UtcNow.AddHours(-3)));
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await SyncAsync(client, session, workspace, connection)).StatusCode);
        await using var after = Db();
        Assert.Equal(0, await after.PluggySyncRuns.CountAsync(r => r.Status == PluggySyncStatus.Running));
        Assert.Equal(1, await after.PluggySyncRuns.CountAsync(r => r.ErrorSummary == "Abandoned" && r.Status == PluggySyncStatus.Failed));
    }

    [Fact]
    public async Task FreshRunningRecordBlocksANewSync()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-running");
        await using (var db = Db())
        {
            var link = await db.PluggyItemLinks.AsNoTracking().SingleAsync(l => l.Id == connection);
            db.PluggySyncRuns.Add(PluggySyncRun.Start(workspace, connection, link.UserId, DateTimeOffset.UtcNow.AddMinutes(-2)));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await SyncAsync(client, session, workspace, connection)).StatusCode);
        Assert.DoesNotContain(Pluggy.Calls, c => c.StartsWith("transactions:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiredConsentIsReportedWithoutQueryingTransactions()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-consent");
        Pluggy.Items[ItemA] = FakePluggyClient.Item(ItemA, status: "LOGIN_ERROR", consentExpiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var run = await JsonOf(await SyncAsync(client, session, workspace, connection));

        Assert.Equal(("Failed", 0, "ConsentExpired"), (run.GetProperty("status").GetString(), run.GetProperty("seen").GetInt32(), run.GetProperty("errorSummary").GetString()));
        Assert.DoesNotContain(Pluggy.Calls, c => c.StartsWith("transactions:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedIdInsideOnePayloadIsStoredOnceAndReported()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-duplicate-id");
        Pluggy.TransactionPageSize = 100;
        Pluggy.TransactionsByAccount[BankA].Add(Tx("b1", BankA, "2026-10-01", -10.25m));

        var run = await JsonOf(await SyncAsync(client, session, workspace, connection));

        Assert.Equal(8, run.GetProperty("created").GetInt32());
        Assert.Contains("DuplicateIdsInPayload:1", run.GetProperty("errorSummary").GetString());
        await using var db = Db();
        Assert.Equal(8, await db.PluggyImportedTransactions.CountAsync());
    }

    [Fact]
    public async Task ListFiltersByAccountPeriodStatusAndClassificationAndShowsCounters()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-list");
        await SyncAsync(client, session, workspace, connection);
        Pluggy.TransactionsByAccount[BankA][2] = Tx("b3", BankA, "2026-10-03", -1m, "POSTED");
        Pluggy.TransactionsByAccount[BankA].Add(Tx("n1", BankA, "2026-10-06", -5m, "PENDING"));
        await SyncAsync(client, session, workspace, connection);

        var all = await ListAsync(client, session, workspace);
        Assert.Equal(9, all.GetProperty("total").GetInt32());
        var counters = all.GetProperty("counters");
        Assert.Equal((9, 1, 1, 1), (counters.GetProperty("total").GetInt32(), counters.GetProperty("new").GetInt32(), counters.GetProperty("updated").GetInt32(), counters.GetProperty("pending").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, counters.GetProperty("lastSyncAt").ValueKind);
        var byDate = all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("date").GetDateTimeOffset()).ToList();
        Assert.Equal(byDate.OrderByDescending(d => d), byDate);
        Assert.True(all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("pluggyTransactionId").GetString() == "n1").GetProperty("isNewInLastSync").GetBoolean());
        Assert.True(all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("pluggyTransactionId").GetString() == "b3").GetProperty("changedInLastSync").GetBoolean());

        Assert.Equal(6, (await ListAsync(client, session, workspace, $"?accountId={BankA}")).GetProperty("total").GetInt32());
        Assert.Equal(3, (await ListAsync(client, session, workspace, $"?accountId={CardA}")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await ListAsync(client, session, workspace, "?status=Pending")).GetProperty("total").GetInt32());
        Assert.Equal(8, (await ListAsync(client, session, workspace, "?status=Posted")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await ListAsync(client, session, workspace, "?classification=CardInstallment")).GetProperty("total").GetInt32());
        Assert.Equal(3, (await ListAsync(client, session, workspace, $"?accountId={BankA}&from=2026-10-02&to=2026-10-04")).GetProperty("total").GetInt32());
        var paged = await ListAsync(client, session, workspace, "?page=2&pageSize=4");
        Assert.Equal((9, 2, 4, 4), (paged.GetProperty("total").GetInt32(), paged.GetProperty("page").GetInt32(), paged.GetProperty("pageSize").GetInt32(), paged.GetProperty("items").GetArrayLength()));
        // Contadores ignoram os filtros de status/classificação.
        Assert.Equal(9, (await ListAsync(client, session, workspace, "?status=Pending")).GetProperty("counters").GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("status=Bogus")]
    [InlineData("classification=Bogus")]
    [InlineData("from=01/10/2026")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=5000")]
    [InlineData("page=0")]
    public async Task ListRejectsInvalidFilters(string query)
    {
        var (session, workspace, client, _) = await ArrangeAsync("sync-invalid-" + query.Split('=')[0] + query.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Get, $"{Root(workspace)}/imported-transactions?{query}", session)).StatusCode);
    }

    [Fact]
    public async Task SameCapturedMovementWithDifferentIdsIsFlaggedAsPossibleDuplicate()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-dups");
        Pluggy.TransactionPageSize = 100;
        Pluggy.TransactionsByAccount[BankA] = [Tx("d1", BankA, "2026-10-01", -30m, description: "PADARIA"), Tx("d2", BankA, "2026-10-01", -30m, description: "PADARIA"), Tx("d3", BankA, "2026-10-01", -31m, description: "PADARIA")];
        Pluggy.TransactionsByAccount[CardA] = [];
        await SyncAsync(client, session, workspace, connection);

        var items = (await ListAsync(client, session, workspace)).GetProperty("items").EnumerateArray().ToList();
        JsonElement Item(string id) => items.Single(i => i.GetProperty("pluggyTransactionId").GetString() == id);
        Assert.Equal(2, Item("d1").GetProperty("possibleDuplicateCount").GetInt32());
        Assert.Equal(Item("d1").GetProperty("possibleDuplicateKey").GetString(), Item("d2").GetProperty("possibleDuplicateKey").GetString());
        Assert.Equal(1, Item("d3").GetProperty("possibleDuplicateCount").GetInt32());
        var stability = await JsonOf(await Send(client, HttpMethod.Get, $"{Root(workspace)}/connections/{connection}/stability", session));
        Assert.Equal(1, stability.GetProperty("possibleDuplicateGroups").GetInt32());
        // Ambas continuam armazenadas: repetições legítimas não são removidas.
        await using var db = Db();
        Assert.Equal(3, await db.PluggyImportedTransactions.CountAsync());
    }

    [Fact]
    public async Task DataIsIsolatedByWorkspaceAndConnection()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-iso-a");
        var other = await SignInAsync("sync-iso-b");
        var otherWorkspace = await CreateWorkspaceAsync(other);
        var otherConnection = await LinkAsync(client, other, otherWorkspace.Id, ItemB, "Nubank");
        Pluggy.TransactionsByAccount[BankB] = [Tx("n1", BankB, "2026-10-01", -2m)];
        await SyncAsync(client, session, workspace, connection);
        await SyncAsync(client, other, otherWorkspace.Id, otherConnection);

        Assert.Equal(8, (await ListAsync(client, session, workspace)).GetProperty("total").GetInt32());
        var othersList = await ListAsync(client, other, otherWorkspace.Id);
        Assert.Equal(1, othersList.GetProperty("total").GetInt32());
        Assert.Equal("n1", othersList.GetProperty("items")[0].GetProperty("pluggyTransactionId").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await SyncAsync(client, other, otherWorkspace.Id, connection)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SyncAsync(client, other, workspace, connection)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(otherWorkspace.Id)}/imported-transactions?connectionId={connection}", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(otherWorkspace.Id)}/connections/{connection}/stability", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(otherWorkspace.Id)}/connections/{connection}/sync-runs", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(workspace)}/imported-transactions", other)).StatusCode);
        // A mesma sincronização não mistura contas entre conexões.
        await using var db = Db();
        Assert.Equal(1, await db.PluggyImportedTransactions.CountAsync(t => t.WorkspaceId == otherWorkspace.Id));
    }

    [Fact]
    public async Task SyncRequiresAuthenticationAndCsrf()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-auth");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"{Root(workspace)}/connections/{connection}/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Root(workspace)}/imported-transactions")).StatusCode);
        using var withoutCsrf = new HttpRequestMessage(HttpMethod.Post, $"{Root(workspace)}/connections/{connection}/sync");
        withoutCsrf.Headers.Add("Cookie", session);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(withoutCsrf)).StatusCode);
        Assert.DoesNotContain(Pluggy.Calls, c => c.StartsWith("transactions:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncRunsAreListedNewestFirstWithAggregatedResults()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-runs");
        await SyncAsync(client, session, workspace, connection);
        await SyncAsync(client, session, workspace, connection);

        var runs = await JsonOf(await Send(client, HttpMethod.Get, $"{Root(workspace)}/connections/{connection}/sync-runs", session));
        Assert.Equal(2, runs.GetArrayLength());
        Assert.Equal((0, 8), (runs[0].GetProperty("created").GetInt32(), runs[0].GetProperty("unchanged").GetInt32()));
        Assert.Equal((8, 0), (runs[1].GetProperty("created").GetInt32(), runs[1].GetProperty("unchanged").GetInt32()));
    }

    [Fact]
    public async Task ImportNeverChangesOperationalFinancialDataOrBalances()
    {
        var (session, workspace, client, connection) = await ArrangeAsync("sync-readonly");
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace}/accounts", session,
            new { name = "Conta", type = "Checking", financialScope = "PF", openingBalance = "100.00", openingBalanceDate = "2026-01-01" });
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);
        var accountId = JsonDocument.Parse(await account.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var balanceResponse = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{workspace}/accounts/{accountId}/balance", session);
        Assert.Equal(HttpStatusCode.OK, balanceResponse.StatusCode);
        var balanceBefore = await balanceResponse.Content.ReadAsStringAsync();
        var before = await Snapshot();

        await SyncAsync(client, session, workspace, connection);
        Pluggy.TransactionsByAccount[BankA][2] = Tx("b3", BankA, "2026-10-03", -1m, "POSTED");
        await SyncAsync(client, session, workspace, connection);

        Assert.Equal(before, await Snapshot());
        Assert.Equal(balanceBefore, await (await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{workspace}/accounts/{accountId}/balance", session)).Content.ReadAsStringAsync());
        Assert.All(Pluggy.Calls, call => Assert.DoesNotContain("pay", call, StringComparison.OrdinalIgnoreCase));

        async Task<int[]> Snapshot()
        {
            await using var db = Db();
            return
            [
                await db.FinancialAccounts.CountAsync(), await db.FinancialTransactions.CountAsync(), await db.FinancialTransfers.CountAsync(),
                await db.FinancialPlannedTransactions.CountAsync(), await db.FinancialCreditCards.CountAsync(), await db.FinancialCreditCardPurchases.CountAsync(),
                await db.FinancialCreditCardInvoices.CountAsync(), await db.FinancialCreditCardImportedInstallments.CountAsync(), await db.FinancialDebts.CountAsync(),
                await db.FinancialRecurrences.CountAsync(),
            ];
        }
    }
}

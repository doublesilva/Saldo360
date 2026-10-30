using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Classification;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Pluggy;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// R04: central de automação Pluggy + agente de IA. API real sobre PostgreSQL; a Pluggy e o provedor de IA são simulados (nenhuma chamada externa, nenhum custo).
/// Cobre preferências, histórico, categorização manual e após a sincronização, pausa/intervalo do agendador e a revisão da classificação (inclusive compra de cartão).
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PluggyAutomationCenterTests(PostgresFixture postgres) : PluggyApiTestBase(postgres)
{
    private static readonly DateOnly Today = BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static string D(int daysAgo) => Today.AddDays(-daysAgo).ToString("yyyy-MM-dd");

    private sealed record Ctx(string Session, Guid Workspace, HttpClient Client, WebApplicationFactory<Program> Host, Guid Connection);

    private static string Integration(Guid workspace) => $"/api/financial-workspaces/{workspace}/pluggy-integration";
    private static string Automation(Ctx c) => $"{Integration(c.Workspace)}/automation";

    private WebApplicationFactory<Program> CreateHost(bool autoEnabled = false, bool agentEnabled = true) => Factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Pluggy:Enabled", "true");
        builder.UseSetting("Pluggy:ImportEnabled", "true");
        builder.UseSetting("Pluggy:ClientId", "client-sintetico");
        builder.UseSetting("Pluggy:ClientSecret", "segredo-que-nao-pode-vazar");
        builder.UseSetting("Pluggy:SyncPageDelayMilliseconds", "0");
        builder.UseSetting("Pluggy:SyncRateLimitDelaySeconds", "0");
        builder.UseSetting("Pluggy:SyncRateLimitRetries", "1");
        builder.UseSetting("Pluggy:AutoSync:Enabled", autoEnabled ? "true" : "false");
        builder.UseSetting("Pluggy:AutoSync:StartupDelaySeconds", "3600");
        builder.UseSetting("Pluggy:AutoSync:ConnectionDelaySeconds", "0");
        builder.UseSetting("AiAgent:Enabled", agentEnabled ? "true" : "false");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPluggyClient>(Pluggy);
            services.AddSingleton<IFinancialClassificationModel>(Ai);
            services.AddSingleton(new FinancialClassificationOptions { AiEnabled = true, RetryDelayMilliseconds = 0 });
        });
    });

    private static PluggyTransaction BankTx(string id, int daysAgo, decimal amount, string description, string? category = "Groceries") =>
        FakePluggyClient.Transaction(id, BankA, D(daysAgo), amount, "POSTED") with
        {
            OperationType = "CARTAO", Description = description, DescriptionRaw = description.ToUpperInvariant(), Type = amount < 0 ? "DEBIT" : "CREDIT", Category = category,
        };

    private async Task<Ctx> ArrangeAsync(string user, bool autoEnabled = false, bool agentEnabled = true, bool seed = true, bool linkBank = true, string item = ItemA)
    {
        var host = CreateHost(autoEnabled, agentEnabled);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var connection = await LinkAsync(client, session, workspace.Id, item, "Itaú PF");
        Pluggy.TransactionPageSize = 50;
        Pluggy.TransactionsByAccount[BankA] = seed
            ? [BankTx("g1", 9, -10.25m, "Mercado Central"), BankTx("g2", 8, -20m, "Mercado Bairro"), BankTx("unk", 4, -15m, "Loja Zeta", "Mystery Category")]
            : [];
        var ctx = new Ctx(session, workspace.Id, client, host, connection);
        if (linkBank) await LinkAccountAsync(ctx, BankA, "Itaú corrente");
        return ctx;
    }

    private async Task LinkAccountAsync(Ctx c, string account, string name, bool card = false)
    {
        var body = card ? (object)new { connectionId = c.Connection, pluggyAccountId = account, mode = "CreateNew", name, closingDay = 8, dueDay = 15 }
            : new { connectionId = c.Connection, pluggyAccountId = account, mode = "CreateNew", name, financialScope = "PF" };
        Assert.Equal(HttpStatusCode.Created, (await Post(c, $"{Integration(c.Workspace)}/links", body)).StatusCode);
    }

    private Task<HttpResponseMessage> Post(Ctx c, string url, object? body = null) => Send(c.Client, HttpMethod.Post, url, c.Session, body ?? new { });
    private Task<HttpResponseMessage> Put(Ctx c, string url, object body) => Send(c.Client, HttpMethod.Put, url, c.Session, body);
    private Task<HttpResponseMessage> Get(Ctx c, string url) => Send(c.Client, HttpMethod.Get, url, c.Session);

    private async Task<JsonElement> GetJson(Ctx c, string url)
    {
        var response = await Get(c, url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode}");
        return await JsonOf(response);
    }

    private async Task SyncAsync(Ctx c)
    {
        var response = await Post(c, $"{Root(c.Workspace)}/connections/{c.Connection}/sync");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<PluggyAutoSyncSummary> PassAsync(Ctx c)
    {
        using var scope = c.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PluggyAutoSyncRunner>().RunDueAsync(default);
    }

    private async Task MakeDueAsync(Guid connection)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pluggy_sync_states SET next_attempt_at = now() - interval '1 minute', claimed_until = NULL WHERE connection_id = {connection}");
    }

    private async Task<Guid> CreateCategoryAsync(Ctx c, string name, string type = "Expense")
    {
        var response = await Send(c.Client, HttpMethod.Post, $"/api/financial-workspaces/{c.Workspace}/categories", c.Session, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    private async Task<(int Sync, int Categorization)> RunRowsAsync(Guid workspace)
    {
        await using var db = Db();
        return (await db.PluggySyncRuns.CountAsync(r => r.WorkspaceId == workspace), await db.PluggyCategorizationRuns.CountAsync(r => r.WorkspaceId == workspace));
    }

    // ---- Estado e preferências -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOverviewIsReadOnlyShowsDefaultsAndNeverCallsThePluggyOrTheAi()
    {
        var c = await ArrangeAsync("r04-overview");
        Pluggy.Calls.Clear();

        var overview = await GetJson(c, Automation(c));

        Assert.Empty(Pluggy.Calls);
        Assert.Equal(0, Ai.Calls);
        Assert.Equal((0, 0), await RunRowsAsync(c.Workspace)); // ler não executa nada
        var settings = overview.GetProperty("settings");
        Assert.False(settings.GetProperty("syncPaused").GetBoolean());
        Assert.False(settings.GetProperty("categorizeAfterSync").GetBoolean()); // nunca ligada como efeito de implantação
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("categorizeConsentAt").ValueKind);
        Assert.True(settings.GetProperty("syncIntervalIsDefault").GetBoolean());
        Assert.Equal(new[] { 6, 12, 24 }, overview.GetProperty("server").GetProperty("intervalOptions").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.False(overview.GetProperty("server").GetProperty("schedulerEnabled").GetBoolean());
        Assert.Equal("SchedulerOff", overview.GetProperty("sync").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("sync").GetProperty("nextRunAt").ValueKind); // sem agendador real não há "próxima execução"
        Assert.DoesNotContain("segredo", overview.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithoutTheAiAgentTheOverviewSaysSoAndTheAiIsNeverOffered()
    {
        var c = await ArrangeAsync("r04-noai", agentEnabled: false);
        Ai.Available = false;

        var overview = await GetJson(c, Automation(c));

        var ai = overview.GetProperty("server").GetProperty("ai");
        Assert.False(ai.GetProperty("available").GetBoolean());
        Assert.Equal("AiNotConfigured", ai.GetProperty("reason").GetString());
        Assert.Equal("AiUnavailable", overview.GetProperty("categorization").GetProperty("state").GetString());
    }

    [Fact]
    public async Task OnlyValidSettingsAreAcceptedAndTheAiConsentIsExplicitAndRecorded()
    {
        var c = await ArrangeAsync("r04-settings");

        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, Automation(c), new { syncIntervalHours = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, Automation(c), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, Automation(c), new { syncPaused = true, workspaceId = Guid.NewGuid() })).StatusCode); // campo desconhecido
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, Automation(c), new { categorizeAfterSync = true })).StatusCode); // sem consentimento
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, Automation(c), new { categorizeAfterSync = true, confirmExternal = false })).StatusCode);
        await using (var db = Db()) Assert.False(await db.PluggyAutomationSettings.AnyAsync(s => s.WorkspaceId == c.Workspace)); // recusas não gravam nada

        var saved = await JsonOf(await Put(c, Automation(c), new { syncPaused = true, syncIntervalHours = 12, categorizeAfterSync = true, confirmExternal = true }));
        Assert.True(saved.GetProperty("syncPaused").GetBoolean());
        Assert.Equal(12, saved.GetProperty("syncIntervalHours").GetInt32());
        Assert.False(saved.GetProperty("syncIntervalIsDefault").GetBoolean());
        Assert.True(saved.GetProperty("categorizeAfterSync").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, saved.GetProperty("categorizeConsentAt").ValueKind);

        var off = await JsonOf(await Put(c, Automation(c), new { categorizeAfterSync = false }));
        Assert.False(off.GetProperty("categorizeAfterSync").GetBoolean());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("categorizeConsentAt").ValueKind); // desligar apaga o consentimento
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, Automation(c), new { categorizeAfterSync = true })).StatusCode); // religar exige confirmar de novo
        Assert.True((await JsonOf(await Put(c, Automation(c), new { syncPaused = false }))).GetProperty("syncIntervalHours").GetInt32() == 12);
    }

    [Fact]
    public async Task AnotherUserCannotReadOrChangeTheAutomationOfAWorkspaceTheyDoNotBelongTo()
    {
        var owner = await ArrangeAsync("r04-iso-a");
        var strangerSession = await SignInAsync("r04-iso-b");
        var stranger = owner with { Session = strangerSession };

        Assert.Equal(HttpStatusCode.NotFound, (await Get(stranger, Automation(owner))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Put(stranger, Automation(owner), new { syncPaused = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(stranger, $"{Automation(owner)}/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(stranger, $"{Integration(owner.Workspace)}/classification-review")).StatusCode);
        await using var db = Db();
        Assert.False(await db.PluggyAutomationSettings.AnyAsync(s => s.WorkspaceId == owner.Workspace));
    }

    // ---- Pausa e intervalo do agendador --------------------------------------------------------------------------------------------

    [Fact]
    public async Task PausingBlocksScheduledRunsButNeverTheManualSyncAndResumingSchedulesAgain()
    {
        var c = await ArrangeAsync("r04-pause", autoEnabled: true);
        await Put(c, Automation(c), new { syncPaused = true });
        Pluggy.Calls.Clear();

        var paused = await PassAsync(c);

        Assert.Equal(0, paused.Considered);
        Assert.Empty(Pluggy.Calls); // pausado: nada agendado chama a Pluggy
        Assert.Equal("Paused", (await GetJson(c, Automation(c))).GetProperty("sync").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, (await GetJson(c, Automation(c))).GetProperty("sync").GetProperty("nextRunAt").ValueKind);

        await SyncAsync(c); // "Sincronizar agora" continua valendo
        await using (var db = Db())
        {
            var run = await db.PluggySyncRuns.AsNoTracking().SingleAsync(r => r.WorkspaceId == c.Workspace);
            Assert.Equal(PluggySyncTrigger.Manual, run.Trigger);
        }

        await Put(c, Automation(c), new { syncPaused = false });
        await MakeDueAsync(c.Connection);
        var resumed = await PassAsync(c);
        Assert.Equal(1, resumed.Considered); // voltou a ser elegível para o agendador
    }

    [Fact]
    public async Task TheChosenIntervalSchedulesTheNextRunOfTheWorkspaceAfterASuccessfulSync()
    {
        var c = await ArrangeAsync("r04-interval", autoEnabled: true);
        await Put(c, Automation(c), new { syncIntervalHours = 6 });

        await SyncAsync(c);

        await using var db = Db();
        var state = await db.PluggySyncStates.AsNoTracking().SingleAsync(s => s.ConnectionId == c.Connection);
        Assert.InRange((state.NextAttemptAt!.Value - (DateTimeOffset.UtcNow + TimeSpan.FromHours(6))).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(3));
    }

    // ---- Histórico ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHistoryUnitesSyncAndCategorizationRunsWithPerOperationCountsFiltersAndPagination()
    {
        var c = await ArrangeAsync("r04-history");
        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, $"{Integration(c.Workspace)}/categorization/run")).StatusCode);

        var all = await GetJson(c, $"{Automation(c)}/runs");

        Assert.Equal(2, all.GetProperty("total").GetInt32());
        var items = all.GetProperty("items").EnumerateArray().ToList();
        var categorization = items.Single(i => i.GetProperty("kind").GetString() == "Categorization");
        var sync = items.Single(i => i.GetProperty("kind").GetString() == "Sync");
        Assert.Equal(("Manual", "Manual"), (categorization.GetProperty("trigger").GetString(), sync.GetProperty("trigger").GetString()));
        Assert.Equal("Completed", sync.GetProperty("state").GetString());
        Assert.Equal("Itaú PF", sync.GetProperty("scope").GetString());
        Assert.Equal(3, sync.GetProperty("sync").GetProperty("created").GetInt32());
        Assert.Equal(JsonValueKind.Null, sync.GetProperty("categorization").ValueKind); // contagens por operação: nunca somadas entre etapas
        Assert.Equal(JsonValueKind.Null, categorization.GetProperty("sync").ValueKind);
        Assert.Equal("Todo o orçamento", categorization.GetProperty("scope").GetString());
        Assert.True(categorization.GetProperty("durationSeconds").GetInt32() >= 0);
        Assert.Equal("CompletedWithPending", categorization.GetProperty("state").GetString()); // "Loja Zeta" ficou sem categoria segura

        Assert.Equal(1, (await GetJson(c, $"{Automation(c)}/runs?kind=Sync")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await GetJson(c, $"{Automation(c)}/runs?state=CompletedWithPending")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await GetJson(c, $"{Automation(c)}/runs?state=Failed")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await GetJson(c, $"{Automation(c)}/runs?from={D(40)}&to={D(30)}")).GetProperty("total").GetInt32());
        var firstPage = await GetJson(c, $"{Automation(c)}/runs?pageSize=1&page=1");
        Assert.Equal((2, 1), (firstPage.GetProperty("total").GetInt32(), firstPage.GetProperty("items").GetArrayLength()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(c, $"{Automation(c)}/runs?kind=Outra")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(c, $"{Automation(c)}/runs?state=Quebrado")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(c, $"{Automation(c)}/runs?pageSize=1000")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(c, $"{Automation(c)}/runs?from=2026-02-02&to=2026-01-01")).StatusCode);

        var detail = await GetJson(c, $"{Automation(c)}/runs/Sync/{sync.GetProperty("id").GetGuid()}");
        Assert.Equal(sync.GetProperty("id").GetGuid(), detail.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, $"{Automation(c)}/runs/Categorization/{sync.GetProperty("id").GetGuid()}")).StatusCode);
        var raw = all.GetRawText();
        Assert.DoesNotContain("segredo", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ItemA, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mercado", raw, StringComparison.OrdinalIgnoreCase); // histórico nunca guarda descrições
    }

    [Fact]
    public async Task OneWorkspaceNeverSeesTheRunsOfAnother()
    {
        var a = await ArrangeAsync("r04-hist-a");
        await SyncAsync(a);
        var b = await ArrangeAsync("r04-hist-b", seed: false, linkBank: false, item: ItemB);

        Assert.Equal(1, (await GetJson(a, $"{Automation(a)}/runs")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await GetJson(b, $"{Automation(b)}/runs")).GetProperty("total").GetInt32());
        var id = (await GetJson(a, $"{Automation(a)}/runs")).GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await Get(b, $"{Automation(b)}/runs/Sync/{id}")).StatusCode);
    }

    [Fact]
    public async Task AnAbandonedRunningRecordShowsAsInterruptedFailureAndIsRecoveredWithoutBlockingANewRun()
    {
        var c = await ArrangeAsync("r04-stale");
        await SyncAsync(c);
        Guid staleId;
        await using (var db = Db())
        {
            var user = await db.FinancialWorkspaces.AsNoTracking().Where(w => w.Id == c.Workspace).Select(w => w.OwnerUserId).SingleAsync();
            var run = PluggyCategorizationRun.Start(c.Workspace, user, PluggyCategorizationTrigger.Manual, null, null, false, DateTimeOffset.UtcNow.AddHours(-5));
            db.PluggyCategorizationRuns.Add(run);
            await db.SaveChangesAsync();
            staleId = run.Id;
        }

        var shown = await GetJson(c, $"{Automation(c)}/runs/Categorization/{staleId}");
        Assert.Equal(("Failed", true, "Interrupted"), (shown.GetProperty("state").GetString(), shown.GetProperty("interrupted").GetBoolean(), shown.GetProperty("failureCode").GetString()));
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("finishedAt").ValueKind);
        await using (var db = Db()) Assert.Equal(PluggyCategorizationRunState.Running, (await db.PluggyCategorizationRuns.AsNoTracking().SingleAsync(r => r.Id == staleId)).State); // ler não altera

        Assert.Equal(HttpStatusCode.OK, (await Post(c, $"{Integration(c.Workspace)}/categorization/run")).StatusCode); // a abandonada é encerrada e a nova roda
        await using (var db = Db()) Assert.Equal(PluggyCategorizationRunState.Failed, (await db.PluggyCategorizationRuns.AsNoTracking().SingleAsync(r => r.Id == staleId)).State);
    }

    [Fact]
    public async Task ASecondCategorizationWhileOneIsRunningIsRefusedAndNothingIsProcessedTwice()
    {
        var c = await ArrangeAsync("r04-busy");
        await SyncAsync(c);
        await using (var db = Db())
        {
            var user = await db.FinancialWorkspaces.AsNoTracking().Where(w => w.Id == c.Workspace).Select(w => w.OwnerUserId).SingleAsync();
            db.PluggyCategorizationRuns.Add(PluggyCategorizationRun.Start(c.Workspace, user, PluggyCategorizationTrigger.Manual, null, null, false, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var busy = await Post(c, $"{Integration(c.Workspace)}/categorization/run");

        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("Running", (await GetJson(c, Automation(c))).GetProperty("categorization").GetProperty("state").GetString());
        await using var check = Db();
        Assert.Equal(1, await check.PluggyCategorizationRuns.CountAsync(r => r.WorkspaceId == c.Workspace)); // clique repetido não cria outra execução
    }

    // ---- Categorização depois da sincronização ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CategorizationAfterSyncRunsOnlyWithConsentAndAiAvailableAndIsALinkedStepSeparateFromTheSync()
    {
        var c = await ArrangeAsync("r04-after");
        Ai.Available = true;
        Ai.Responder = FakeFinancialClassificationModel.Always("Lazer");
        await CreateCategoryAsync(c, "Lazer");

        await SyncAsync(c); // preferência desligada: só a sincronização
        Assert.Equal((1, 0), await RunRowsAsync(c.Workspace));
        Assert.Equal(0, Ai.Calls);

        await Put(c, Automation(c), new { categorizeAfterSync = true, confirmExternal = true });
        Pluggy.TransactionsByAccount[BankA] = [.. Pluggy.TransactionsByAccount[BankA], BankTx("new", 2, -33m, "Cinema Centro", "Mystery Category")];
        await SyncAsync(c);

        Assert.Equal((2, 1), await RunRowsAsync(c.Workspace));
        var runs = (await GetJson(c, $"{Automation(c)}/runs")).GetProperty("items").EnumerateArray().ToList();
        var after = runs.Single(r => r.GetProperty("kind").GetString() == "Categorization");
        Assert.Equal("AfterSync", after.GetProperty("trigger").GetString());
        var linkedSync = runs.Single(r => r.GetProperty("id").GetGuid() == after.GetProperty("relatedRunId").GetGuid());
        Assert.Equal(("Sync", "Completed"), (linkedSync.GetProperty("kind").GetString(), linkedSync.GetProperty("state").GetString())); // a sincronização segue concluída
        Assert.True(after.GetProperty("categorization").GetProperty("usedAi").GetBoolean());
        Assert.True(Ai.Calls > 0);
        Assert.All(Ai.Requests.SelectMany(r => r.Items), item => Assert.DoesNotContain("Mercado", item.Text)); // só o elegível é enviado
    }

    [Fact]
    public async Task CategorizationAfterSyncIsSkippedWhenTheAiIsUnavailableAndTheSyncCompletesNormally()
    {
        var c = await ArrangeAsync("r04-after-noai");
        await Put(c, Automation(c), new { categorizeAfterSync = true, confirmExternal = true });
        Ai.Available = false;

        await SyncAsync(c);

        Assert.Equal((1, 0), await RunRowsAsync(c.Workspace));
        Assert.Equal(0, Ai.Calls);
        await using var db = Db();
        Assert.Equal(PluggySyncStatus.Completed, (await db.PluggySyncRuns.AsNoTracking().SingleAsync(r => r.WorkspaceId == c.Workspace)).Status);
        Assert.Equal(PluggySyncOutcome.Completed, (await db.PluggySyncStates.AsNoTracking().SingleAsync(s => s.ConnectionId == c.Connection)).LastOutcome);
    }

    [Fact]
    public async Task AnAiFailureAfterTheSyncIsRecordedAsTheCategorizationStepAndNeverAsAConnectionFailure()
    {
        var c = await ArrangeAsync("r04-after-fail");
        Ai.Available = true;
        Ai.Failure = new InvalidOperationException("falha simulada do provedor");
        await Put(c, Automation(c), new { categorizeAfterSync = true, confirmExternal = true });

        await SyncAsync(c);

        await using var db = Db();
        Assert.Equal(PluggySyncOutcome.Completed, (await db.PluggySyncStates.AsNoTracking().SingleAsync(s => s.ConnectionId == c.Connection)).LastOutcome); // a conexão segue saudável
        Assert.Equal(0, (await db.PluggySyncStates.AsNoTracking().SingleAsync(s => s.ConnectionId == c.Connection)).ConsecutiveFailures);
        var run = await db.PluggyCategorizationRuns.AsNoTracking().SingleAsync(r => r.WorkspaceId == c.Workspace);
        Assert.NotEqual(PluggyCategorizationRunState.Running, run.State);
        Assert.True(await db.PluggyImportedTransactions.CountAsync(t => t.WorkspaceId == c.Workspace) == 3); // dados da sincronização intactos
    }

    [Fact]
    public async Task ManualCategorizationIsAvailableWithoutTheAutomaticPreferenceAndNeverChangesAManualChoice()
    {
        var c = await ArrangeAsync("r04-manual-kept");
        await SyncAsync(c);
        var food = await CreateCategoryAsync(c, "Alimentação");
        Assert.Equal(HttpStatusCode.OK, (await Post(c, $"{Integration(c.Workspace)}/categorization/run")).StatusCode); // a avaliação marca o que precisa de revisão
        var review = await GetJson(c, $"{Integration(c.Workspace)}/classification-review?kind=category");
        var item = review.GetProperty("items").EnumerateArray().First(i => i.GetProperty("description").GetString() == "Loja Zeta");
        var changed = await Put(c, $"/api/financial-workspaces/{c.Workspace}/transactions/{item.GetProperty("recordId").GetGuid()}/category", new { categoryId = food });
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await Post(c, $"{Integration(c.Workspace)}/categorization/run", new { force = true })).StatusCode);

        await using var db = Db();
        var kept = await db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == item.GetProperty("recordId").GetGuid());
        Assert.Equal(food, kept.CategoryId); // escolha do usuário nunca é sobrescrita, nem com "forçar"
        Assert.Equal(PluggyCategorySource.User, (await db.PluggyPostings.AsNoTracking().SingleAsync(p => p.FinancialTransactionId == kept.Id)).CategorySource);
    }

    // ---- Revisão da classificação ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheReviewListsImportedExpensesWithoutNatureAndWithoutASafeCategoryAndValidatesItsParameters()
    {
        var c = await ArrangeAsync("r04-review");
        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, $"{Integration(c.Workspace)}/categorization/run")).StatusCode);

        var nature = await GetJson(c, $"{Integration(c.Workspace)}/classification-review?kind=nature&from={D(30)}&to={D(0)}");
        Assert.Equal("nature", nature.GetProperty("kind").GetString());
        Assert.False(nature.GetProperty("wholeBudget").GetBoolean());
        Assert.Equal(3, nature.GetProperty("total").GetInt32());
        Assert.All(nature.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal("Transaction", item.GetProperty("action").GetString());
            Assert.True(item.GetProperty("imported").GetBoolean());
            Assert.True(item.GetProperty("canChangeNature").GetBoolean());
        });
        var category = await GetJson(c, $"{Integration(c.Workspace)}/classification-review?kind=category");
        Assert.True(category.GetProperty("wholeBudget").GetBoolean()); // a lista por categoria é do orçamento inteiro e diz isso
        Assert.Contains("Loja Zeta", category.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("description").GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await Get(c, $"{Integration(c.Workspace)}/classification-review?kind=outra")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(c, $"{Integration(c.Workspace)}/classification-review?pageSize=1000")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(c, $"{Integration(c.Workspace)}/classification-review?from=2026-02-02&to=2026-01-01")).StatusCode);
        Assert.Equal((1, 1), await RunRowsAsync(c.Workspace)); // ler a lista nunca cria execução (só as duas feitas acima)
    }

    [Fact]
    public async Task AnImportedCardPurchaseIsReviewableAndItsManualCategoryAndNatureSurviveResyncAndForcedCategorization()
    {
        var c = await ArrangeAsync("r04-card");
        Pluggy.BillsByAccount[CardA] = [];
        Pluggy.TransactionsByAccount[CardA] =
        [
            FakePluggyClient.Transaction("cc1", CardA, D(5), 80m, "POSTED") with
            {
                Description = "Livraria Online", DescriptionRaw = "LIVRARIA ONLINE", OperationType = "PAGAMENTO", Type = "DEBIT", Category = null,
            },
        ];
        await LinkAccountAsync(c, CardA, "Itaú cartão", card: true);
        await SyncAsync(c);

        var review = await GetJson(c, $"{Integration(c.Workspace)}/classification-review?kind=nature&from={D(60)}&to={Today.AddDays(40):yyyy-MM-dd}");
        var purchase = review.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("description").GetString()!.StartsWith("Livraria", StringComparison.Ordinal));
        Assert.Equal("CardPurchase", purchase.GetProperty("action").GetString());
        Assert.True(purchase.GetProperty("canChangeCategory").GetBoolean());
        Assert.Contains("compra", purchase.GetProperty("scope").GetString(), StringComparison.OrdinalIgnoreCase); // alcance explícito
        var cardId = purchase.GetProperty("cardId").GetGuid();
        var purchaseId = purchase.GetProperty("recordId").GetGuid();
        var books = await CreateCategoryAsync(c, "Livros");
        var baseUrl = $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}/purchases/{purchaseId}";

        var category = await Put(c, $"{baseUrl}/category", new { categoryId = books });
        Assert.True(category.IsSuccessStatusCode, await category.Content.ReadAsStringAsync());
        var nature = await Put(c, $"{baseUrl}/expense-nature", new { expenseNature = "Variable" });
        Assert.True(nature.IsSuccessStatusCode, await nature.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, $"{baseUrl}/category", new { categoryId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, $"{baseUrl}/category", new { categoryId = books, amount = 1 })).StatusCode); // só a categoria muda

        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, $"{Integration(c.Workspace)}/categorization/run", new { force = true })).StatusCode);

        await using var db = Db();
        var saved = await db.FinancialCreditCardPurchases.AsNoTracking().SingleAsync(p => p.Id == purchaseId);
        Assert.Equal((books, ExpenseNature.Variable), (saved.CategoryId, saved.Nature));
        Assert.Equal(80m, saved.TotalAmount); // valor nunca muda
        var afterwards = await GetJson(c, $"{Integration(c.Workspace)}/classification-review?kind=nature&from={D(60)}&to={Today.AddDays(40):yyyy-MM-dd}");
        Assert.DoesNotContain(afterwards.GetProperty("items").EnumerateArray(), i => i.GetProperty("recordId").GetGuid() == purchaseId); // classificada: sai da lista
        Assert.Equal(HttpStatusCode.NotFound, (await Put(new Ctx(await SignInAsync("r04-card-stranger"), c.Workspace, c.Client, c.Host, c.Connection), $"{baseUrl}/category", new { categoryId = books })).StatusCode);
    }
}

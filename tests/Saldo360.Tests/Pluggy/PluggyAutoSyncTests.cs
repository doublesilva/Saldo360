using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// T09.6: sincronização automática das conexões Pluggy. API real sobre PostgreSQL, cliente Pluggy simulado; o agendador é acionado direto pelo
/// <see cref="PluggyAutoSyncRunner"/> (o serviço em segundo plano fica ocioso nos testes) e o vencimento é controlado no estado da conexão.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PluggyAutoSyncTests(PostgresFixture postgres) : PluggyApiTestBase(postgres)
{
    private static readonly DateOnly Today = Saldo360.Domain.Common.BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static string D(int daysAgo) => Today.AddDays(-daysAgo).ToString("yyyy-MM-dd");

    private sealed record Setup(string Session, Guid Workspace, HttpClient Client, WebApplicationFactory<Program> Host, Guid ConnectionA);

    private WebApplicationFactory<Program> CreateHost(bool autoEnabled, Action<IWebHostBuilder>? configure = null) => Factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Pluggy:Enabled", "true");
        builder.UseSetting("Pluggy:ImportEnabled", "true");
        builder.UseSetting("Pluggy:ClientId", "client-sintetico");
        builder.UseSetting("Pluggy:ClientSecret", "segredo-que-nao-pode-vazar");
        builder.UseSetting("Pluggy:SyncPageDelayMilliseconds", "0");
        builder.UseSetting("Pluggy:SyncRateLimitDelaySeconds", "0");
        builder.UseSetting("Pluggy:SyncRateLimitRetries", "1");
        builder.UseSetting("Pluggy:AutoSync:Enabled", autoEnabled ? "true" : "false");
        builder.UseSetting("Pluggy:AutoSync:StartupDelaySeconds", "3600"); // o serviço em segundo plano fica ocioso: os testes acionam o runner
        builder.UseSetting("Pluggy:AutoSync:ConnectionDelaySeconds", "0");
        configure?.Invoke(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPluggyClient>(Pluggy);
            services.AddSingleton<Saldo360.Application.Classification.IFinancialClassificationModel>(Ai);
            services.AddSingleton(new Saldo360.Application.Classification.FinancialClassificationOptions { AiEnabled = true, RetryDelayMilliseconds = 0 });
        });
    });

    private async Task<Setup> ArrangeAsync(string user, bool autoEnabled = true, Action<IWebHostBuilder>? configure = null, bool linkBank = true)
    {
        var host = CreateHost(autoEnabled, configure);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var connection = await LinkAsync(client, session, workspace.Id, ItemA, "Itaú PF");
        var setup = new Setup(session, workspace.Id, client, host, connection);
        if (linkBank) await LinkAccountAsync(setup, connection, BankA, "Itaú corrente");
        return setup;
    }

    private async Task<JsonElement> LinkAccountAsync(Setup s, Guid connection, string account, string name, bool card = false)
    {
        var body = card ? (object)new { connectionId = connection, pluggyAccountId = account, mode = "CreateNew", name, closingDay = 8, dueDay = 15 }
            : new { connectionId = connection, pluggyAccountId = account, mode = "CreateNew", name, financialScope = "PF" };
        var response = await Send(s.Client, HttpMethod.Post, $"/api/financial-workspaces/{s.Workspace}/pluggy-integration/links", s.Session, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonOf(response);
    }

    private static PluggyTransaction BankTx(string id, int daysAgo, decimal amount, string description = "MERCADO") =>
        FakePluggyClient.Transaction(id, BankA, D(daysAgo), amount) with { Description = description, DescriptionRaw = description, OperationType = "PIX", Category = amount > 0 ? "Salary" : "Groceries" };

    private async Task<PluggyAutoSyncSummary> PassAsync(Setup s)
    {
        using var scope = s.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PluggyAutoSyncRunner>().RunDueAsync(default);
    }

    /// <summary>Faz a conexão vencer agora (como se o intervalo tivesse passado) sem mexer no relógio.</summary>
    private async Task MakeDueAsync(Guid connection)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pluggy_sync_states SET next_attempt_at = now() - interval '1 minute', claimed_until = NULL WHERE connection_id = {connection}");
    }

    private async Task<PluggySyncState> StateAsync(Guid connection)
    {
        await using var db = Db();
        return await db.PluggySyncStates.AsNoTracking().SingleAsync(x => x.ConnectionId == connection);
    }

    private async Task<List<PluggySyncRun>> RunsAsync(Guid connection)
    {
        await using var db = Db();
        return await db.PluggySyncRuns.AsNoTracking().Where(r => r.ConnectionId == connection).OrderBy(r => r.StartedAt).ToListAsync();
    }

    private static void Near(DateTimeOffset? actual, TimeSpan expected)
    {
        Assert.NotNull(actual);
        Assert.InRange((actual!.Value - (DateTimeOffset.UtcNow + expected)).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(3));
    }

    private void SeedBank(params PluggyTransaction[] transactions) => Pluggy.TransactionsByAccount[BankA] = [.. transactions];

    // ---- 1: agendamento ligado e desligado ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADisabledSchedulerNeverTouchesPluggy()
    {
        SeedBank(BankTx("b1", 3, -10.5m));
        var off = await ArrangeAsync("t96-off", autoEnabled: false);
        Pluggy.Calls.Clear(); // as chamadas do vínculo inicial não contam
        var disabled = await PassAsync(off);
        Assert.Equal((false, 0), (disabled.Enabled, disabled.Considered));
        Assert.Empty(Pluggy.Calls);
        Assert.Empty(await RunsAsync(off.ConnectionA));
        Assert.Null((await Db().PluggySyncStates.AsNoTracking().FirstOrDefaultAsync(x => x.ConnectionId == off.ConnectionA))?.LastAttemptAt);
    }

    [Fact]
    public async Task AnEnabledSchedulerSyncsTheDueConnectionWithTheDefaultDailyInterval()
    {
        SeedBank(BankTx("b1", 3, -10.5m));
        var on = await ArrangeAsync("t96-on");
        var summary = await PassAsync(on);

        Assert.Equal((true, true, 1, 1), (summary.Enabled, summary.InWindow, summary.Considered, summary.Synced));
        var run = Assert.Single(await RunsAsync(on.ConnectionA));
        Assert.Equal((PluggySyncTrigger.Scheduled, PluggySyncStatus.Completed), (run.Trigger, run.Status));
        var state = await StateAsync(on.ConnectionA);
        Assert.Equal((PluggySyncOutcome.Completed, PluggySyncTrigger.Scheduled, 0), (state.LastOutcome, state.LastTrigger, state.ConsecutiveFailures));
        Near(state.NextAttemptAt, TimeSpan.FromHours(24)); // intervalo padrão
        Assert.NotNull(state.LastSuccessAt);
    }

    [Fact]
    public async Task AnOutsideOfWindowPassSyncsNothing()
    {
        var hour = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(Saldo360.Domain.Common.BusinessCalendar.TimeZoneId)).Hour;
        var s = await ArrangeAsync("t96-window", configure: b => { b.UseSetting("Pluggy:AutoSync:WindowStartHour", ((hour + 2) % 24).ToString()); b.UseSetting("Pluggy:AutoSync:WindowEndHour", ((hour + 3) % 24).ToString()); });

        var summary = await PassAsync(s);

        Assert.Equal((true, false, 0), (summary.Enabled, summary.InWindow, summary.Synced));
        Assert.Empty(await RunsAsync(s.ConnectionA));
        // E a sincronização manual nunca depende da janela.
        Assert.Equal(HttpStatusCode.OK, (await Send(s.Client, HttpMethod.Post, $"{Root(s.Workspace)}/connections/{s.ConnectionA}/sync", s.Session)).StatusCode);
    }

    // ---- 2, 3: seleção de elegíveis e execução periódica -------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyConnectionsWithAnIntegratedAccountAreEligibleAndThePerPassLimitIsRespected()
    {
        var s = await ArrangeAsync("t96-eligible", configure: b => b.UseSetting("Pluggy:AutoSync:MaxConnectionsPerRun", "1"));
        var other = await LinkAsync(s.Client, s.Session, s.Workspace, ItemB, "Nubank"); // conexão sem conta integrada
        var third = await LinkAccountAsync(s, other, BankB, "Nubank conta");

        var first = await PassAsync(s);
        var second = await PassAsync(s);
        var third_ = await PassAsync(s);

        Assert.Equal((1, 1), (first.Considered, first.Synced));
        Assert.Equal((1, 1), (second.Considered, second.Synced)); // o limite por passada deixou a outra conexão para a seguinte
        Assert.Equal(0, third_.Considered); // ninguém mais vencido: o intervalo de 24 h ainda não passou
        Assert.Equal(2, await Db().PluggySyncRuns.CountAsync());
        _ = third;
    }

    [Fact]
    public async Task AConnectionWithoutAnyIntegratedAccountIsNeverSynced()
    {
        var s = await ArrangeAsync("t96-no-links", linkBank: false);

        var summary = await PassAsync(s);

        Assert.Equal(0, summary.Considered);
        Assert.DoesNotContain(Pluggy.Calls, c => c.StartsWith("transactions:", StringComparison.Ordinal));
        Assert.Empty(await RunsAsync(s.ConnectionA));
    }

    [Fact]
    public async Task ThePeriodicPassSyncsAgainOnlyWhenTheIntervalPassedAndTheSourceHasNewData()
    {
        SeedBank(BankTx("b1", 3, -10.5m));
        var s = await ArrangeAsync("t96-periodic");
        await PassAsync(s);
        Pluggy.Calls.Clear();

        // Ainda dentro do intervalo: nada é consultado.
        Assert.Equal(0, (await PassAsync(s)).Considered);
        Assert.Empty(Pluggy.Calls);

        // Intervalo vencido, mas o Item não trouxe nada novo: só uma consulta do Item, sem execução nem gravação.
        await MakeDueAsync(s.ConnectionA);
        var nothing = await PassAsync(s);
        Assert.Equal((1, 0, 1), (nothing.Considered, nothing.Synced, nothing.NothingNew));
        Assert.All(Pluggy.Calls, c => Assert.StartsWith("item:", c));
        Assert.Single(await RunsAsync(s.ConnectionA));
        var waiting = await StateAsync(s.ConnectionA);
        Assert.Equal(PluggySyncOutcome.SourceNotUpdated, waiting.LastOutcome);
        Near(waiting.NextAttemptAt, TimeSpan.FromMinutes(120));
        Assert.NotNull(waiting.LastSuccessAt); // a última sincronização completa continua sendo a de antes

        // A instituição atualizou o Item: agora vale sincronizar.
        Pluggy.Items[ItemA] = Pluggy.Items[ItemA] with { LastUpdatedAt = Pluggy.Items[ItemA].LastUpdatedAt!.Value.AddHours(6) };
        SeedBank(BankTx("b1", 3, -10.5m), BankTx("b2", 1, -20m));
        await MakeDueAsync(s.ConnectionA);
        Assert.Equal(1, (await PassAsync(s)).Synced);
        Assert.Equal(2, (await RunsAsync(s.ConnectionA)).Count);
    }

    // ---- 4: concorrência manual × automática ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ManualAndAutomaticNeverProcessTheSameConnectionAtTheSameTime()
    {
        SeedBank(BankTx("b1", 3, -10.5m));
        var s = await ArrangeAsync("t96-concurrent");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Pluggy.BeforeTransactions = async _ => { entered.TrySetResult(); await release.Task; };

        // A manual está no meio da coleta; a passada automática encontra a conexão ocupada e não cria outra execução.
        var manual = Send(s.Client, HttpMethod.Post, $"{Root(s.Workspace)}/connections/{s.ConnectionA}/sync", s.Session);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var automatic = await PassAsync(s);
        Assert.Equal((1, 1, 0), (automatic.Considered, automatic.Busy, automatic.Synced));
        Assert.Single(await RunsAsync(s.ConnectionA));
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await manual).StatusCode);
        Assert.Single(await RunsAsync(s.ConnectionA));

        // O inverso: a automática em andamento faz o botão receber 409, sem execução paralela.
        Pluggy.BeforeTransactions = null;
        await MakeDueAsync(s.ConnectionA);
        Pluggy.Items[ItemA] = Pluggy.Items[ItemA] with { LastUpdatedAt = Pluggy.Items[ItemA].LastUpdatedAt!.Value.AddHours(6) };
        var enteredAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Pluggy.BeforeTransactions = async _ => { enteredAgain.TrySetResult(); await releaseAgain.Task; };
        var running = PassAsync(s);
        await enteredAgain.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(s.Client, HttpMethod.Post, $"{Root(s.Workspace)}/connections/{s.ConnectionA}/sync", s.Session)).StatusCode);
        releaseAgain.SetResult();
        Assert.Equal(1, (await running).Synced);
        Assert.Equal(2, (await RunsAsync(s.ConnectionA)).Count);
        await using var db = Db();
        Assert.Equal(0, await db.PluggySyncRuns.CountAsync(r => r.Status == PluggySyncStatus.Running));
    }

    [Fact]
    public async Task TwoPassesAtTheSameTimeClaimTheConnectionOnlyOnce()
    {
        SeedBank(BankTx("b1", 3, -10.5m));
        var s = await ArrangeAsync("t96-claim");

        var results = await Task.WhenAll(PassAsync(s), PassAsync(s));

        Assert.Equal(1, results.Sum(r => r.Synced));
        Assert.Single(await RunsAsync(s.ConnectionA));
    }

    // ---- 5, 10, 11, 12, 13, 15: idempotência e processamento financeiro -----------------------------------------------------------

    [Fact]
    public async Task RepeatedAutomaticRunsAreIdempotentAndUpdateTransactionsBillsAndTheLedgerWithoutOpenAI()
    {
        var closing = new DateOnly(Today.Year, Today.Month, 8) is var c && c <= Today ? c : c.AddMonths(-1);
        Ai.Available = true; // se algo tentasse usar a IA, ela responderia: o teste prova que nada pergunta
        SeedBank(BankTx("b1", 3, -10.5m), BankTx("b2", 2, 99.9m, "SALARIO"));
        Pluggy.AccountsByItem[ItemA] = [FakePluggyClient.Account(BankA, ItemA), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        Pluggy.BillsByAccount[CardA] = [new("bill-1", DateTimeOffset.Parse(closing.AddDays(7).ToString("yyyy-MM-dd") + "T00:00:00Z"), 230m, "BRL", null, null, [], DateTimeOffset.Parse(closing.ToString("yyyy-MM-dd") + "T00:00:00Z"), [])];
        Pluggy.TransactionsByAccount[CardA] =
        [
            FakePluggyClient.Transaction("c1", CardA, closing.AddDays(-12).ToString("yyyy-MM-dd"), 150m, card: new(null, null, null, null, "bill-1", "2222", null, null)) with { Type = "DEBIT", OperationType = "PAGAMENTO", Description = "LOJA", Category = null },
            FakePluggyClient.Transaction("c2", CardA, closing.AddDays(-9).ToString("yyyy-MM-dd"), 80m, card: new(null, null, null, null, "bill-1", "2222", null, null)) with { Type = "DEBIT", OperationType = "PAGAMENTO", Description = "TV", Category = null },
        ];
        var s = await ArrangeAsync("t96-idem");
        await LinkAccountAsync(s, s.ConnectionA, CardA, "Cartão Itaú", card: true);
        // Categoria do usuário equivalente à da Pluggy: a classificação local a usa; nada vai para a IA.
        var category = await Send(s.Client, HttpMethod.Post, $"/api/financial-workspaces/{s.Workspace}/categories", s.Session, new { name = "Alimentação", type = "Expense" });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var foodId = (await JsonOf(category)).GetProperty("id").GetGuid();
        Pluggy.Calls.Clear();

        await PassAsync(s);
        await using var db = Db();
        var first = (await db.FinancialTransactions.CountAsync(), await db.FinancialCreditCardPurchases.CountAsync(), await db.FinancialCreditCardInvoices.CountAsync(),
            await db.PluggyImportedTransactions.CountAsync(), await db.PluggyCardBills.CountAsync());
        Assert.Equal((2, 2, 1), (first.Item1, first.Item2, first.Item3));
        Assert.Equal(230m, (await db.FinancialCreditCardInvoices.AsNoTracking().SingleAsync()).InstitutionTotal); // fatura fechada pelo banco registrada
        // Classificação local: a despesa com categoria Groceries da Pluggy recebe a categoria equivalente do usuário; a sem equivalente fica sem categoria (nunca vai para a IA).
        var expense = await db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Type == Saldo360.Domain.Categories.CategoryType.Expense);
        Assert.Equal(foodId, expense.CategoryId);
        Assert.Equal(0, Ai.Calls);

        // Mesma origem, de novo e de novo: nada duplica (nem lançamentos, nem compras, nem faturas).
        for (var i = 0; i < 2; i++)
        {
            Pluggy.Items[ItemA] = Pluggy.Items[ItemA] with { LastUpdatedAt = Pluggy.Items[ItemA].LastUpdatedAt!.Value.AddHours(6 * (i + 1)) };
            await MakeDueAsync(s.ConnectionA);
            Assert.Equal(1, (await PassAsync(s)).Synced);
        }
        Assert.Equal(first, (await db.FinancialTransactions.CountAsync(), await db.FinancialCreditCardPurchases.CountAsync(), await db.FinancialCreditCardInvoices.CountAsync(),
            await db.PluggyImportedTransactions.CountAsync(), await db.PluggyCardBills.CountAsync()));

        // A instituição traz movimentação nova e uma fatura atualizada: o próximo ciclo as incorpora (transações, fatura e processamento financeiro).
        SeedBank(BankTx("b1", 3, -10.5m), BankTx("b2", 2, 99.9m, "SALARIO"), BankTx("b3", 1, -42m, "FARMACIA"));
        Pluggy.BillsByAccount[CardA] = [new("bill-1", DateTimeOffset.Parse(closing.AddDays(7).ToString("yyyy-MM-dd") + "T00:00:00Z"), 260m, "BRL", null, null, [], DateTimeOffset.Parse(closing.ToString("yyyy-MM-dd") + "T00:00:00Z"), [])];
        Pluggy.Items[ItemA] = Pluggy.Items[ItemA] with { LastUpdatedAt = Pluggy.Items[ItemA].LastUpdatedAt!.Value.AddHours(24) };
        await MakeDueAsync(s.ConnectionA);
        Assert.Equal(1, (await PassAsync(s)).Synced);

        Assert.Equal(3, await db.FinancialTransactions.CountAsync());
        Assert.Equal(260m, (await db.FinancialCreditCardInvoices.AsNoTracking().SingleAsync()).InstitutionTotal);
        Assert.Equal(0, Ai.Calls); // nenhuma chamada automática à OpenAI
        Assert.Equal(4, (await RunsAsync(s.ConnectionA)).Count);
        Assert.All(await RunsAsync(s.ConnectionA), r => Assert.Equal(PluggySyncTrigger.Scheduled, r.Trigger));
    }

    // ---- 6, 7: falha isolada, timeout e limite de requisições ---------------------------------------------------------------------

    [Fact]
    public async Task AFailureInOneConnectionNeverStopsTheOthersAndRetriesBackOffUntilTheNextInterval()
    {
        SeedBank(BankTx("b1", 3, -10.5m));
        var s = await ArrangeAsync("t96-isolated");
        var failing = await LinkAsync(s.Client, s.Session, s.Workspace, ItemB, "Nubank");
        await LinkAccountAsync(s, failing, BankB, "Nubank conta");
        Pluggy.Failures[$"accounts:{ItemB}"] = new PluggyException(PluggyFailureKind.Unavailable, 503);

        var first = await PassAsync(s);

        Assert.Equal((2, 1, 1), (first.Considered, first.Synced, first.Failed));
        var ok = await StateAsync(s.ConnectionA);
        Assert.Equal((PluggySyncOutcome.Completed, 0), (ok.LastOutcome, ok.ConsecutiveFailures));
        await using (var db = Db()) Assert.Equal(1, await db.FinancialTransactions.CountAsync(t => t.Description == "MERCADO")); // a conexão saudável foi processada
        var broken = await StateAsync(failing);
        Assert.Equal((PluggySyncOutcome.Failed, 1), (broken.LastOutcome, broken.ConsecutiveFailures));
        Near(broken.NextAttemptAt, TimeSpan.FromMinutes(30)); // primeira nova tentativa
        var run = Assert.Single(await RunsAsync(failing));
        Assert.Equal(PluggySyncStatus.Failed, run.Status);
        Assert.DoesNotContain("segredo", run.ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase); // só códigos, nunca segredos

        // Segunda e terceira falhas: espera dobra; esgotadas as tentativas, só no próximo intervalo regular.
        await MakeDueAsync(failing);
        await PassAsync(s);
        Near((await StateAsync(failing)).NextAttemptAt, TimeSpan.FromMinutes(60));
        await MakeDueAsync(failing);
        await PassAsync(s);
        var exhausted = await StateAsync(failing);
        Assert.Equal(3, exhausted.ConsecutiveFailures);
        Near(exhausted.NextAttemptAt, TimeSpan.FromHours(24));

        // Recuperada a instituição, a próxima tentativa funciona e zera as falhas.
        Pluggy.Failures.Remove($"accounts:{ItemB}");
        Pluggy.TransactionsByAccount[BankB] = [];
        await MakeDueAsync(failing);
        Assert.Equal(1, (await PassAsync(s)).Synced);
        Assert.Equal((PluggySyncOutcome.Completed, 0), ((await StateAsync(failing)).LastOutcome, (await StateAsync(failing)).ConsecutiveFailures));
    }

    [Theory]
    [InlineData(PluggyFailureKind.Timeout, PluggySyncOutcome.CompletedWithErrors)]
    [InlineData(PluggyFailureKind.RateLimited, PluggySyncOutcome.Failed)]
    [InlineData(PluggyFailureKind.Authentication, PluggySyncOutcome.CompletedWithErrors)]
    public async Task TimeoutRateLimitAndRejectedCredentialsAreFailuresWithRetryNotCrashes(PluggyFailureKind kind, PluggySyncOutcome expected)
    {
        SeedBank(BankTx("b1", 3, -10.5m));
        var s = await ArrangeAsync("t96-transient-" + kind);
        Pluggy.Failures[$"transactions:{BankA}"] = new PluggyException(kind, kind == PluggyFailureKind.RateLimited ? 429 : null);

        var summary = await PassAsync(s);

        // Rate limit interrompe a coleta inteira (falha); timeout ou credencial recusada numa conta deixam as outras contas passarem (concluída com erros). As duas coisas agendam nova tentativa.
        Assert.Equal(1, summary.Synced + summary.Failed);
        var state = await StateAsync(s.ConnectionA);
        Assert.Equal((expected, 1), (state.LastOutcome, state.ConsecutiveFailures));
        Assert.Contains(kind.ToString(), state.LastError ?? "");
        Near(state.NextAttemptAt, TimeSpan.FromMinutes(30));
        await using var db = Db();
        Assert.Equal(0, await db.FinancialTransactions.CountAsync()); // nada foi lançado a partir de uma coleta que falhou
    }

    // ---- 8: consentimento expirado ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExpiredConsentIsDetectedRecheckedWithoutNoiseAndResumesOnceRenewed()
    {
        SeedBank(BankTx("b1", 3, -10.5m));
        var s = await ArrangeAsync("t96-consent");
        Pluggy.Items[ItemA] = FakePluggyClient.Item(ItemA, consentExpiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var first = await PassAsync(s);

        Assert.Equal(1, first.ConsentExpired);
        var state = await StateAsync(s.ConnectionA);
        Assert.Equal((PluggySyncOutcome.ConsentExpired, 0), (state.LastOutcome, state.ConsecutiveFailures)); // precisa do usuário: não é falha a repetir
        Near(state.NextAttemptAt, TimeSpan.FromHours(6));
        Assert.DoesNotContain(Pluggy.Calls, c => c.StartsWith("transactions:", StringComparison.Ordinal)); // não consulta dados sem autorização
        await using (var db = Db()) Assert.Equal(PluggyAccountLinkStatus.ConsentExpired, (await db.PluggyAccountLinks.AsNoTracking().FirstAsync()).Status);

        // Reverificações enquanto continua vencido não criam execuções.
        await MakeDueAsync(s.ConnectionA);
        Assert.Equal(1, (await PassAsync(s)).ConsentExpired);
        Assert.Single(await RunsAsync(s.ConnectionA));

        // O usuário renova o consentimento: a conexão volta a sincronizar sozinha.
        Pluggy.Items[ItemA] = FakePluggyClient.Item(ItemA, consentExpiresAt: DateTimeOffset.UtcNow.AddDays(3));
        await MakeDueAsync(s.ConnectionA);
        Assert.Equal(1, (await PassAsync(s)).Synced);
        var renewed = await StateAsync(s.ConnectionA);
        Assert.Equal(PluggySyncOutcome.Completed, renewed.LastOutcome);
        await using (var db = Db()) Assert.Equal(PluggyAccountLinkStatus.Active, (await db.PluggyAccountLinks.AsNoTracking().FirstAsync()).Status);

        // E o monitoramento avisa que o consentimento está perto de vencer (3 dias, aviso em 7).
        var monitor = await JsonOf(await Send(s.Client, HttpMethod.Get, $"/api/financial-workspaces/{s.Workspace}/pluggy-integration/sync-status", s.Session));
        Assert.True(monitor.GetProperty("connections")[0].GetProperty("consentExpiringSoon").GetBoolean());
    }

    // ---- 9: execução abandonada ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAbandonedRunIsRecoveredAndTheConnectionSyncsAgain()
    {
        SeedBank(BankTx("b1", 3, -10.5m));
        var s = await ArrangeAsync("t96-abandoned");
        Guid abandonedId;
        using (var scope = s.Host.Services.CreateScope())
        {
            var imports = scope.ServiceProvider.GetRequiredService<IPluggyImportRepository>();
            var userId = (await Db().Users.FirstAsync()).Id;
            abandonedId = (await imports.TryStartRunAsync(s.Workspace, s.ConnectionA, userId, DateTimeOffset.UtcNow.AddHours(-3), TimeSpan.FromMinutes(30), PluggySyncTrigger.Scheduled, default))!.Id;
        }
        await using (var check = Db()) Assert.Equal(PluggySyncStatus.Running, (await check.PluggySyncRuns.AsNoTracking().SingleAsync(r => r.Id == abandonedId)).Status);

        var summary = await PassAsync(s);

        Assert.Equal(1, summary.Synced);
        var runs = await RunsAsync(s.ConnectionA);
        Assert.Equal(2, runs.Count);
        var abandoned = runs.Single(r => r.Id == abandonedId);
        Assert.Equal((PluggySyncStatus.Failed, "Abandoned"), (abandoned.Status, abandoned.ErrorSummary));
        Assert.DoesNotContain(runs, r => r.Status == PluggySyncStatus.Running);
        Assert.Equal(PluggySyncOutcome.Completed, (await StateAsync(s.ConnectionA)).LastOutcome);
    }

    // ---- 14: histórico e monitoramento ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMonitoringViewShowsAttemptsNextRunCountsDurationAndTheRecentHistory()
    {
        SeedBank(BankTx("b1", 3, -10.5m), BankTx("b2", 2, 99.9m, "SALARIO"));
        var s = await ArrangeAsync("t96-monitor");
        await PassAsync(s);
        Assert.Equal(HttpStatusCode.OK, (await Send(s.Client, HttpMethod.Post, $"{Root(s.Workspace)}/connections/{s.ConnectionA}/sync", s.Session)).StatusCode);

        var monitor = await JsonOf(await Send(s.Client, HttpMethod.Get, $"/api/financial-workspaces/{s.Workspace}/pluggy-integration/sync-status", s.Session));

        Assert.True(monitor.GetProperty("auto").GetProperty("enabled").GetBoolean());
        Assert.Equal(24, monitor.GetProperty("auto").GetProperty("intervalHours").GetInt32());
        var connection = Assert.Single(monitor.GetProperty("connections").EnumerateArray());
        Assert.Equal(("Completed", "Manual", 1), (connection.GetProperty("status").GetString(), connection.GetProperty("lastTrigger").GetString(), connection.GetProperty("integratedAccounts").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, connection.GetProperty("lastAttemptAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, connection.GetProperty("lastSuccessAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, connection.GetProperty("nextAttemptAt").ValueKind);
        Assert.True(connection.GetProperty("lastDurationSeconds").GetInt32() >= 0);
        var runs = connection.GetProperty("recentRuns").EnumerateArray().ToList();
        Assert.Equal(new[] { "Manual", "Scheduled" }, runs.Select(r => r.GetProperty("trigger").GetString()!).ToArray()); // mais recente primeiro
        Assert.DoesNotContain("segredo", monitor.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ItemA, monitor.GetRawText(), StringComparison.OrdinalIgnoreCase); // nem o identificador do Item
    }

    [Fact]
    public async Task WithAutomaticSyncOffTheMonitoringShowsNoNextRun()
    {
        var s = await ArrangeAsync("t96-monitor-off", autoEnabled: false);

        var monitor = await JsonOf(await Send(s.Client, HttpMethod.Get, $"/api/financial-workspaces/{s.Workspace}/pluggy-integration/sync-status", s.Session));

        Assert.False(monitor.GetProperty("auto").GetProperty("enabled").GetBoolean());
        var connection = monitor.GetProperty("connections")[0];
        Assert.Equal(("NeverSynced", JsonValueKind.Null), (connection.GetProperty("status").GetString(), connection.GetProperty("nextAttemptAt").ValueKind));
    }

    // ---- 16: isolamento entre workspaces ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EachConnectionIsSyncedIntoItsOwnWorkspaceAndOthersCannotSeeItsMonitoring()
    {
        SeedBank(BankTx("b1", 3, -10.5m));
        var a = await ArrangeAsync("t96-iso-a");
        var strangerSession = await SignInAsync("t96-iso-b");
        var strangerWorkspace = await CreateWorkspaceAsync(strangerSession);
        var strangerConnection = await LinkAsync(a.Client, strangerSession, strangerWorkspace.Id, ItemB, "Nubank");
        Pluggy.TransactionsByAccount[BankB] = [FakePluggyClient.Transaction("n1", BankB, D(2), -7m)];
        var stranger = new Setup(strangerSession, strangerWorkspace.Id, a.Client, a.Host, strangerConnection);
        await LinkAccountAsync(stranger, strangerConnection, BankB, "Nubank conta");

        var summary = await PassAsync(a);

        Assert.Equal(2, summary.Synced);
        await using var db = Db();
        Assert.Equal(1, await db.PluggyImportedTransactions.CountAsync(t => t.WorkspaceId == a.Workspace));
        Assert.Equal(1, await db.PluggyImportedTransactions.CountAsync(t => t.WorkspaceId == strangerWorkspace.Id));
        Assert.Equal(0, await db.PluggyImportedTransactions.CountAsync(t => t.WorkspaceId == a.Workspace && t.PluggyAccountId == BankB)); // dados de um orçamento nunca vão para o outro
        Assert.Equal(HttpStatusCode.NotFound, (await Send(a.Client, HttpMethod.Get, $"/api/financial-workspaces/{strangerWorkspace.Id}/pluggy-integration/sync-status", a.Session)).StatusCode);
        var own = await JsonOf(await Send(a.Client, HttpMethod.Get, $"/api/financial-workspaces/{a.Workspace}/pluggy-integration/sync-status", a.Session));
        Assert.Single(own.GetProperty("connections").EnumerateArray());
    }
}

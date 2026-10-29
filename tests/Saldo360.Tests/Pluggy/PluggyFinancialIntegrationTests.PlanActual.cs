using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// T10.1: o Dashboard Planejado × Projetado × Realizado sobre contas Pluggy (cliente simulado): cheque especial, revisões, pendentes, referência provisória e
/// sincronização. Nenhum teste depende da Pluggy nem da OpenAI reais.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private Task<JsonElement> PlanActualAsync(Ctx c, string query = "") => JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-dashboard/plan-actual{query}");

    /// <summary>Conta bancária com cheque especial: saldo observado negativo igual à utilização, como o Itaú informa.</summary>
    private async Task<(Ctx C, Guid Account)> ArrangeOverdraftAsync(string user, decimal used, decimal contracted = 2000m, Func<Guid, Task>? history = null)
    {
        var c = await ArrangeAsync(user, seed: false);
        Pluggy.TransactionsByAccount[BankA] = [BankTx("in", PreviousMonthStart.AddDays(3), 1000m, "SALARIO"), BankTx("out", PreviousMonthStart.AddDays(10), -250m)];
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, -used, contracted, used), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var accountId = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        if (history is not null) await history(accountId);
        await SyncAsync(c);
        return (c, accountId);
    }

    /// <summary>Observação antiga de saldo (histórico): substituída pela vigente, como depois de uma nova sincronização.</summary>
    private async Task AddObservationAsync(Ctx c, Guid account, decimal balance, DateOnly asOf, int hour = 20)
    {
        await using var db = Db();
        var observedAt = new DateTimeOffset(asOf.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero);
        var anchor = FinancialAccountBalanceAnchor.Create(c.Workspace, account, balance, observedAt, asOf, BalanceAnchorKind.CurrentBalance, "Pluggy", null, observedAt);
        anchor.Supersede();
        db.Add(anchor);
        await db.SaveChangesAsync();
    }

    private static JsonElement Overdraft(JsonElement dashboard) => dashboard.GetProperty("overdraft");

    // ---- 18–21: cheque especial -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task OverdraftRecoveryIsTheReductionOfTheUsedLimitSinceTheStartOfTheMonth()
    {
        var (c, account) = await ArrangeOverdraftAsync("t101-recovery", used: 800m);
        await AddObservationAsync(c, account, -1500m, MonthStart.AddDays(-1));

        var overdraft = Overdraft(await PlanActualAsync(c, $"?scope=account&accountId={account}"));

        Assert.True(overdraft.GetProperty("eligible").GetBoolean());
        var item = overdraft.GetProperty("accounts")[0];
        Assert.Equal(("1500.00", "800.00", "700.00", "Recovered", false), (Text(item, "usedAtStart"), Text(item, "usedLatest"), Text(item, "effectiveRecovery"), Text(item, "direction"), item.GetProperty("fullyRecovered").GetBoolean()));
        Assert.Equal(("700.00", "2000.00"), (Text(overdraft, "effectiveRecovery"), Text(overdraft, "contractedLimit")));
        // O limite contratado é crédito do banco: nunca entra como receita nem em saldo.
        Assert.Equal("0.00", Text(await PlanActualAsync(c, $"?scope=account&accountId={account}"), "income", "realized"));
    }

    [Fact]
    public async Task ARiseInTheUsedLimitIsShownAsAnIncreaseInDebt()
    {
        var (c, account) = await ArrangeOverdraftAsync("t101-increase", used: 800m);
        await AddObservationAsync(c, account, -500m, MonthStart.AddDays(-1));

        var item = Overdraft(await PlanActualAsync(c, $"?scope=account&accountId={account}")).GetProperty("accounts")[0];

        Assert.Equal(("-300.00", "Increased"), (Text(item, "effectiveRecovery"), Text(item, "direction")));
    }

    [Fact]
    public async Task AFullyRecoveredLimitIsFlaggedWithoutAnyInventedAdditionalRecovery()
    {
        var (c, account) = await ArrangeOverdraftAsync("t101-recovered", used: 0m);
        await AddObservationAsync(c, account, -400m, MonthStart.AddDays(-1));

        var item = Overdraft(await PlanActualAsync(c, $"?scope=account&accountId={account}")).GetProperty("accounts")[0];

        Assert.True(item.GetProperty("fullyRecovered").GetBoolean());
        Assert.Equal(("400.00", "0.00"), (Text(item, "effectiveRecovery"), Text(item, "usedLatest")));
        // Sem recuperação adicional fictícia: a projeção não passa de zero (ou fica indisponível), nunca positiva.
        Assert.True(item.GetProperty("projectedRecoveryFromNow").ValueKind == JsonValueKind.Null || Text(item, "projectedRecoveryFromNow") == "0.00" || Text(item, "projectedRecoveryFromNow").StartsWith('-'));
    }

    [Fact]
    public async Task WithoutAComparableStartObservationTheHistoryIsInsufficientAndTheCurrentUseIsPreserved()
    {
        var (c, account) = await ArrangeOverdraftAsync("t101-insufficient", used: 800m);

        var overdraft = Overdraft(await PlanActualAsync(c, $"?scope=account&accountId={account}"));
        var item = overdraft.GetProperty("accounts")[0];

        Assert.Equal(("InsufficientHistory", "800.00"), (Text(item, "state"), Text(item, "usedLatest")));
        Assert.True(IsNullJson(item, "effectiveRecovery") && IsNullJson(item, "direction"));
        Assert.Equal("800.00", Text(overdraft, "usedLatest"));
        Assert.Contains("InsufficientHistory", item.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task AccountsWithoutAContractedOrUsedLimitHaveNoOverdraftIndicator()
    {
        var (c, account) = await ArrangeOverdraftAsync("t101-no-limit", used: 0m, contracted: 0m);

        var overdraft = Overdraft(await PlanActualAsync(c, $"?scope=account&accountId={account}"));

        Assert.False(overdraft.GetProperty("eligible").GetBoolean());
        Assert.Equal(0, overdraft.GetProperty("accounts").GetArrayLength());
    }

    [Fact]
    public async Task ThePastMonthComparesTheStartAndTheEndOfThatMonthAndFutureMonthsHaveNoEffectiveRecovery()
    {
        var (c, account) = await ArrangeOverdraftAsync("t101-periods", used: 800m);
        await AddObservationAsync(c, account, -3000m, PreviousMonthStart.AddDays(-1));
        await AddObservationAsync(c, account, -2500m, MonthStart.AddDays(-1));

        var past = await PlanActualAsync(c, $"?scope=account&accountId={account}&competence={PreviousMonthStart:yyyy-MM}");
        var future = await PlanActualAsync(c, $"?scope=account&accountId={account}&competence={MonthStart.AddMonths(1):yyyy-MM}");

        Assert.Equal(("Past", "500.00"), (Text(past, "periodKind"), Text(Overdraft(past).GetProperty("accounts")[0], "effectiveRecovery")));
        Assert.True(IsNullJson(Overdraft(past).GetProperty("accounts")[0], "projectedRecovery"));
        Assert.True(IsNullJson(Overdraft(future).GetProperty("accounts")[0], "effectiveRecovery"));
        Assert.Contains("FuturePeriod", Overdraft(future).GetProperty("accounts")[0].GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    // ---- 25–26: revisão, referência provisória e cobertura ----------------------------------------------------------------------

    private async Task<(Ctx C, Guid Account)> ArrangeReviewAsync(string user)
    {
        var c = await ArrangeAsync(user, seed: false);
        Pluggy.TransactionsByAccount[BankA] =
        [
            BankTx("salary", MonthStart, 3000m, "SALARIO"),
            Tx("loan", BankA, DaysAgo(MonthStart), 5000m, operation: "OPERACAO_CREDITO", description: "CREDITO CONTRATADO", category: "Loans"),
            Tx("refund", BankA, DaysAgo(MonthStart), 80m, operation: "ESTORNO", description: "ESTORNO COMPRA", category: "Shopping"),
        ];
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, 3000m), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var account = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);
        return (c, account);
    }

    [Fact]
    public async Task LoansAndRefundsStayInReviewAndNeverBecomeIncomeWhileTheConfirmedSalaryIsStillShown()
    {
        var (c, account) = await ArrangeReviewAsync("t101-review");

        var dashboard = await PlanActualAsync(c, $"?scope=account&accountId={account}");

        // Só o salário é receita. O empréstimo e o estorno aguardam decisão: contam como revisão, afetam a confiança e não entram em nenhum total.
        Assert.Equal("3000.00", Text(dashboard, "income", "realized"));
        var coverage = dashboard.GetProperty("coverage");
        Assert.Equal((2, 2, true, false), (coverage.GetProperty("reviewCount").GetInt32(), coverage.GetProperty("reviewInMonth").GetInt32(), coverage.GetProperty("reviewAffectsConfidence").GetBoolean(), coverage.GetProperty("complete").GetBoolean()));
        Assert.Contains("MovementsInReview", dashboard.GetProperty("income").GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("Partial", Text(dashboard, "income", "state"));
        var item = coverage.GetProperty("accounts")[0];
        Assert.Equal((2, "Itaú corrente"), (item.GetProperty("reviewCount").GetInt32(), Text(item, "name")));
    }

    [Fact]
    public async Task AProvisionalBankReferenceIsReportedAsACaveatWhileTheRealizedFiguresRemain()
    {
        var (c, account) = await ArrangeReviewAsync("t101-provisional");

        var item = (await PlanActualAsync(c, $"?scope=account&accountId={account}")).GetProperty("coverage").GetProperty("accounts")[0];

        Assert.Equal("Provisional", Text(item, "referenceReliability"));
        Assert.NotEqual("NoDivergence", Text(item, "reconciliationStatus"));
    }

    [Fact]
    public async Task PendingBankMovementsAreNeverRealizedButCountAsKnownExpenseCommitments()
    {
        var c = await ArrangeAsync("t101-pending", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [BankTx("salary", MonthStart, 1000m, "SALARIO"), Tx("pending", BankA, DaysAgo(MonthStart), -40m, "PENDING", "PIX", "PADARIA")];
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, 1000m), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var account = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);

        var cash = (await PlanActualAsync(c, $"?scope=account&accountId={account}")).GetProperty("expenses").GetProperty("cashFlow");

        Assert.Equal(("0.00", "40.00", "40.00"), (Text(cash, "realized"), Text(cash, "pending"), Text(cash, "projected")));
        Assert.Equal("40.00", cash.GetProperty("components").EnumerateArray().Single(x => x.GetProperty("code").GetString() == "PendingBank").GetProperty("amount").GetString());
    }

    // ---- 27–29: sincronização ----------------------------------------------------------------------------------------------------

    private async Task<Ctx> ArrangeAutoAsync(string user, bool autoEnabled)
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var client = LabClient(configure: services =>
            services.AddSingleton(new PluggyAutoSyncOptions { Enabled = autoEnabled, StartupDelaySeconds = 3600 }));
        var connection = await LinkAsync(client, session, workspace.Id, ItemA, "Itaú PF");
        Pluggy.TransactionPageSize = 3;
        Pluggy.TransactionsByAccount[BankA] = [BankTx("salary", MonthStart, 1000m, "SALARIO")];
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, 1000m), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        return new(session, workspace.Id, client, connection);
    }

    [Fact]
    public async Task WithTheAutomaticSynchronizationOffTheDashboardSaysSoAndNeverPromisesANextAttempt()
    {
        var c = await ArrangeAutoAsync("t101-sync-off", autoEnabled: false);
        await LinkBankAsync(c);
        await SyncAsync(c);

        var sync = (await PlanActualAsync(c)).GetProperty("sync");

        Assert.False(sync.GetProperty("autoEnabled").GetBoolean());
        Assert.True(IsNullJson(sync, "nextAttemptAt"));
        Assert.Equal(("Completed", false), (Text(sync, "state"), sync.GetProperty("stale").GetBoolean()));
        Assert.NotNull(Text(sync, "latestSuccessAt"));
        Assert.Equal(1, sync.GetProperty("connections").GetArrayLength());
    }

    [Fact]
    public async Task WithTheAutomaticSynchronizationOnTheNextAttemptIsShown()
    {
        var c = await ArrangeAutoAsync("t101-sync-on", autoEnabled: true);
        await LinkBankAsync(c);
        await SyncAsync(c);

        var sync = (await PlanActualAsync(c)).GetProperty("sync");

        Assert.True(sync.GetProperty("autoEnabled").GetBoolean());
        Assert.False(IsNullJson(sync, "nextAttemptAt"));
        Assert.Equal(24, sync.GetProperty("autoIntervalHours").GetInt32());
    }

    [Fact]
    public async Task AllAccountsNeverReduceDifferentSynchronizationMomentsToASingleDate()
    {
        var c = await ArrangeAutoAsync("t101-sync-dates", autoEnabled: true);
        var pf = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        var second = await LinkAsync(c.Client, c.Session, c.Workspace, ItemB, "Nubank PJ");
        Pluggy.AccountsByItem[ItemB] = [BankAccount(BankB, ItemB, 10m)];
        Pluggy.TransactionsByAccount[BankB] = [Tx("n1", BankB, DaysAgo(MonthStart), 10m, description: "PIX", category: "Salary")];
        var link = await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = second, pluggyAccountId = BankB, mode = "CreateNew", name = "Nubank PJ", financialScope = "PJ" });
        Assert.Equal(HttpStatusCode.Created, link.StatusCode);
        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Root(c.Workspace)}/connections/{second}/sync")).StatusCode);
        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pluggy_sync_states SET last_success_at = now() - interval '40 hours' WHERE connection_id = {second}");

        var all = (await PlanActualAsync(c, "?scope=all")).GetProperty("sync");
        var primary = (await PlanActualAsync(c, $"?scope=account&accountId={pf}")).GetProperty("sync");

        Assert.Equal(2, all.GetProperty("connections").GetArrayLength());
        Assert.False(all.GetProperty("sameMoment").GetBoolean());
        Assert.True(all.GetProperty("stale").GetBoolean());
        Assert.NotEqual(Text(all, "oldestSuccessAt"), Text(all, "latestSuccessAt"));
        // A conta PF só depende da conexão dela: a defasagem da outra não contamina o escopo.
        Assert.Equal((1, false), (primary.GetProperty("connections").GetArrayLength(), primary.GetProperty("stale").GetBoolean()));
    }

    private static bool IsNullJson(JsonElement element, string property) => element.GetProperty(property).ValueKind == JsonValueKind.Null;
}

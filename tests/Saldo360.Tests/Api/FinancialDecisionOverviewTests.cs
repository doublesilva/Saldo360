using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// T11.1: bloco "decision" da Visão Geral pela API real (Postgres). Os valores usam o dia real só como âncora: vencidos = ontem, hoje e fim do mês, todos na competência corrente
/// ou antes dela, então nada depende do dia do mês. A consulta é somente leitura.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialDecisionOverviewTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static DateOnly EndOfMonth => ThisMonth.AddMonths(1).AddDays(-1);
    private static string Month(DateOnly d) => d.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _income, _expense;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task ArrangeAsync(string user)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        _income = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Receitas", type = "Income" }))).Id;
        _expense = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Despesas", type = "Expense" }))).Id;
    }

    private async Task<Guid> AccountAsync(string name, string opening, string limit, string scope = "PF")
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
            new { name, type = "Checking", financialScope = scope, openingBalance = opening, openingBalanceDate = ThisMonth.AddMonths(-1), overdraftLimit = limit });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task PlanAsync(Guid account, string type, string amount, DateOnly due) =>
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/planned-transactions", _session,
            new { accountId = account, categoryId = type == "Income" ? _income : _expense, type, description = "Previsto", amount, dueDate = due })).StatusCode);

    private async Task<JsonElement> DecisionAsync(string scope, Guid? account, DateOnly month)
    {
        var url = $"{Root}/financial-dashboard/overview?competence={Month(month)}&scope={scope}" + (account is null ? "" : $"&accountId={account}");
        var response = await SendAsync(HttpMethod.Get, url, _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("decision").Clone();
    }

    private static string Text(JsonElement e, params string[] path) { foreach (var p in path) e = e.GetProperty(p); return e.GetString()!; }

    [Fact]
    public async Task SingleAccountReportsCommitmentsCoverageAndASafeMarginWithoutTreatingTheLimitAsOwnFunds()
    {
        await ArrangeAsync("dec-conta");
        var account = await AccountAsync("Itaú PF", "1000.00", "5000.00");
        await PlanAsync(account, "Expense", "100.00", Today.AddDays(-1));
        await PlanAsync(account, "Expense", "200.00", Today);
        await PlanAsync(account, "Expense", "300.00", EndOfMonth);

        var decision = await DecisionAsync("account", account, ThisMonth);

        Assert.Equal("Current", Text(decision, "context", "period"));
        Assert.Equal("Itaú PF", Text(decision, "context", "accountName")); Assert.Equal("PF", Text(decision, "context", "accountScope"));
        Assert.Equal("600.00", Text(decision, "commitments", "total")); Assert.Equal("100.00", Text(decision, "commitments", "overdue")); Assert.Equal("500.00", Text(decision, "commitments", "inCompetence"));
        Assert.Equal("Covered", Text(decision, "coverage", "state")); Assert.Equal("400.00", Text(decision, "coverage", "minimumBalance"));
        Assert.Equal("400.00", Text(decision, "safeMargin", "amount")); // 5.400 (saldo + limite) NÃO é margem
        Assert.Equal("CashSpend", Text(decision, "safeMargin", "scope"));
        Assert.Equal("NotApplicable", Text(decision, "recovery", "state"));
    }

    [Fact]
    public async Task InsufficientAccountShowsTheFirstDateAndDeficitAndConsolidatedKeepsEachAccountSeparate()
    {
        await ArrangeAsync("dec-consolidado");
        var pf = await AccountAsync("Itaú PF", "100.00", "0.00");
        var pj = await AccountAsync("Inter PJ", "50000.00", "0.00", "PJ");
        await PlanAsync(pf, "Expense", "500.00", Today);

        var single = await DecisionAsync("account", pf, ThisMonth);
        Assert.Equal("Insufficient", Text(single, "coverage", "state"));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), Text(single, "coverage", "firstBeyondLimitDate")); Assert.Equal("400.00", Text(single, "coverage", "deficit"));

        var all = await DecisionAsync("all", null, ThisMonth);
        var accounts = all.GetProperty("coverage").GetProperty("accounts").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        Assert.Equal("Insufficient", accounts["Itaú PF"].GetProperty("state").GetString());
        Assert.Equal("Covered", accounts["Inter PJ"].GetProperty("state").GetString()); // o saldo reservado da PJ não compensa a PF
        Assert.Equal("Insufficient", Text(all, "coverage", "state"));
        Assert.Equal("Unavailable", Text(all, "safeMargin", "state"));
        Assert.Contains("ConsolidatedScope", all.GetProperty("safeMargin").GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task FutureAndClosedCompetencesAreLabelledAndDoNotOfferATodayMargin()
    {
        await ArrangeAsync("dec-periodos");
        var account = await AccountAsync("Itaú PF", "1000.00", "0.00");
        await PlanAsync(account, "Expense", "100.00", ThisMonth.AddMonths(1).AddDays(4));

        var future = await DecisionAsync("account", account, ThisMonth.AddMonths(1));
        Assert.Equal("Future", Text(future, "context", "period")); Assert.Equal("100.00", Text(future, "commitments", "inCompetence"));
        Assert.Contains("CompetenceNotCurrent", future.GetProperty("safeMargin").GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));

        var closed = await DecisionAsync("account", account, ThisMonth.AddMonths(-1));
        Assert.Equal("Closed", Text(closed, "context", "period")); Assert.Equal("Unavailable", Text(closed, "coverage", "state"));
        Assert.Equal("ClosedCompetence", Text(closed, "coverage", "unavailableReason"));
    }

    [Fact]
    public async Task AccountWithoutAReferenceBalanceIsUnavailableNotZero()
    {
        await ArrangeAsync("dec-sem-saldo");
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session, new { name = "Sem saldo", type = "Checking", financialScope = "PF" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = (await ReadAsync<IdDto>(response)).Id;

        var decision = await DecisionAsync("account", account, ThisMonth);
        Assert.Equal("Unavailable", Text(decision, "coverage", "state")); Assert.Equal("Unavailable", Text(decision, "safeMargin", "state"));
        Assert.Equal(JsonValueKind.Null, decision.GetProperty("safeMargin").GetProperty("amount").ValueKind);
    }

    [Fact]
    public async Task ReadingTheDecisionCreatesNoTransactionsTransfersOrPlans()
    {
        await ArrangeAsync("dec-leitura");
        var account = await AccountAsync("Itaú PF", "1000.00", "500.00");
        await PlanAsync(account, "Expense", "200.00", Today);
        async Task<(int Transactions, int Transfers, int Planned, int Snapshots)> CountAsync()
        {
            await using var db = Db();
            return (await db.FinancialTransactions.CountAsync(), await db.FinancialTransfers.CountAsync(), await db.FinancialPlannedTransactions.CountAsync(), await db.OverdraftRecoverySnapshots.CountAsync());
        }
        var before = await CountAsync();
        await DecisionAsync("account", account, ThisMonth); await DecisionAsync("all", null, ThisMonth); await DecisionAsync("primary", null, ThisMonth);
        Assert.Equal(before, await CountAsync());
    }
}

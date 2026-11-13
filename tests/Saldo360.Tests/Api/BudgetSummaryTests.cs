using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;
using Xunit.Abstractions;

namespace Saldo360.Tests.Api;

/// <summary>
/// Resumo do orçamento PF (<c>financial-dashboard/budget-summary</c>): o MESMO total de <c>pfBudgetControl.total</c> da Visão Geral, sem montar os demais blocos do dashboard.
/// Inclui a medição do caminho antigo (overview completo) contra o novo, com o mesmo conjunto de dados.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class BudgetSummaryTests(PostgresFixture postgres, ITestOutputHelper output) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month => ThisMonth.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private readonly List<Guid> _accounts = [];
    private Guid _expense;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task ArrangeAsync(string user, int pfAccounts = 1)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        _expense = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Despesas", type = "Expense" }))).Id;
        for (var i = 0; i < pfAccounts; i++)
            _accounts.Add((await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
                new { name = $"PF {i + 1}", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = ThisMonth.AddMonths(-1), overdraftLimit = "1000.00" }))).Id);
    }

    private async Task ExpenseAsync(Guid account, string amount, string description = "Efetivo") =>
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transactions", _session,
            new { accountId = account, categoryId = _expense, type = "Expense", description, amount, transactionDate = Today })).StatusCode);

    private async Task<Guid> CardAsync(string name, Guid? payer)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards", _session, new { name, issuer = "Banco", creditLimit = "10000.00", closingDay = 27, dueDay = 28, defaultPaymentAccountId = payer });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task PurchaseAsync(Guid card, string amount)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards/{card}/purchases", _session, new
        {
            description = "Compra", purchaseDate = ThisMonth, totalAmount = amount, installmentCount = 1, categoryId = _expense, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth, expenseNature = (string?)null,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task ConfigureTotalAsync(decimal total)
    {
        await using var db = Db();
        db.FinancialMonthlyAggregateTargets.Add(FinancialMonthlyAggregateTarget.Create(_workspace.Id, ThisMonth, MonthlyAggregateTargetType.TotalExpenses, total, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<(JsonElement Body, long Bytes)> GetAsync(string path)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/{path}", _session);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return (JsonDocument.Parse(text).RootElement.Clone(), text.Length);
    }

    private async Task<JsonElement> SummaryAsync() => (await GetAsync($"budget-summary?competence={Month}")).Body;
    private async Task<JsonElement> PfOfOverviewAsync() => (await GetAsync($"overview?competence={Month}&scope=all")).Body.GetProperty("pfBudgetControl");

    /// <summary>O resumo e o pfBudgetControl da Visão Geral entregam exatamente o mesmo total, estado, notas e origem da meta.</summary>
    private async Task AssertSameAsOverviewAsync()
    {
        var summary = await SummaryAsync(); var pf = await PfOfOverviewAsync();
        Assert.Equal(pf.GetProperty("total").GetRawText(), summary.GetProperty("total").GetRawText());
        Assert.Equal(pf.GetProperty("state").GetString(), summary.GetProperty("state").GetString());
        Assert.Equal(pf.GetProperty("competence").GetString(), summary.GetProperty("competence").GetString());
        Assert.Equal(pf.GetProperty("notes").GetRawText(), summary.GetProperty("notes").GetRawText());
        Assert.Equal(pf.TryGetProperty("totalTargetOrigin", out var origin) ? origin.GetRawText() : "null", summary.TryGetProperty("totalTargetOrigin", out var o2) ? o2.GetRawText() : "null");
    }

    [Fact]
    public async Task SummaryMatchesTheOverviewWhenTheTargetIsDefinedAndExceeded()
    {
        await ArrangeAsync("bs-meta", 2);
        await ExpenseAsync(_accounts[0], "1000.00"); await ExpenseAsync(_accounts[1], "700.00");
        await PurchaseAsync(await CardAsync("Nubank", _accounts[0]), "5200.00");
        await ConfigureTotalAsync(6000m); // previsto 6900: excede

        await AssertSameAsOverviewAsync();
        var total = (await SummaryAsync()).GetProperty("total");
        Assert.Equal("6000.00", total.GetProperty("target").GetString());
        Assert.Equal("Exceeded", total.GetProperty("status").GetString());
        Assert.Equal("900.00", total.GetProperty("excess").GetString());
    }

    [Fact]
    public async Task SummaryKeepsAbsentTargetDistinctFromZero()
    {
        await ArrangeAsync("bs-ausente");
        await ExpenseAsync(_accounts[0], "300.00");
        await AssertSameAsOverviewAsync();
        var absent = await SummaryAsync();
        Assert.Equal("TargetNotDefined", absent.GetProperty("state").GetString());
        Assert.False(absent.GetProperty("total").GetProperty("targetDefined").GetBoolean());
        Assert.Equal(JsonValueKind.Null, absent.GetProperty("total").GetProperty("target").ValueKind);
        Assert.Equal("300.00", absent.GetProperty("total").GetProperty("realized").GetString());

        await ConfigureTotalAsync(0m);
        await AssertSameAsOverviewAsync();
        var zero = (await SummaryAsync()).GetProperty("total");
        Assert.True(zero.GetProperty("targetDefined").GetBoolean());
        Assert.Equal("0.00", zero.GetProperty("target").GetString());
    }

    [Fact]
    public async Task SummaryMatchesTheOverviewWhenCoverageIsPartial()
    {
        await ArrangeAsync("bs-parcial");
        var card = await CardAsync("Nubank", null); // cartão sem conta pagadora: o total PF sai parcial
        await PurchaseAsync(card, "400.00");
        await ConfigureTotalAsync(5000m);
        await AssertSameAsOverviewAsync();
        Assert.Equal("Partial", (await SummaryAsync()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task SummaryIsReadOnlyValidatesTheCompetenceAndChecksAccess()
    {
        await ArrangeAsync("bs-leitura");
        await ExpenseAsync(_accounts[0], "100.00"); await ConfigureTotalAsync(1000m);
        async Task<(int Transactions, int Targets, int Aggregates)> CountAsync() { await using var db = Db(); return (await db.FinancialTransactions.CountAsync(), await db.FinancialBudgetTargets.CountAsync(), await db.FinancialMonthlyAggregateTargets.CountAsync()); }
        var before = await CountAsync();
        await SummaryAsync(); await SummaryAsync();
        Assert.Equal(before, await CountAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/budget-summary?competence=2026-13", _session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{Guid.NewGuid()}/financial-dashboard/budget-summary?competence={Month}", _session)).StatusCode);
    }

    [Fact]
    public async Task SummaryWithoutPfAccountsIsUnavailableNeverZero()
    {
        await ArrangeAsync("bs-sem-pf", 0);
        var summary = await SummaryAsync();
        Assert.Equal("Unavailable", summary.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("total").ValueKind);
    }

    /// <summary>
    /// Medição (mesmo conjunto de dados): 6 contas PF, 3 cartões e 25 lançamentos por conta. Compara a consulta antiga (overview completo, que o Acompanhamento usava) com o resumo, e mostra
    /// quanto cada etapa pesa: o resumo precisa do plano×realizado de cada conta PF; o overview ainda monta o resto do dashboard (e repete o plano das contas para outros blocos).
    /// </summary>
    [Fact(Skip = "Medição manual (leva ~2 min e depende da máquina): remova o Skip para reproduzir os tempos registrados na entrega.")]
    public async Task MeasureOverviewVersusSummary()
    {
        await ArrangeAsync("bs-medicao", 6);
        foreach (var account in _accounts) for (var i = 0; i < 25; i++) await ExpenseAsync(account, $"{10 + i}.00", $"Despesa {i}");
        for (var c = 0; c < 3; c++) { var card = await CardAsync($"Cartão {c}", _accounts[c]); for (var p = 0; p < 4; p++) await PurchaseAsync(card, $"{100 + p * 10}.00"); }
        await ConfigureTotalAsync(20000m);

        async Task<(double Median, long Bytes)> TimeAsync(string path, int runs = 7)
        {
            await GetAsync(path); // aquecimento (JIT, planos de consulta)
            var times = new List<double>(); long bytes = 0;
            for (var i = 0; i < runs; i++) { var watch = Stopwatch.StartNew(); bytes = (await GetAsync(path)).Bytes; times.Add(watch.Elapsed.TotalMilliseconds); }
            times.Sort(); return (times[times.Count / 2], bytes);
        }

        var overview = await TimeAsync($"overview?competence={Month}&scope=all");
        var summary = await TimeAsync($"budget-summary?competence={Month}");
        var perAccount = new List<double>();
        foreach (var account in _accounts) perAccount.Add((await TimeAsync($"plan-actual?competence={Month}&scope=account&accountId={account}", 3)).Median);
        var planAll = await TimeAsync($"plan-actual?competence={Month}&scope=all", 3);
        output.WriteLine($"MEDIÇÃO overview completo: mediana {overview.Median:F0} ms, {overview.Bytes} bytes (1 requisição)");
        output.WriteLine($"MEDIÇÃO budget-summary:    mediana {summary.Median:F0} ms, {summary.Bytes} bytes (1 requisição)");
        output.WriteLine($"MEDIÇÃO plan-actual por conta PF (etapa repetida no resumo): {string.Join(" + ", perAccount.Select(p => $"{p:F0}"))} = {perAccount.Sum():F0} ms; plan-actual consolidado: {planAll.Median:F0} ms");
        Assert.True(summary.Median < overview.Median, $"o resumo ({summary.Median:F0} ms) deveria ser mais rápido que o overview completo ({overview.Median:F0} ms)");
        Assert.True(summary.Bytes < overview.Bytes);
    }

    /// <summary>
    /// P1 (guia de configuração): tempo das consultas que o guia faz sozinho (contas, resumo PF, seis meses) contra as da Visão Geral completa (overview e plan-actual).
    /// Mesmo conjunto de dados da medição acima.
    /// </summary>
    [Fact(Skip = "Medição manual (leva ~2 min e depende da máquina): remova o Skip para reproduzir os tempos registrados na entrega.")]
    public async Task MeasureGuideQueriesVersusOverview()
    {
        await ArrangeAsync("bs-guia", 6);
        foreach (var account in _accounts) for (var i = 0; i < 25; i++) await ExpenseAsync(account, $"{10 + i}.00", $"Despesa {i}");
        for (var c = 0; c < 3; c++) { var card = await CardAsync($"Cartão {c}", _accounts[c]); for (var p = 0; p < 4; p++) await PurchaseAsync(card, $"{100 + p * 10}.00"); }
        await ConfigureTotalAsync(20000m);

        async Task<double> TimeAsync(string url)
        {
            async Task OnceAsync() { var r = await SendAsync(HttpMethod.Get, $"{Root}/{url}", _session); Assert.Equal(HttpStatusCode.OK, r.StatusCode); await r.Content.ReadAsStringAsync(); }
            await OnceAsync();
            var times = new List<double>();
            for (var i = 0; i < 5; i++) { var watch = Stopwatch.StartNew(); await OnceAsync(); times.Add(watch.Elapsed.TotalMilliseconds); }
            times.Sort(); return times[times.Count / 2];
        }
        foreach (var (name, url) in new[] {
            ("contas (listAccounts)", "accounts"),
            ("budget-summary", $"financial-dashboard/budget-summary?competence={Month}"),
            ("six-months (primary)", $"monthly-plan/six-months?from={Month}&scope=primary"),
            ("plan-actual (primary)", $"financial-dashboard/plan-actual?competence={Month}&scope=primary"),
            ("overview (all)", $"financial-dashboard/overview?competence={Month}&scope=all") })
            output.WriteLine($"MEDIÇÃO GUIA {name}: mediana {await TimeAsync(url):F0} ms");
    }
}

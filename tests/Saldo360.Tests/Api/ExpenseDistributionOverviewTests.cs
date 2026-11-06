using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transactions;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Dashboard Folio: bloco "expenseDistribution" da Visão Geral pela API real (Postgres). Mesma lista de fatos do orçamento agrupada por categoria principal, conferida com
/// "Despesas do mês" (Plano × Realizado) no mesmo escopo: subcategorias, cartão pela fatura, devolução líquida, despesa sem categoria, pagamento de fatura e transferência fora.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class ExpenseDistributionOverviewTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month => ThisMonth.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _pf, _pj, _housing, _rent, _food;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task<Guid> PostAsync(string path, object body)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/{path}", _session, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{path}: {await response.Content.ReadAsStringAsync()}");
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private Task<Guid> ExpenseAsync(Guid account, Guid category, string amount) =>
        PostAsync("transactions", new { accountId = account, categoryId = category, type = "Expense", description = "Efetivo", amount, transactionDate = Today });

    private async Task ArrangeAsync(string user)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        var opening = ThisMonth.AddMonths(-1);
        _pf = await PostAsync("accounts", new { name = "Conta PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "5000.00", openingBalanceDate = opening });
        _pj = await PostAsync("accounts", new { name = "Conta PJ", type = "Checking", financialScope = "PJ", openingBalance = "5000.00", openingBalanceDate = opening });
        _housing = await PostAsync("categories", new { name = "Moradia", type = "Expense" });
        _rent = await PostAsync("categories", new { name = "Aluguel", parentCategoryId = _housing });
        _food = await PostAsync("categories", new { name = "Alimentação", type = "Expense" });

        var rentPaid = await ExpenseAsync(_pf, _rent, "1000.00");
        await ExpenseAsync(_pf, _housing, "500.00");
        await ExpenseAsync(_pj, _food, "700.00");
        var card = await PostAsync("credit-cards", new { name = "Nubank", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _pf });
        await PostAsync($"credit-cards/{card}/purchases", new
        {
            description = "Mercado", purchaseDate = ThisMonth, totalAmount = "300.00", installmentCount = 1, categoryId = _food, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth, expenseNature = (string?)null,
        });
        await PostAsync("transfers", new { sourceAccountId = _pf, destinationAccountId = _pj, amount = 50m, transferDate = Today, description = "Entre contas" });

        await using var db = Db();
        var owner = _workspace.OwnerUserId; var now = DateTimeOffset.UtcNow;
        db.FinancialTransactions.Add(FinancialTransaction.CreateFromPluggy(_workspace.Id, _pf, null, CategoryType.Expense, "Débito sem categoria", 200m, Today, owner, now));
        db.FinancialTransactions.Add(FinancialTransaction.CreateInvoicePayment(_workspace.Id, _pf, "Pagamento da fatura", 999m, Today, owner, now));
        var refund = FinancialTransaction.CreateBankRefund(_workspace.Id, _pf, "Devolução do aluguel", 100m, Today, owner, now);
        db.FinancialTransactions.Add(refund);
        db.FinancialBankRefunds.Add(FinancialBankRefund.Create(_workspace.Id, refund.Id, rentPaid, 100m, owner, now));
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> OverviewAsync(string scope, Guid? account = null)
    {
        var url = $"{Root}/financial-dashboard/overview?competence={Month}&scope={scope}" + (account is null ? "" : $"&accountId={account}");
        var response = await SendAsync(HttpMethod.Get, url, _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Column(JsonElement distribution, string field) =>
        distribution.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty(field).ValueKind == JsonValueKind.Null ? "null" : c.GetProperty(field).GetString()!).ToArray();

    [Fact]
    public async Task ConsolidatedGroupsEveryCategoryWithSpendingAndMatchesTheMonthExpenses()
    {
        await ArrangeAsync("dist-consolidado");

        var overview = await OverviewAsync("all");
        var distribution = overview.GetProperty("expenseDistribution");

        Assert.Equal("Complete", distribution.GetProperty("state").GetString());
        // Aluguel (sub) + Moradia − devolução = 1400; cartão pela fatura; sem categoria explícito. Pagamento de fatura (999) e transferência (50) fora.
        Assert.Equal(["Moradia", "Alimentação", "Sem categoria"], Column(distribution, "name"));
        Assert.Equal(["1400.00", "1000.00", "200.00"], Column(distribution, "amount"));
        Assert.Equal(["53.8", "38.5", "7.7"], Column(distribution, "percentage"));
        Assert.Equal(_housing.ToString(), Column(distribution, "categoryId")[0]); Assert.Equal("null", Column(distribution, "categoryId")[2]);
        Assert.Equal("2600.00", distribution.GetProperty("total").GetString());
        // O total confere com "Despesas do mês" do mesmo escopo, que vem do Plano × Realizado.
        var plan = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/plan-actual?competence={Month}&scope=all", _session)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(plan.GetProperty("expenses").GetProperty("consumption").GetProperty("realized").GetString(), distribution.GetProperty("total").GetString());
        Assert.Equal("2600.00", distribution.GetProperty("reference").GetString());
        Assert.Empty(distribution.GetProperty("reasons").EnumerateArray());
    }

    [Fact]
    public async Task EachAccountSeesItsOwnSpendingAndTheCardsItPays()
    {
        await ArrangeAsync("dist-contas");

        var pf = (await OverviewAsync("account", _pf)).GetProperty("expenseDistribution");
        Assert.Equal("Complete", pf.GetProperty("state").GetString());
        Assert.Equal(["Moradia", "Alimentação", "Sem categoria"], Column(pf, "name"));
        Assert.Equal(["1400.00", "300.00", "200.00"], Column(pf, "amount"));
        Assert.Equal("1900.00", pf.GetProperty("total").GetString()); Assert.Equal("1900.00", pf.GetProperty("reference").GetString());

        var pj = (await OverviewAsync("account", _pj)).GetProperty("expenseDistribution");
        Assert.Equal("Complete", pj.GetProperty("state").GetString());
        Assert.Equal(["Alimentação"], Column(pj, "name")); Assert.Equal(["100.0"], Column(pj, "percentage"));
        Assert.Equal("700.00", pj.GetProperty("reference").GetString());

        // A conta principal é o escopo padrão.
        Assert.Equal("1900.00", (await OverviewAsync("primary")).GetProperty("expenseDistribution").GetProperty("total").GetString());
    }

    [Fact]
    public async Task SeeAllReturnsTheCompleteDistributionForTheSameCompetenceAndScopeAndValidatesLikeTheOverview()
    {
        await ArrangeAsync("dist-ver-todas");
        var url = $"{Root}/financial-dashboard/expense-distribution?competence={Month}";
        var response = await SendAsync(HttpMethod.Get, $"{url}&scope=account&accountId={_pf}", _session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var full = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Complete", full.GetProperty("state").GetString());
        Assert.Equal(["Moradia", "Alimentação", "Sem categoria"], Column(full, "name")); Assert.Equal(["Category", "Category", "Uncategorized"], Column(full, "kind"));
        Assert.Equal("1900.00", full.GetProperty("total").GetString()); Assert.Equal("1900.00", full.GetProperty("reference").GetString()); Assert.Equal(2, full.GetProperty("categoryCount").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/expense-distribution?competence=2026-13", _session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{url}&scope=account&accountId={Guid.NewGuid()}", _session)).StatusCode);
        var stranger = await SignInAsync("dist-ver-todas-outro");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{url}&scope=all", stranger)).StatusCode);
    }

    [Fact]
    public async Task ReadingTheDistributionWritesNothing()
    {
        await ArrangeAsync("dist-leitura");
        async Task<(int, int, int)> CountAsync()
        {
            await using var db = Db();
            return (await db.FinancialTransactions.CountAsync(), await db.FinancialCategories.CountAsync(), await db.FinancialBankRefunds.CountAsync());
        }
        var before = await CountAsync();
        await OverviewAsync("all"); await OverviewAsync("account", _pj);
        Assert.Equal(before, await CountAsync());
    }
}

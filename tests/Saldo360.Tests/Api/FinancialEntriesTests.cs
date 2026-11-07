using System.Net;
using System.Text.Json;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>T13.1: leitura unificada dos lançamentos (financial-entries) pela API real (Postgres): uma linha por movimento lógico, filtros e paginação no servidor.</summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Transactions")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialEntriesTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month => ThisMonth.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _pf, _pj, _expense, _income, _card;
    private Guid _cancelled;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task<Guid> PostAsync(string path, object body)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/{path}", _session, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{path}: {await response.Content.ReadAsStringAsync()}");
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task ArrangeAsync(string user)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        var opening = ThisMonth.AddMonths(-1);
        _pf = await PostAsync("accounts", new { name = "Conta PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "5000.00", openingBalanceDate = opening });
        _pj = await PostAsync("accounts", new { name = "Conta PJ", type = "Checking", financialScope = "PJ", openingBalance = "5000.00", openingBalanceDate = opening });
        _expense = await PostAsync("categories", new { name = "Despesas", type = "Expense" });
        _income = await PostAsync("categories", new { name = "Receitas", type = "Income" });
        Task<Guid> Tx(Guid account, Guid category, string type, string description, string amount) =>
            PostAsync("transactions", new { accountId = account, categoryId = category, type, description, amount, transactionDate = Today });
        await Tx(_pf, _expense, "Expense", "Mercado PF", "100.00");
        await Tx(_pf, _income, "Income", "Salário PF", "500.00");
        await Tx(_pj, _expense, "Expense", "Despesa PJ", "40.00");
        _cancelled = await Tx(_pf, _expense, "Expense", "Compra cancelada", "9.00");
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transactions/{_cancelled}/cancel", _session, new { })).StatusCode);
        await PostAsync("planned-transactions", new { accountId = _pf, categoryId = _expense, type = "Expense", amount = "200.00", dueDate = ThisMonth.AddMonths(1).AddDays(-1), description = "Previsão de aluguel" });
        await PostAsync("transfers", new { sourceAccountId = _pf, destinationAccountId = _pj, amount = 50m, transferDate = Today, description = T });
        _card = await PostAsync("credit-cards", new { name = "Nubank", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _pf });
        await PostAsync($"credit-cards/{_card}/purchases", new { description = "Mercado no cartão", purchaseDate = ThisMonth, totalAmount = "300.00", installmentCount = 1, categoryId = _expense, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth, expenseNature = (string?)null });
    }

    private async Task<JsonElement> GetAsync(string query, HttpStatusCode expected = HttpStatusCode.OK, string? session = null)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Root}/financial-entries?competence={Month}{query}", session ?? _session);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return expected == HttpStatusCode.OK ? JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone() : default;
    }

    private const string T = "Transferência de Conta PF para Conta PJ";
    private static string[] Descriptions(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("description").GetString()!).Select(d => d.StartsWith(T, StringComparison.Ordinal) ? T : d).ToArray();

    [Fact]
    public async Task EveryMovementAppearsOnceAcrossAllSourcesWithItsOriginTypeAndStatus()
    {
        await ArrangeAsync("entries-todas");
        var page = await GetAsync("&pageSize=100");
        var items = page.GetProperty("items").EnumerateArray().ToList();

        // Transações, previsão, cartão e UMA linha de transferência (as duas pontas viram origem e destino); o cancelado fica de fora por padrão.
        Assert.Equal(new[] { "Despesa PJ", "Mercado PF", "Mercado no cartão", "Previsão de aluguel", "Salário PF", T }, Descriptions(page).Order(StringComparer.Ordinal));
        Assert.Equal(items.Count, items.Select(e => e.GetProperty("id").GetString()).Distinct().Count());
        var transfer = items.Single(e => e.GetProperty("source").GetString() == "Transfer");
        Assert.Equal("Neutral", transfer.GetProperty("direction").GetString()); Assert.Equal("Conta PF", transfer.GetProperty("account").GetProperty("name").GetString()); Assert.Equal("Conta PJ", transfer.GetProperty("counterpart").GetProperty("name").GetString());
        var cardEntry = items.Single(e => e.GetProperty("source").GetString() == "CardPurchase");
        Assert.Equal("Nubank", cardEntry.GetProperty("card").GetProperty("name").GetString()); Assert.Equal("Consumes", cardEntry.GetProperty("budgetImpact").GetString()); Assert.Equal("AtInvoicePayment", cardEntry.GetProperty("cashImpact").GetString());
        var planned = items.Single(e => e.GetProperty("source").GetString() == "Planned");
        Assert.Equal("Pending", planned.GetProperty("status").GetString()); Assert.Equal("Due", planned.GetProperty("dateKind").GetString()); Assert.Contains("Realize", planned.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        var income = items.Single(e => e.GetProperty("description").GetString() == "Salário PF");
        Assert.Equal(("Income", "In", "Realized", "500.00"), (income.GetProperty("type").GetString(), income.GetProperty("direction").GetString(), income.GetProperty("status").GetString(), income.GetProperty("amount").GetString()));
        Assert.Equal(6, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task FiltersCombineOnTheServerScopeSeparatesPfFromPjAndCancelledOnesAreOptIn()
    {
        await ArrangeAsync("entries-filtros");
        Assert.DoesNotContain("Despesa PJ", Descriptions(await GetAsync("&scope=pf")));
        Assert.Equal(["Despesa PJ"], Descriptions(await GetAsync("&scope=pj&type=Expense")));   // a transferência PF→PJ não é despesa
        Assert.Equal([T], Descriptions(await GetAsync("&type=Transfer")));
        Assert.Equal(["Previsão de aluguel"], Descriptions(await GetAsync("&status=Pending")));
        Assert.Equal(["Mercado PF"], Descriptions(await GetAsync($"&accountId={_pf}&type=Expense&search=mercado&source=Transaction")));
        Assert.Contains("Compra cancelada", Descriptions(await GetAsync("&includeCancelled=true&pageSize=100")));
        Assert.Equal(["Compra cancelada"], Descriptions(await GetAsync("&status=Cancelled")));
        Assert.Equal(["Mercado no cartão"], Descriptions(await GetAsync($"&source=CardPurchase&accountId={_pf}")));
        Assert.Empty(Descriptions(await GetAsync("&search=inexistente")));
        // Outra competência não traz nada deste mês.
        var other = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/financial-entries?competence={ThisMonth.AddMonths(-3):yyyy-MM}", _session)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, other.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task PaginationAndSortingAreStableAndCoverEveryEntryExactlyOnce()
    {
        await ArrangeAsync("entries-paginas");
        var seen = new List<string>();
        for (var number = 1; number <= 3; number++) seen.AddRange(Descriptions(await GetAsync($"&pageSize=2&page={number}&sort=description&direction=asc")));
        Assert.Equal(new[] { "Despesa PJ", "Mercado no cartão", "Mercado PF", "Previsão de aluguel", "Salário PF", T }, seen);
        Assert.Empty(Descriptions(await GetAsync("&pageSize=2&page=4&sort=description&direction=asc")));
        var byAmount = await GetAsync("&sort=amount&direction=desc&pageSize=100");
        Assert.Equal(new[] { "Salário PF", "Mercado no cartão", "Previsão de aluguel", "Mercado PF", T, "Despesa PJ" }, Descriptions(byAmount));
    }

    [Fact]
    public async Task InvalidParametersAreRejectedAndOtherUsersDoNotSeeTheEntries()
    {
        await ArrangeAsync("entries-validacao");
        foreach (var bad in new[] { "&scope=xx", "&type=Foo", "&status=Bar", "&sort=zzz", "&page=0", "&pageSize=101" }) await GetAsync(bad, HttpStatusCode.BadRequest);
        var response = await SendAsync(HttpMethod.Get, $"{Root}/financial-entries?competence=2026-13", _session);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await GetAsync("", HttpStatusCode.NotFound, await SignInAsync("entries-outro"));
        Assert.Equal("no-store", (await SendAsync(HttpMethod.Get, $"{Root}/financial-entries?competence={Month}", _session)).Headers.CacheControl?.ToString());
    }
}

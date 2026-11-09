using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transactions;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// T13.4: filtros independentes (conta × cartão), agrupamento e totais por grupo pela API real (PostgreSQL). Os totais cobrem TODOS os lançamentos filtrados, não só a página.
/// Cenário (competência atual): Cartão A pago pela conta PF, Cartão B pago pela conta PJ, duas compras idênticas no A, uma parcela, um financiamento de fatura, movimentos bancários,
/// transferência PF→PJ, pagamento da fatura do A, estorno e um lançamento cancelado.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Transactions")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialEntriesGroupingTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month => ThisMonth.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _pf, _pj, _expense, _income, _cardA, _cardB;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task<Guid> PostAsync(string path, object body)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/{path}", _session, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{path}: {await response.Content.ReadAsStringAsync()}");
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private object Purchase(string description, string total, int installments = 1) => new
    {
        description, purchaseDate = ThisMonth, totalAmount = total, installmentCount = installments, categoryId = _expense, amountMode = "Total",
        previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth, expenseNature = (string?)null,
    };

    private async Task ArrangeAsync(string user)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        var opening = ThisMonth.AddMonths(-1);
        _pf = await PostAsync("accounts", new { name = "Conta PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "9000.00", openingBalanceDate = opening });
        _pj = await PostAsync("accounts", new { name = "Conta PJ", type = "Checking", financialScope = "PJ", openingBalance = "9000.00", openingBalanceDate = opening });
        _expense = await PostAsync("categories", new { name = "Despesas", type = "Expense" });
        _income = await PostAsync("categories", new { name = "Receitas", type = "Income" });
        _cardA = await PostAsync("credit-cards", new { name = "Cartão A", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _pf });
        _cardB = await PostAsync("credit-cards", new { name = "Cartão B", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _pj });

        await PostAsync($"credit-cards/{_cardA}/purchases", Purchase("Mercado", "100.00"));      // duas compras com descrição idêntica
        await PostAsync($"credit-cards/{_cardA}/purchases", Purchase("Mercado", "100.00"));
        await PostAsync($"credit-cards/{_cardA}/purchases", Purchase("Geladeira", "300.00", 3));  // parcela 1/3 = 100,00 na competência
        var financing = await PostAsync($"credit-cards/{_cardA}/purchases", Purchase("Parcelamento de fatura", "200.00"));
        await PostAsync($"credit-cards/{_cardB}/purchases", Purchase("Software", "50.00"));
        await using (var db = Db()) await db.Database.ExecuteSqlRawAsync("UPDATE financial_credit_card_purchases SET operation_kind = 'InvoiceFinancing' WHERE id = {0}", financing);

        Task<Guid> Tx(Guid account, Guid category, string type, string description, string amount) =>
            PostAsync("transactions", new { accountId = account, categoryId = category, type, description, amount, transactionDate = Today });
        var rent = await Tx(_pf, _expense, "Expense", "Mercado PF", "40.00");
        await Tx(_pf, _income, "Income", "Salário", "500.00");
        var cancelled = await Tx(_pf, _expense, "Expense", "Lançamento cancelado", "15.00");
        await PostAsync("transfers", new { sourceAccountId = _pf, destinationAccountId = _pj, amount = 200m, transferDate = Today, description = "Repasse" });
        var cancel = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transactions/{cancelled}/cancel", _session, new { });
        Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());

        await using (var db = Db())
        {
            var owner = _workspace.OwnerUserId; var now = DateTimeOffset.UtcNow;
            var refund = FinancialTransaction.CreateBankRefund(_workspace.Id, _pf, "Devolução", 10m, Today, owner, now);
            db.FinancialTransactions.Add(refund);
            db.FinancialBankRefunds.Add(FinancialBankRefund.Create(_workspace.Id, refund.Id, rent, 10m, owner, now));
            await db.SaveChangesAsync();
        }

        var invoices = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/credit-cards/{_cardA}/invoices?from={Month}&through={Month}", _session)).Content.ReadAsStringAsync()).RootElement;
        var invoice = invoices[0].GetProperty("id").GetGuid();
        await using (var official = Db()) await OfficialInvoiceFixture.CloseAsync(official, invoice);
        var payment = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards/{_cardA}/invoices/{invoice}/payments", _session, new { operationId = Guid.NewGuid(), financialAccountId = _pf, paymentDate = Today, amount = 60m });
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);
    }

    private async Task<JsonElement> PageAsync(string query)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Root}/financial-entries?competence={Month}&{query}", _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static List<JsonElement> Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToList();
    private static JsonElement Group(JsonElement page, string name) => page.GetProperty("groups").EnumerateArray().Single(g => Str(g, "name") == name);
    private static string[] Money(JsonElement g, params string[] names) => names.Select(n => Str(g, n)).ToArray();

    [Fact]
    public async Task IndependentAccountAndCardFiltersIntersectAndTheCardIsMatchedByItsRealId()
    {
        await ArrangeAsync("agrupa-filtros");

        var byCard = Items(await PageAsync($"cardId={_cardA}&pageSize=100"));
        var byAccount = Items(await PageAsync($"accountId={_pj}&pageSize=100"));
        var both = Items(await PageAsync($"accountId={_pf}&cardId={_cardA}&pageSize=100"));
        var impossible = await PageAsync($"accountId={_pj}&cardId={_cardA}&pageSize=100");
        var withoutCard = Items(await PageAsync("withoutCard=true&pageSize=100"));

        Assert.Equal(4, byCard.Count);                                                                                  // Mercado ×2, Geladeira 1/3 e o financiamento: só o cartão A
        Assert.All(byCard, e => Assert.Equal(_cardA, e.GetProperty("card").GetProperty("id").GetGuid()));
        Assert.Equal(byCard.Select(e => Str(e, "id")).Order(), both.Select(e => Str(e, "id")).Order());                // conta pagadora PF ∩ cartão A = o cartão A
        Assert.Empty(Items(impossible));                                                                                // conta PJ ∩ cartão A = vazio
        Assert.Empty(impossible.GetProperty("groups").EnumerateArray());                                                // grupo vazio não aparece
        Assert.All(byAccount, e => Assert.True(e.GetProperty("account").GetProperty("id").GetGuid() == _pj || e.GetProperty("counterpart").ValueKind != JsonValueKind.Null || e.GetProperty("card").ValueKind != JsonValueKind.Null));
        Assert.DoesNotContain(withoutCard, e => e.GetProperty("card").ValueKind != JsonValueKind.Null);                 // "Sem cartão": só movimentos não vinculados a cartão
        Assert.Contains(withoutCard, e => Str(e, "description") == "Salário");
    }

    [Fact]
    public async Task GroupedByCardEachCardAndTheBankAccountHaveExactCountsAndTotalsAndPfAndPjCardsStaySeparate()
    {
        await ArrangeAsync("agrupa-totais");

        var page = await PageAsync("groupBy=card&pageSize=100&includeCancelled=true");
        var a = Group(page, "Cartão A"); var b = Group(page, "Cartão B"); var pf = Group(page, "Conta PF");

        Assert.Equal(["Cartão A", "Cartão B", "Conta PF"], page.GetProperty("groups").EnumerateArray().Select(g => Str(g, "name")));   // cartões, depois contas
        Assert.Equal(("PF", "PJ"), (Str(a, "scope"), Str(b, "scope")));                                                              // PF e PJ separados pela conta pagadora
        // Cartão A: Mercado 100 + Mercado 100 + Geladeira 100 + financiamento 200.
        Assert.Equal(4, a.GetProperty("count").GetInt32());
        Assert.Equal(["500.00", "0.00", "500.00", "0.00", "500.00", "200.00"], Money(a, "outflow", "inflow", "budgetConsumed", "budgetReduced", "budgetNet", "invoiceFinancing"));   // o financiamento é destaque DENTRO das saídas e do consumo
        Assert.Equal((1, "50.00", "50.00"), (b.GetProperty("count").GetInt32(), Str(b, "outflow"), Str(b, "budgetConsumed")));
        // Conta PF: despesa 40, receita 500 + estorno 10, pagamento de fatura 60, transferência (1, não somada) e um cancelado fora das somas.
        Assert.Equal(["40.00", "510.00", "60.00", "40.00", "10.00", "30.00"], Money(pf, "outflow", "inflow", "paymentsOut", "budgetConsumed", "budgetReduced", "budgetNet"));
        Assert.Equal((1, 1), (pf.GetProperty("transferCount").GetInt32(), pf.GetProperty("cancelledCount").GetInt32()));
        Assert.Equal(pf.GetProperty("count").GetInt32() - 1, pf.GetProperty("activeCount").GetInt32());
        // Nenhum lançamento aparece em dois grupos: a soma das quantidades é o total de linhas.
        Assert.Equal(page.GetProperty("totalCount").GetInt32(), page.GetProperty("groups").EnumerateArray().Sum(g => g.GetProperty("count").GetInt32()));
        Assert.Equal(Items(page).Count, Items(page).Select(e => Str(e, "id")).Distinct().Count());
        // As duas compras idênticas continuam distintas.
        Assert.Equal(2, Items(page).Count(e => Str(e, "description") == "Mercado" && e.GetProperty("card").GetProperty("id").GetGuid() == _cardA));
    }

    [Fact]
    public async Task PaginationNeverChangesTheGroupTotalsAndEveryEntryAppearsOnceInStableGroupOrder()
    {
        await ArrangeAsync("agrupa-pagina");

        var first = await PageAsync("groupBy=card&pageSize=3&page=1&includeCancelled=true");
        var total = first.GetProperty("totalCount").GetInt32();
        var pages = Enumerable.Range(1, (int)Math.Ceiling(total / 3.0)).ToList();
        var seen = new List<string>(); var keys = new List<string>();
        foreach (var number in pages)
        {
            var page = await PageAsync($"groupBy=card&pageSize=3&page={number}&includeCancelled=true");
            Assert.Equal(first.GetProperty("groups").ToString(), page.GetProperty("groups").ToString());   // mesmos totais em qualquer página
            Assert.Equal(first.GetProperty("totals").ToString(), page.GetProperty("totals").ToString());
            seen.AddRange(Items(page).Select(e => Str(e, "id"))); keys.AddRange(Items(page).Select(e => Str(e, "groupKey")));
        }

        Assert.Equal(total, seen.Count);
        Assert.Equal(total, seen.Distinct().Count());                                                     // sem repetição entre páginas
        Assert.Equal(keys.OrderBy(k => first.GetProperty("groups").EnumerateArray().Select(g => Str(g, "key")).ToList().IndexOf(k)), keys);   // grupos contíguos, na ordem estável
        foreach (var g in first.GetProperty("groups").EnumerateArray())                                    // a página inicial indicada contém o primeiro item do grupo
        {
            var start = await PageAsync($"groupBy=card&pageSize=3&page={g.GetProperty("startsOnPage").GetInt32()}&includeCancelled=true");
            Assert.Contains(Items(start), e => Str(e, "groupKey") == Str(g, "key"));
        }
    }

    [Fact]
    public async Task WithoutGroupingTheOrderingAndPagingAreTheClassicOnesAndInvalidGroupingParametersAreRejected()
    {
        await ArrangeAsync("agrupa-compat");

        var plain = await PageAsync("pageSize=100");
        Assert.Empty(plain.GetProperty("groups").EnumerateArray());
        Assert.Equal("none", Str(plain, "groupBy"));
        Assert.All(Items(plain), e => Assert.Equal(JsonValueKind.Null, e.GetProperty("groupKey").ValueKind));
        var dates = Items(plain).Select(e => Str(e, "date")).ToList();
        Assert.Equal(dates.OrderByDescending(d => d), dates);                                              // data decrescente, como sempre

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{Root}/financial-entries?competence={Month}&groupBy=banana", _session)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{Root}/financial-entries?competence={Month}&cardId={Guid.NewGuid()}", _session)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{Root}/financial-entries?competence={Month}&cardId={_cardA}&withoutCard=true", _session)).StatusCode);
    }

    [Fact]
    public async Task GroupTotalsAgreeWithTheLedgerBudgetConsumptionOfTheSameFilteredRows()
    {
        await ArrangeAsync("agrupa-orcamento");

        var page = await PageAsync("groupBy=card&pageSize=100&scope=pf");
        var consumed = page.GetProperty("groups").EnumerateArray().Sum(g => decimal.Parse(Str(g, "budgetConsumed"), System.Globalization.CultureInfo.InvariantCulture));
        var viaItems = Items(page).Where(e => Str(e, "budgetImpact") == "Consumes" && Str(e, "status") != "Cancelled").Sum(e => decimal.Parse(Str(e, "amount"), System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(viaItems, consumed);                                  // o cabeçalho soma exatamente as linhas que consomem orçamento
        Assert.Equal(consumed - decimal.Parse(Str(page.GetProperty("totals"), "budgetReduced"), System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(Str(page.GetProperty("totals"), "budgetNet"), System.Globalization.CultureInfo.InvariantCulture));
    }
}

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// PROJ-02: a meta única "Todos os cartões" na projeção integrada de seis meses, com PostgreSQL real. A meta é de consumo (competência da fatura), compartilhada por todos os cartões,
/// separada do fluxo bancário; sem cartões vinculados o consumo nunca é lido como cobertura completa. Valores sintéticos.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CardTargetIntegratedTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Key(int offset) => ThisMonth.AddMonths(offset).ToString("yyyy-MM");
    private static string Text(JsonElement element, params string[] path) { foreach (var part in path) element = element.GetProperty(part); return element.GetString()!; }

    private sealed record IdDto(Guid Id);
    private sealed record Setup(string Session, WorkspaceDto Workspace, Guid Pf, Guid Pj, Guid Category)
    {
        public string Root => $"/api/financial-workspaces/{Workspace.Id}";
    }

    private async Task<Setup> ArrangeAsync(string user)
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var root = $"/api/financial-workspaces/{workspace.Id}";
        async Task<Guid> Create(string path, object body) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{root}/{path}", session, body))).Id;
        var opening = ThisMonth.AddMonths(-1);
        var pf = await Create("accounts", new { name = "Itaú PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "20000.00", openingBalanceDate = opening });
        var pj = await Create("accounts", new { name = "Itaú PJ", type = "Checking", financialScope = "PJ", openingBalance = "500.00", openingBalanceDate = opening });
        var category = await Create("categories", new { name = "Compras", type = "Expense" });
        return new(session, workspace, pf, pj, category);
    }

    private async Task<Guid> CardAsync(Setup s, string name)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/credit-cards", s.Session, new { name, issuer = "Banco", creditLimit = "30000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = s.Pf });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task PurchaseAsync(Setup s, Guid card, string amount, int installments = 1)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/credit-cards/{card}/purchases", s.Session, new
        {
            description = "Compra", purchaseDate = ThisMonth, totalAmount = amount, installmentCount = installments, categoryId = s.Category, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task TargetAsync(Setup s, int monthOffset, string amount)
    {
        var budget = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/budgets", s.Session, new { competence = Key(monthOffset) });
        Assert.Equal(HttpStatusCode.Created, budget.StatusCode);
        var id = (await ReadAsync<IdDto>(budget)).Id;
        var target = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/budgets/{id}/targets", s.Session, new { groupingType = "AllCards", plannedAmount = amount });
        Assert.Equal(HttpStatusCode.Created, target.StatusCode);
    }

    private async Task<JsonElement> IntegratedAsync(Setup s)
    {
        var response = await SendAsync(HttpMethod.Get, $"{s.Root}/monthly-plan/six-months", s.Session);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("integrated").Clone();
    }

    private static JsonElement CardsOf(JsonElement integrated, int index) => integrated.GetProperty("months")[index].GetProperty("cards");

    [Fact]
    public async Task WithoutLinkedCards_TheTargetIsNotReadAsCompleteCoverageNorAsZeroConsumption()
    {
        var s = await ArrangeAsync("proj02-nocards");
        await TargetAsync(s, 0, "5000.00");

        var integrated = await IntegratedAsync(s);
        var month = integrated.GetProperty("months")[0];
        var cards = CardsOf(integrated, 0);

        Assert.Equal("Unavailable", Text(cards, "state"));
        Assert.Equal("5000.00", Text(cards, "target"));
        Assert.Contains("NoCards", cards.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Contains("NoCardsRegistered", month.GetProperty("quality").EnumerateArray().Select(r => r.GetString()));
        Assert.False(month.GetProperty("complete").GetBoolean());
        Assert.Equal(0, cards.GetProperty("cards").GetArrayLength());
    }

    [Fact]
    public async Task OneTargetIsSharedByEveryCard_ConsumptionSumsAndInstallmentsCommitTheFollowingMonths()
    {
        var s = await ArrangeAsync("proj02-shared");
        var black = await CardAsync(s, "Black");
        var nubank = await CardAsync(s, "Nubank");
        await PurchaseAsync(s, black, "3000.00");
        await PurchaseAsync(s, nubank, "900.00", installments: 3); // 300,00 por mês
        await TargetAsync(s, 0, "5000.00");
        await TargetAsync(s, 1, "200.00");

        var integrated = await IntegratedAsync(s);
        var first = CardsOf(integrated, 0);
        var second = CardsOf(integrated, 1);
        var third = CardsOf(integrated, 2);

        // Mês 0: 3.000 + 300 de dois cartões contra uma única meta de 5.000; sobra 1.700 e nada acima.
        Assert.Equal(("5000.00", "3300.00", "1700.00", "0.00"), (Text(first, "target"), Text(first, "projected"), Text(first, "remaining"), Text(first, "excessProjected")));
        Assert.Equal(2, first.GetProperty("cards").GetArrayLength());
        // Mês 1: só a parcela de 300 compromete o mês; a meta de 200 fica 100 acima. Mês 2: parcela de 300 sem meta cadastrada.
        Assert.Equal(("200.00", "300.00", "0.00", "100.00"), (Text(second, "target"), Text(second, "projected"), Text(second, "remaining"), Text(second, "excessProjected")));
        Assert.Equal("300.00", Text(third, "projected"));
        Assert.Equal("NotDefined", Text(third, "plannedState"));
        Assert.Equal(JsonValueKind.Null, third.GetProperty("target").ValueKind);
    }

    [Fact]
    public async Task ATargetBelowTheConsumptionShowsTheExcessAndOneAboveShowsTheRemainder()
    {
        var s = await ArrangeAsync("proj02-excess");
        var card = await CardAsync(s, "Black");
        await PurchaseAsync(s, card, "6000.00");
        await TargetAsync(s, 0, "5000.00");

        var below = CardsOf(await IntegratedAsync(s), 0);
        Assert.Equal(("6000.00", "0.00", "1000.00"), (Text(below, "projected"), Text(below, "remaining"), Text(below, "excessProjected")));
    }

    [Fact]
    public async Task BankMovementsAndTheTargetItselfNeverChangeTheCardConsumption_AndReadingWritesNothing()
    {
        var s = await ArrangeAsync("proj02-bank");
        var card = await CardAsync(s, "Black");
        await PurchaseAsync(s, card, "1000.00");
        await TargetAsync(s, 0, "5000.00");
        var before = Text(CardsOf(await IntegratedAsync(s), 0), "projected");

        // Despesa bancária e transferência entre contas não são consumo de cartão.
        var expense = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/transactions", s.Session, new { accountId = s.Pf, categoryId = s.Category, type = "Expense", amount = "700.00", transactionDate = Today, description = "Débito" });
        Assert.Equal(HttpStatusCode.Created, expense.StatusCode);
        var transfer = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/transfers", s.Session, new { sourceAccountId = s.Pf, destinationAccountId = s.Pj, amount = "400.00", transferDate = Today, description = "Transferência" });
        Assert.True(transfer.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await transfer.Content.ReadAsStringAsync());

        await using var db = Db();
        var counts = (await db.FinancialTransactions.CountAsync(), await db.FinancialCreditCardPurchases.CountAsync(), await db.FinancialCreditCardInvoices.CountAsync(), await db.FinancialBudgetTargets.CountAsync());
        var after = Text(CardsOf(await IntegratedAsync(s), 0), "projected");

        Assert.Equal(("1000.00", "1000.00"), (before, after));
        Assert.Equal(counts, (await db.FinancialTransactions.CountAsync(), await db.FinancialCreditCardPurchases.CountAsync(), await db.FinancialCreditCardInvoices.CountAsync(), await db.FinancialBudgetTargets.CountAsync()));
    }

    [Fact]
    public async Task PayingTheInvoiceDoesNotConsumeTheTargetAgain()
    {
        var s = await ArrangeAsync("proj02-payment");
        var card = await CardAsync(s, "Black");
        await PurchaseAsync(s, card, "1000.00");
        await TargetAsync(s, 0, "5000.00");
        var before = CardsOf(await IntegratedAsync(s), 0);
        Assert.Equal("1000.00", Text(before, "projected"));

        Guid invoice;
        await using (var db = Db()) invoice = (await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.CreditCardId == card).OrderBy(i => i.DueDate).FirstAsync()).Id;
        await using (var official = Db()) await OfficialInvoiceFixture.CloseAsync(official, invoice);
        var payment = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/credit-cards/{card}/invoices/{invoice}/payments", s.Session,
            new { operationId = Guid.NewGuid(), financialAccountId = s.Pf, paymentDate = Today, amount = "1000.00" });
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);

        var after = CardsOf(await IntegratedAsync(s), 0);
        Assert.Equal(("1000.00", "4000.00", "0.00"), (Text(after, "projected"), Text(after, "remaining"), Text(after, "excessProjected"))); // o pagamento é fluxo de caixa, não consumo
    }
}

using System.Globalization;
using System.Net;
using System.Text.Json;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Recurrences;
using Saldo360.Tests.Support;
using static Saldo360.Tests.Api.FinancialEntriesReconciliationTests;

namespace Saldo360.Tests.Api;

/// <summary>
/// T13.2 (Fase B, harmonização): o orçamento PF (<c>pfBudgetControl</c>) e o Razão (financial-entries) leem as MESMAS assinaturas de cartão do motor de projeção.
/// Cada teste compara realizado, comprometido e estimado, separando cartões e fora dos cartões, para a mesma competência.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialEntriesHarmonizationTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static DateOnly NextMonth => ThisMonth.AddMonths(1);

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _pf, _pj, _expense, _card;
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
        _pf = await PostAsync("accounts", new { name = "Conta PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "9000.00", openingBalanceDate = opening });
        _pj = await PostAsync("accounts", new { name = "Conta PJ", type = "Checking", financialScope = "PJ", openingBalance = "9000.00", openingBalanceDate = opening });
        _expense = await PostAsync("categories", new { name = "Despesas", type = "Expense" });
        _card = await PostAsync("credit-cards", new { name = "Nubank", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _pf });
    }

    private Task<Guid> SubscriptionAsync(Guid card, string description, string amount, DateOnly competence, bool estimated = false) =>
        PostAsync("recurrences", new { creditCardId = card, categoryId = _expense, type = "Expense", description, amount, isEstimated = estimated, frequency = "Monthly", interval = 1, startDate = competence.AddDays(2), dayOfMonth = 3 });

    private async Task DetectedAsync(Guid card, string description, decimal amount, DateOnly competence)
    {
        await using var db = Db();
        var details = new RecurrenceDetails(null, _expense, CategoryType.Expense, description, amount, true, RecurrenceFrequency.Monthly, 1, competence.AddDays(2), null, null, 3, card);
        db.FinancialRecurrences.Add(FinancialRecurrence.CreatePluggyDetected(_workspace.Id, details, $"ext-{description}", 3, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> JsonAsync(string url)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Root}/{url}", _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static decimal Dec(JsonElement element, string name) => decimal.Parse(element.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    private async Task<(List<JsonElement> Items, Components Ledger, JsonElement Budget)> SnapshotAsync(DateOnly competence)
    {
        var month = competence.ToString("yyyy-MM");
        var items = (await JsonAsync($"financial-entries?competence={month}&scope=pf&pageSize=100")).GetProperty("items").EnumerateArray().ToList();
        var budget = (await JsonAsync($"financial-dashboard/overview?competence={month}&scope=all")).GetProperty("pfBudgetControl");
        return (items, Classify(items), budget);
    }

    /// <summary>Realizado, comprometido e estimado, cartões × fora dos cartões, e o total projetado: o Razão tem de igualar o orçamento.</summary>
    private static void AssertReconciled(Components ledger, JsonElement budget)
    {
        var cards = budget.GetProperty("cards"); var outside = budget.GetProperty("outsideCards"); var total = budget.GetProperty("total");
        Assert.Equal((Dec(cards, "realized"), Dec(cards, "committed"), Dec(cards, "estimated")), (ledger.CardsRealized, ledger.CardsCommitted, ledger.CardsEstimated));
        Assert.Equal((Dec(outside, "realized"), Dec(outside, "committed")), (ledger.Realized, ledger.Committed));
        Assert.Equal(Dec(total, "projected"), ledger.Realized + ledger.Committed + ledger.CardsRealized + ledger.CardsCommitted + ledger.CardsEstimated);
    }

    private static List<JsonElement> Subscriptions(IEnumerable<JsonElement> items) =>
        items.Where(e => e.GetProperty("source").GetString() == "CardProjected").ToList();

    [Fact]
    public async Task ManualDetectedAndRepeatedSubscriptionsComposeTheBudgetOnceAndTheSameCommitmentIsNeverCountedTwice()
    {
        await ArrangeAsync("harmoniza-assinaturas");
        await SubscriptionAsync(_card, "Streaming", "39.90", NextMonth);          // cenário 1: manual, sem cobrança confirmada
        await SubscriptionAsync(_card, "Streaming", "20.00", NextMonth);          // cenário 4: outra assinatura com a mesma descrição
        await DetectedAsync(_card, "Cloud", 25m, NextMonth);                      // cenário 2: detectada pelo Pluggy

        var (items, ledger, budget) = await SnapshotAsync(NextMonth);

        var lines = Subscriptions(items);
        Assert.Equal(["Cloud", "Streaming", "Streaming"], lines.Select(e => e.GetProperty("description").GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal((59.90m, 25m), (ledger.CardsCommitted, ledger.CardsEstimated));   // fixas = comprometido; detectada = estimativa
        AssertReconciled(ledger, budget);

        await SubscriptionAsync(_card, "Cloud", "25.00", NextMonth);              // cenário 3: a mesma assinatura cadastrada à mão depois da detecção
        var (afterItems, afterLedger, afterBudget) = await SnapshotAsync(NextMonth);

        Assert.Single(Subscriptions(afterItems), e => e.GetProperty("description").GetString() == "Cloud");
        Assert.Equal("ManualRecurring", Subscriptions(afterItems).Single(e => e.GetProperty("description").GetString() == "Cloud").GetProperty("origin").GetString());
        Assert.Equal((84.90m, 0m), (afterLedger.CardsCommitted, afterLedger.CardsEstimated));
        AssertReconciled(afterLedger, afterBudget);
    }

    [Fact]
    public async Task ASubscriptionLaunchedOnTheInvoiceReplacesItsProjectionAndTheClosedInvoiceTotalAndItsPartialPaymentChangeNoBudgetConsumption()
    {
        await ArrangeAsync("harmoniza-fatura");
        await PostAsync($"credit-cards/{_card}/purchases", new { description = "Mercado no cartão", purchaseDate = ThisMonth, totalAmount = "300.00", installmentCount = 1, categoryId = _expense, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth, expenseNature = (string?)null });
        var recurrence = await SubscriptionAsync(_card, "Streaming", "39.90", NextMonth);
        var before = await SnapshotAsync(NextMonth);
        Assert.Equal(39.90m, before.Ledger.CardsCommitted);

        // cenário 5: lançada na fatura (materializada) → vira compra do cartão, sem repetir a projeção
        var materialized = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/recurrences/{recurrence}/materialize", _session, new { from = NextMonth.AddDays(2), through = NextMonth.AddDays(3) });
        Assert.Equal(HttpStatusCode.OK, materialized.StatusCode);
        var launched = await SnapshotAsync(NextMonth);
        Assert.Equal((39.90m, 0m), (launched.Ledger.CardsRealized, launched.Ledger.CardsCommitted));
        AssertReconciled(launched.Ledger, launched.Budget);

        // cenários 6 e 7: fatura do mês fechada por total e paga parcialmente — pagamento é caixa, não consumo do orçamento
        var invoices = await JsonAsync($"credit-cards/{_card}/invoices?from={ThisMonth:yyyy-MM}&through={ThisMonth:yyyy-MM}");
        var invoice = invoices[0].GetProperty("id").GetGuid();
        await using (var official = Db()) await OfficialInvoiceFixture.CloseAsync(official, invoice);
        var current = await SnapshotAsync(ThisMonth);
        var payment = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards/{_card}/invoices/{invoice}/payments", _session, new { operationId = Guid.NewGuid(), financialAccountId = _pf, paymentDate = Today, amount = 100m });
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);
        var paid = await SnapshotAsync(ThisMonth);

        Assert.Equal(300m, current.Ledger.CardsRealized);
        Assert.Equal((current.Ledger.CardsRealized, current.Ledger.CardsCommitted, current.Ledger.CardsEstimated), (paid.Ledger.CardsRealized, paid.Ledger.CardsCommitted, paid.Ledger.CardsEstimated));
        Assert.Equal(Dec(current.Budget.GetProperty("total"), "projected"), Dec(paid.Budget.GetProperty("total"), "projected"));
        Assert.Contains(paid.Items, e => e.GetProperty("type").GetString() == "Payment");   // o pagamento aparece como movimento de caixa…
        Assert.Contains("Pagamento de fatura/dívida (caixa)", paid.Ledger.Excluded.Keys);     // …e fica fora do consumo
        AssertReconciled(paid.Ledger, paid.Budget);
    }

    [Fact]
    public async Task ASubscriptionOnAPjCardStaysOutOfThePfLedgerAndBudget()
    {
        await ArrangeAsync("harmoniza-pj");
        var pjCard = await PostAsync("credit-cards", new { name = "Cartão PJ", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _pj });
        await SubscriptionAsync(pjCard, "Software PJ", "99.00", NextMonth);
        await SubscriptionAsync(_card, "Streaming", "39.90", NextMonth);

        var (items, ledger, budget) = await SnapshotAsync(NextMonth);

        Assert.DoesNotContain(items, e => e.GetProperty("description").GetString() == "Software PJ");
        Assert.Equal(39.90m, ledger.CardsCommitted);
        AssertReconciled(ledger, budget);
    }

    [Fact]
    public async Task SameNameWithDifferentAmountsKeepsBothChargesFlaggedForReviewWhileTheSameAmountCollapsesToTheManualOne()
    {
        await ArrangeAsync("harmoniza-duplicidade");
        await SubscriptionAsync(_card, "Google", "25.00", NextMonth);
        await DetectedAsync(_card, "Google", 59.90m, NextMonth);

        var (items, ledger, budget) = await SnapshotAsync(NextMonth);

        var google = Subscriptions(items).Where(e => e.GetProperty("description").GetString() == "Google").ToList();
        Assert.Equal(["ManualRecurring", "Recurring"], google.Select(e => e.GetProperty("origin").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(google, e => Assert.Contains("PossibleDuplicate", e.GetProperty("flags").EnumerateArray().Select(f => f.GetString())));
        Assert.Equal((25m, 59.90m), (ledger.CardsCommitted, ledger.CardsEstimated));   // nada foi descartado
        AssertReconciled(ledger, budget);
    }

    [Fact]
    public async Task ALinkedRefundReducesTheConsumptionOfItsExpenseAndAnUnlinkedCreditGetsNoInventedClassification()
    {
        await ArrangeAsync("harmoniza-estorno");
        var rent = await PostAsync("transactions", new { accountId = _pf, categoryId = _expense, type = "Expense", description = "Aluguel", amount = "1000.00", transactionDate = Today });
        var income = await PostAsync("categories", new { name = "Receitas", type = "Income" });
        await PostAsync("transactions", new { accountId = _pf, categoryId = income, type = "Income", description = "Crédito avulso", amount = "30.00", transactionDate = Today });
        await using (var db = Db())
        {
            var owner = _workspace.OwnerUserId; var now = DateTimeOffset.UtcNow;
            var refund = Saldo360.Domain.Transactions.FinancialTransaction.CreateBankRefund(_workspace.Id, _pf, "Devolução do aluguel", 100m, Today, owner, now);
            db.FinancialTransactions.Add(refund);
            db.FinancialBankRefunds.Add(Saldo360.Domain.Transactions.FinancialBankRefund.Create(_workspace.Id, refund.Id, rent, 100m, owner, now));
            await db.SaveChangesAsync();
        }

        var (items, ledger, budget) = await SnapshotAsync(ThisMonth);

        Assert.Equal(900m, ledger.Realized);   // 1.000 − 100 vinculados; o crédito avulso não abate nada
        Assert.Equal("Reduces", items.Single(e => e.GetProperty("description").GetString() == "Devolução do aluguel").GetProperty("budgetImpact").GetString());
        Assert.Equal("None", items.Single(e => e.GetProperty("description").GetString() == "Crédito avulso").GetProperty("budgetImpact").GetString());
        Assert.Equal(30m, ledger.Excluded["Receita"]);
        AssertReconciled(ledger, budget);
    }
}

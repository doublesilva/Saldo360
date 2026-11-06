using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// T11.2: bloco "budgetControl" da Visão Geral pela API real (Postgres). Meta de cartões = meta "Todos os cartões" do orçamento do mês; orçamento total = referência agregada do mês.
/// Fora dos cartões = total − cartões. A consulta é somente leitura.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class BudgetControlOverviewTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month => ThisMonth.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _account, _expense;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task ArrangeAsync(string user)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        _expense = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Despesas", type = "Expense" }))).Id;
        _account = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
            new { name = "Itaú PF", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = ThisMonth.AddMonths(-1), overdraftLimit = "1000.00" }))).Id;
    }

    private async Task<Guid> CardAsync(string name)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards", _session, new { name, issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _account });
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

    private async Task ExpenseAsync(string amount) =>
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transactions", _session,
            new { accountId = _account, categoryId = _expense, type = "Expense", description = "Efetivo", amount, transactionDate = Today })).StatusCode);

    private async Task ConfigureBudgetAsync(string cardTarget, decimal total)
    {
        var budget = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets", _session, new { competence = Month }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/{budget}/targets", _session, new { groupingType = "AllCards", plannedAmount = cardTarget })).StatusCode);
        await using var db = Db();
        db.FinancialMonthlyAggregateTargets.Add(FinancialMonthlyAggregateTarget.Create(_workspace.Id, ThisMonth, MonthlyAggregateTargetType.TotalExpenses, total, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> ControlAsync(string scope, Guid? account = null)
    {
        var url = $"{Root}/financial-dashboard/overview?competence={Month}&scope={scope}" + (account is null ? "" : $"&accountId={account}");
        var response = await SendAsync(HttpMethod.Get, url, _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("budgetControl").Clone();
    }

    private static string Text(JsonElement e, params string[] path) { foreach (var p in path) e = e.GetProperty(p); return e.GetString()!; }

    [Fact]
    public async Task ConfiguredBudgetSeparatesCardsFromTheRestKeepsTheExcessAndRanksThePressure()
    {
        await ArrangeAsync("bc-configurado");
        var card = await CardAsync("Nubank");
        await PurchaseAsync(card, "5200.00");
        await ExpenseAsync("1000.00");
        await ConfigureBudgetAsync("5000.00", 12500m);

        var control = await ControlAsync("all");

        Assert.True(control.GetProperty("targetsAttributable").GetBoolean());
        Assert.Equal("Historical", Text(control, "totalTargetOrigin")); // T12.2: meta do registro mensal anterior à recorrência
        Assert.Equal("12500.00", Text(control, "total", "target")); Assert.Equal("5000.00", Text(control, "cards", "target")); Assert.Equal("7500.00", Text(control, "outsideCards", "target"));
        Assert.Equal("5200.00", Text(control, "cards", "projected")); Assert.Equal("200.00", Text(control, "cards", "excess")); Assert.Equal("Exceeded", Text(control, "cards", "status"));
        Assert.Equal("1000.00", Text(control, "outsideCards", "projected")); // o pagamento da fatura não existe aqui; a compra entra uma só vez, pela fatura, no grupo dos cartões
        Assert.Equal("6200.00", Text(control, "total", "projected")); Assert.Equal("6300.00", Text(control, "total", "margin"));
        var first = control.GetProperty("pressures")[0];
        Assert.Equal("CardsOverTarget", first.GetProperty("kind").GetString()); Assert.Equal("200.00", first.GetProperty("amount").GetString()); Assert.Equal(1, first.GetProperty("rank").GetInt32());
        Assert.Contains("CardsOverTarget", control.GetProperty("recommendations").EnumerateArray().Select(r => r.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task BudgetNotConfiguredShowsConsumptionAndNoTargetsAndRecommendsConfiguringIt()
    {
        await ArrangeAsync("bc-sem-orcamento");
        await ExpenseAsync("300.00");

        var control = await ControlAsync("all");

        Assert.Equal("TargetNotDefined", Text(control, "state")); Assert.False(control.GetProperty("total").GetProperty("targetDefined").GetBoolean());
        Assert.Equal(JsonValueKind.Null, control.GetProperty("total").GetProperty("target").ValueKind);
        Assert.Equal("Undefined", Text(control, "totalTargetOrigin")); // T12.2: sem configuração a origem é "não configurada", nunca uma meta zero
        Assert.Equal(JsonValueKind.Null, control.GetProperty("cards").GetProperty("target").ValueKind); Assert.Equal(JsonValueKind.Null, control.GetProperty("total").GetProperty("margin").ValueKind);
        Assert.Equal("300.00", Text(control, "outsideCards", "realized"));
        Assert.Contains("BudgetTotalNotDefined", control.GetProperty("recommendations").EnumerateArray().Select(r => r.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task SingleAccountDoesNotPresentTheGlobalTargetsAsItsOwn()
    {
        await ArrangeAsync("bc-conta");
        await PurchaseAsync(await CardAsync("Nubank"), "400.00");
        await ExpenseAsync("300.00");
        await ConfigureBudgetAsync("5000.00", 12500m);

        var control = await ControlAsync("account", _account);

        Assert.False(control.GetProperty("targetsAttributable").GetBoolean());
        Assert.Equal("TargetNotAttributable", Text(control, "cards", "state")); Assert.Equal("TargetNotAttributable", Text(control, "outsideCards", "state"));
        Assert.Equal(JsonValueKind.Null, control.GetProperty("total").GetProperty("target").ValueKind);
        // T12.2: a origem da meta global também não é atribuída à conta isolada.
        Assert.True(!control.TryGetProperty("totalTargetOrigin", out var origin) || origin.ValueKind == JsonValueKind.Null);
        Assert.Equal("300.00", Text(control, "outsideCards", "realized"));
        Assert.Contains("TargetsGlobalOnly", control.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public async Task ReadingTheBudgetControlWritesNothing()
    {
        await ArrangeAsync("bc-leitura");
        var card = await CardAsync("Nubank");
        await PurchaseAsync(card, "800.00");
        await ExpenseAsync("100.00");
        await ConfigureBudgetAsync("5000.00", 12500m);
        async Task<(int Transactions, int Targets, int Aggregates, int Purchases, int Snapshots)> CountAsync()
        {
            await using var db = Db();
            return (await db.FinancialTransactions.CountAsync(), await db.FinancialBudgetTargets.CountAsync(), await db.FinancialMonthlyAggregateTargets.CountAsync(),
                await db.FinancialCreditCardPurchases.CountAsync(), await db.OverdraftRecoverySnapshots.CountAsync());
        }
        var before = await CountAsync();
        await ControlAsync("all"); await ControlAsync("account", _account); await ControlAsync("primary");
        Assert.Equal(before, await CountAsync());
    }
}

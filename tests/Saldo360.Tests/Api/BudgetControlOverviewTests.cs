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

    private async Task<JsonElement> PfControlAsync(string scope, Guid? account = null)
    {
        var url = $"{Root}/financial-dashboard/overview?competence={Month}&scope={scope}" + (account is null ? "" : $"&accountId={account}");
        var response = await SendAsync(HttpMethod.Get, url, _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("pfBudgetControl").Clone();
    }

    [Fact]
    public async Task PfBudgetIsTheSameForEveryBankScopeAndNeverIncludesPjSpending()
    {
        await ArrangeAsync("bc-pf");
        var pj = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
            new { name = "Itaú PJ", type = "Checking", financialScope = "PJ", openingBalance = "9000.00", openingBalanceDate = ThisMonth.AddMonths(-1) }))).Id;
        await PurchaseAsync(await CardAsync("Nubank"), "5200.00");
        await ExpenseAsync("1000.00");
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transactions", _session,
            new { accountId = pj, categoryId = _expense, type = "Expense", description = "Despesa PJ", amount = "400.00", transactionDate = Today })).StatusCode);
        await ConfigureBudgetAsync("5000.00", 12500m);

        var viaPf = await PfControlAsync("account", _account);
        var viaPj = await PfControlAsync("account", pj);
        var viaAll = await PfControlAsync("all");

        Assert.True(viaPf.GetProperty("targetsAttributable").GetBoolean()); Assert.Equal("pf", Text(viaPf, "scopeKind"));
        // PF: 1000 fora dos cartões + 5200 no cartão (pago pela conta PF). Os 400 da PJ não entram, qualquer que seja a conta escolhida.
        Assert.Equal("6200.00", Text(viaPf, "total", "projected")); Assert.Equal("1000.00", Text(viaPf, "outsideCards", "realized")); Assert.Equal("5200.00", Text(viaPf, "cards", "realized"));
        Assert.Equal("12500.00", Text(viaPf, "total", "target")); Assert.Equal("49.6", Text(viaPf, "total", "projectedPercent"));
        Assert.Equal(viaPf.ToString(), viaPj.ToString()); Assert.Equal(viaPf.ToString(), viaAll.ToString());
        // O orçamento bancário (budgetControl) continua como era: a conta PJ isolada não recebe a meta global.
        Assert.False((await ControlAsync("account", pj)).GetProperty("targetsAttributable").GetBoolean());
    }

    [Fact]
    public async Task PfBudgetWithoutTotalTargetKeepsTheCardTargetAndInventsNothingAndAnUnassignedCardMakesItPartial()
    {
        await ArrangeAsync("bc-pf-sem-total");
        var unpaid = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards", _session, new { name = "Sem pagador", issuer = "Banco", creditLimit = "1000.00", closingDay = 5, dueDay = 10 }))).Id;
        Assert.NotEqual(Guid.Empty, unpaid);
        await PurchaseAsync(await CardAsync("Nubank"), "800.00");
        var budget = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets", _session, new { competence = Month }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/{budget}/targets", _session, new { groupingType = "AllCards", plannedAmount = "1000.00" })).StatusCode);

        var control = await PfControlAsync("account", _account);

        Assert.False(control.GetProperty("total").GetProperty("targetDefined").GetBoolean()); Assert.Equal(JsonValueKind.Null, control.GetProperty("total").GetProperty("target").ValueKind);
        Assert.Equal("1000.00", Text(control, "cards", "target")); Assert.Equal("80.0", Text(control, "cards", "projectedPercent"));
        Assert.Equal(JsonValueKind.Null, control.GetProperty("outsideCards").GetProperty("target").ValueKind); // sem a meta total não há meta fora dos cartões
        Assert.Contains("MissingPayerAccount", control.GetProperty("cards").GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("TargetNotDefined", Text(control, "state")); // meta total ausente prevalece no estado; a cobertura parcial está nos motivos do grupo
    }

    private async Task<Guid> NewAccountAsync(string name, string scope) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
        new { name, type = "Checking", financialScope = scope, openingBalance = "9000.00", openingBalanceDate = ThisMonth.AddMonths(-1) }))).Id;

    private async Task SpendAsync(Guid account, string amount) => Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transactions", _session,
        new { accountId = account, categoryId = _expense, type = "Expense", description = "Histórico", amount, transactionDate = Today })).StatusCode);

    [Fact]
    public async Task PfBudgetKeepsTheHistoryOfInactivePfAccountsAndTheirCardsWithoutPjOrDoubleCounting()
    {
        await ArrangeAsync("bc-pf-inativas");
        var pfOld = await NewAccountAsync("PF antiga", "PF"); var pjOld = await NewAccountAsync("PJ antiga", "PJ");
        await SpendAsync(pfOld, "300.00"); await SpendAsync(pjOld, "200.00"); await ExpenseAsync("100.00");
        var card = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards", _session,
            new { name = "Cartão da PF antiga", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = pfOld }))).Id;
        await PurchaseAsync(card, "700.00");
        await using (var db = Db()) await db.FinancialAccounts.Where(a => a.Id == pfOld || a.Id == pjOld).ExecuteUpdateAsync(set => set.SetProperty(a => a.IsActive, false));

        var control = await PfControlAsync("account", _account);

        // PF: 300 (PF inativa) + 100 (PF ativa) fora dos cartões e 700 no cartão da PF inativa; os 200 da PJ inativa não entram e nada é contado duas vezes.
        Assert.Equal("400.00", Text(control, "outsideCards", "realized")); Assert.Equal("700.00", Text(control, "cards", "realized")); Assert.Equal("1100.00", Text(control, "total", "projected"));
        Assert.Equal(Text(control, "total", "projected"), Text(await PfControlAsync("all"), "total", "projected"));
        // O seletor da Visão Geral não ganhou as contas inativas.
        var overview = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/overview?competence={Month}&scope=all", _session)).Content.ReadAsStringAsync()).RootElement;
        Assert.DoesNotContain(overview.GetProperty("accounts").GetProperty("items").EnumerateArray(), a => a.GetProperty("name").GetString() is "PF antiga" or "PJ antiga");
    }

    private async Task<Guid> CardPaidByAsync(string name, Guid? payer) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards", _session,
        new { name, issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = payer }))).Id;

    [Fact]
    public async Task BudgetCardsDetailReconcilesWithThePfBudgetAndKeepsPjAndUnassignedCardsOut()
    {
        await ArrangeAsync("bc-cartoes");
        var pfOld = await NewAccountAsync("PF antiga", "PF"); var pj = await NewAccountAsync("Itaú PJ", "PJ");
        var big = await CardPaidByAsync("Cartão grande", _account); var old = await CardPaidByAsync("Cartão da antiga", pfOld);
        var pjCard = await CardPaidByAsync("Cartão PJ", pj); var unpaid = await CardPaidByAsync("Sem pagador", null);
        await PurchaseAsync(big, "900.00"); await PurchaseAsync(old, "300.00"); await PurchaseAsync(pjCard, "5000.00"); await PurchaseAsync(unpaid, "400.00");
        await using (var db = Db()) await db.FinancialAccounts.Where(a => a.Id == pfOld).ExecuteUpdateAsync(set => set.SetProperty(a => a.IsActive, false));
        await ConfigureBudgetAsync("5000.00", 12500m);

        var response = await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/budget-cards?competence={Month}", _session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Só os cartões das contas PF (inclusive a inativa), do maior valor para o menor; o cartão PJ e o sem pagador ficam fora.
        Assert.Equal(["Cartão grande", "Cartão da antiga"], detail.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("name").GetString()!));
        Assert.Equal(["900.00", "300.00"], detail.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("projected").GetString()!));
        Assert.All(detail.GetProperty("cards").EnumerateArray(), c => { Assert.NotEqual("Closed", c.GetProperty("invoiceState").GetString()); Assert.Equal("Unpaid", c.GetProperty("paymentState").GetString()); });
        Assert.Contains("MissingPayerAccount", detail.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        // Concilia com o orçamento PF: o total do detalhamento é o mesmo de pfBudgetControl.cards.projected, sem nada contado duas vezes.
        var pf = await PfControlAsync("account", _account);
        Assert.Equal(Text(pf, "cards", "projected"), detail.GetProperty("total").GetString()); Assert.Equal(detail.GetProperty("total").GetString(), detail.GetProperty("official").GetString());
        Assert.DoesNotContain("UnexplainedDifference", detail.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("1200.00", Text(detail, "allocated")); Assert.Equal("Complete", Text(detail, "state"));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/budget-cards?competence=2026-13", _session)).StatusCode);
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transactions;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Caixa × receita × consumo entre Contas (<c>accounts/flows</c>), Agenda (<c>financial-calendar</c>), Visão Geral (<c>overview</c>) e Plano × Realizado, no mesmo recorte e com os mesmos fatos:
/// receita, despesa, devolução confirmada, transferência PF→PJ, compra no cartão e pagamento da fatura.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CashConceptsAlignmentTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month => ThisMonth.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _pf, _pj;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task<Guid> PostAsync(string path, object body)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/{path}", _session, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{path}: {await response.Content.ReadAsStringAsync()}");
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Root}/{path}", _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>PF: receita 3000, despesa 1000, devolução 100 dessa despesa, compra no cartão 300 paga pela PF (fatura paga 300) e transferência 50 para a PJ.</summary>
    private async Task ArrangeAsync(string user)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        var opening = ThisMonth.AddMonths(-1);
        _pf = await PostAsync("accounts", new { name = "Conta PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "5000.00", openingBalanceDate = opening });
        _pj = await PostAsync("accounts", new { name = "Conta PJ", type = "Checking", financialScope = "PJ", openingBalance = "5000.00", openingBalanceDate = opening });
        var salary = await PostAsync("categories", new { name = "Salário", type = "Income" });
        var rent = await PostAsync("categories", new { name = "Aluguel", type = "Expense" });
        await PostAsync("transactions", new { accountId = _pf, categoryId = salary, type = "Income", description = "Salário", amount = "3000.00", transactionDate = Today });
        var rentPaid = await PostAsync("transactions", new { accountId = _pf, categoryId = rent, type = "Expense", description = "Aluguel", amount = "1000.00", transactionDate = Today });
        var card = await PostAsync("credit-cards", new { name = "Nubank", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _pf });
        await PostAsync($"credit-cards/{card}/purchases", new
        {
            description = "Mercado", purchaseDate = ThisMonth, totalAmount = "300.00", installmentCount = 1, categoryId = rent, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth, expenseNature = (string?)null,
        });
        await PostAsync("transfers", new { sourceAccountId = _pf, destinationAccountId = _pj, amount = 50m, transferDate = Today, description = "Entre contas" });
        await using var db = Db();
        var owner = _workspace.OwnerUserId; var now = DateTimeOffset.UtcNow;
        db.FinancialTransactions.Add(FinancialTransaction.CreateInvoicePayment(_workspace.Id, _pf, "Pagamento da fatura", 300m, Today, owner, now));
        var refund = FinancialTransaction.CreateBankRefund(_workspace.Id, _pf, "Devolução do aluguel", 100m, Today, owner, now);
        db.FinancialTransactions.Add(refund);
        db.FinancialBankRefunds.Add(FinancialBankRefund.Create(_workspace.Id, refund.Id, rentPaid, 100m, owner, now));
        await db.SaveChangesAsync();
    }

    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private async Task<JsonElement> FlowsOfAsync(Guid account) =>
        (await GetAsync($"accounts/flows?competence={Month}")).GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == account);

    private async Task<JsonElement> AgendaAsync(Guid? account)
    {
        var from = ThisMonth.ToString("yyyy-MM-dd"); var through = ThisMonth.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
        return (await GetAsync($"financial-calendar?from={from}&through={through}" + (account is null ? "" : $"&accountId={account}"))).GetProperty("summary");
    }

    private async Task<JsonElement> OverviewAsync(string scope, Guid? account = null) =>
        await GetAsync($"financial-dashboard/overview?competence={Month}&scope={scope}" + (account is null ? "" : $"&accountId={account}"));

    [Fact]
    public async Task ARefundIsCashOnceSeparateFromRevenueAndReducesTheConsumptionInEveryScreen()
    {
        await ArrangeAsync("conceitos-devolucao");
        var contas = await FlowsOfAsync(_pf); var agenda = await AgendaAsync(_pf);
        var overview = await OverviewAsync("account", _pf); var transfers = overview.GetProperty("transfers");

        // Caixa: o crédito da devolução entra uma única vez (3000 de receita + 100 de devolução), igual nas três telas.
        Assert.Equal(["3100.00", "1350.00"], [S(contas, "totalInflows"), S(contas, "totalOutflows")]);
        Assert.Equal(["3100.00", "1350.00"], [S(agenda, "totalInflows"), S(agenda, "totalOutflows")]);
        Assert.Equal(["3100.00", "1350.00"], [S(transfers, "totalInflows"), S(transfers, "totalOutflows")]);
        // Contrato preservado (income continua incluindo a devolução) e campos aditivos com a composição.
        Assert.Equal("3100.00", S(contas, "income"));
        Assert.Equal(["3000.00", "100.00"], [S(contas, "revenue"), S(contas, "refunds")]);
        // Receita: a devolução fica fora dela em Contas (revenue), Agenda, Visão Geral e Plano × Realizado.
        var plan = await GetAsync($"financial-dashboard/plan-actual?competence={Month}&scope=account&accountId={_pf}");
        Assert.Equal(["3000.00", "3000.00", "3000.00", "3000.00"], [S(contas, "revenue"), S(agenda, "realizedIncome"), S(transfers, "externalIncome"), S(plan.GetProperty("income"), "realized")]);
        Assert.Equal(["100.00", "100.00"], [S(agenda, "realizedRefunds"), S(transfers, "refunds")]);
        // Consumo: 1000 de aluguel − 100 devolvidos + 300 da compra no cartão; o pagamento da fatura (300) é saída de caixa e não gera novo consumo.
        var consumption = S(plan.GetProperty("expenses").GetProperty("consumption"), "realized");
        Assert.Equal(["1200.00", "1200.00", "1200.00"], [consumption, S(agenda, "realizedExpense"), S(transfers, "externalExpenses")]);
        Assert.Equal("1300.00", S(contas, "expense")); // débitos de caixa: 1000 + 300 da fatura
        Assert.Equal("1300.00", S(transfers, "cashExpenses"));
        // Saldo: 5000 + 3100 − 1300 − 50, contado uma vez.
        Assert.Equal("6750.00", S(transfers, "available"));
    }

    [Fact]
    public async Task TransfersAppearOnBothAccountsAndNetToZeroInTheConsolidatedForEveryScopeCombination()
    {
        _session = await SignInAsync("conceitos-transferencias");
        _workspace = await CreateWorkspaceAsync(_session);
        var opening = ThisMonth.AddMonths(-1);
        async Task<Guid> Account(string name, string scope, bool primary = false) => await PostAsync("accounts", new { name, type = "Checking", financialScope = scope, isPrimary = primary, openingBalance = "1000.00", openingBalanceDate = opening });
        var (pf1, pf2, pj1, pj2) = (await Account("PF 1", "PF", true), await Account("PF 2", "PF"), await Account("PJ 1", "PJ"), await Account("PJ 2", "PJ"));
        async Task Move(Guid from, Guid to, decimal amount) => await PostAsync("transfers", new { sourceAccountId = from, destinationAccountId = to, amount, transferDate = Today, description = "Entre contas" });
        await Move(pf1, pf2, 10m); await Move(pj1, pj2, 20m); await Move(pf1, pj1, 30m); // PF↔PF, PJ↔PJ e PF↔PJ

        (decimal In, decimal Out) Pair(JsonElement f) => (decimal.Parse(S(f, "transfersReceived"), System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(S(f, "transfersSent"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((0m, 40m), Pair(await FlowsOfAsync(pf1))); Assert.Equal((10m, 0m), Pair(await FlowsOfAsync(pf2)));
        Assert.Equal((30m, 20m), Pair(await FlowsOfAsync(pj1))); Assert.Equal((20m, 0m), Pair(await FlowsOfAsync(pj2)));
        foreach (var (account, received, sent) in new[] { (pf1, "0.00", "40.00"), (pf2, "10.00", "0.00"), (pj1, "30.00", "20.00"), (pj2, "20.00", "0.00") })
        {
            var agenda = await AgendaAsync(account);
            Assert.Equal([received, sent], [S(agenda, "transfersReceived"), S(agenda, "transfersSent")]);
            var transfers = (await OverviewAsync("account", account)).GetProperty("transfers");
            Assert.Equal([received, sent], [S(transfers, "received"), S(transfers, "sent")]);
        }
        // Consolidado com as quatro contas: as três transferências são internas, o efeito líquido é zero e nada vira receita ou consumo.
        var all = await AgendaAsync(null);
        Assert.Equal(["0.00", "0.00", "60.00"], [S(all, "totalInflows"), S(all, "totalOutflows"), S(all, "transfersInternal")]);
        var consolidated = (await OverviewAsync("all")).GetProperty("transfers");
        Assert.Equal(["0.00", "0.00", "0.00", "0.00"], [S(consolidated, "totalInflows"), S(consolidated, "totalOutflows"), S(consolidated, "externalIncome"), S(consolidated, "externalExpenses")]);
        Assert.Equal("0.00", S(consolidated, "net"));
    }

    [Fact]
    public async Task TheBudgetControlOnlyReportsASourceTimeWhenThereIsAnIntegrationNeverInventingOne()
    {
        await ArrangeAsync("conceitos-origem");
        var overview = await OverviewAsync("all");
        // Sem integração bancária nas contas, o horário da origem é nulo (o front mostra "Atualização da origem indisponível").
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("pfBudgetControl").GetProperty("dataUpdatedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("decision").GetProperty("context").GetProperty("lastSyncedAt").ValueKind);
        // Contas manuais confirmadas: duas contas no consolidado, uma PF no orçamento PF, nenhuma integrada.
        var decision = overview.GetProperty("decision").GetProperty("context").GetProperty("source");
        Assert.Equal([2, 0, 0], [decision.GetProperty("accounts").GetInt32(), decision.GetProperty("integrated").GetInt32(), decision.GetProperty("synced").GetInt32()]);
        var pf = overview.GetProperty("pfBudgetControl").GetProperty("source");
        Assert.Equal([1, 0, 0], [pf.GetProperty("accounts").GetInt32(), pf.GetProperty("integrated").GetInt32(), pf.GetProperty("synced").GetInt32()]);
        Assert.Equal(JsonValueKind.Null, pf.GetProperty("oldestSyncAt").ValueKind);
    }
}

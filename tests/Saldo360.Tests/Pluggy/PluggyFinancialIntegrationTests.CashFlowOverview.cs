using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// Fluxo de caixa da Visão Geral: ponte de saldo (abertura + entradas - saídas = fechamento) por conta e no consolidado, sem saldo inicial nunca zero, conciliação contra o saldo
/// bancário, devoluções/cartão/fatura sem duplicar, transferência prevista fora dos realizados e igualdade com Contas e Agenda. Cliente Pluggy simulado, todas as datas relativas
/// (movimentos no mesmo dia, sem depender de existir um dia anterior no mês corrente).
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private async Task SetOpeningAsync(Ctx c, Guid account, string name, string scope, decimal? amount, bool active = true)
    {
        var edit = await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/accounts/{account}", c.Session,
            new { name, type = "Checking", financialScope = scope, isActive = active, openingBalance = amount, openingBalanceDate = amount is null ? null : D(60) });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
    }

    private async Task<JsonElement> CashFlowAsync(Ctx c, string scope, Guid? account = null, string? competence = null)
    {
        if (competence is null) return (await TransferOverviewAsync(c, scope, account)).GetProperty("cashFlow");
        var url = $"/api/financial-workspaces/{c.Workspace}/financial-dashboard/overview?competence={competence}&scope={scope}" + (account is null ? "" : $"&accountId={account}");
        return (await JsonGetAsync(c, url)).GetProperty("cashFlow");
    }

    private static decimal Dec(JsonElement element, params string[] path) => decimal.Parse(Text(element, path), CultureInfo.InvariantCulture);

    /// <summary>Todos os movimentos no mesmo dia (D-3): PF recebe a transferência e gasta 40; PJ envia a transferência e recebe 100 de cliente.</summary>
    private async Task<Pair> ArrangeCashPairAsync(string user, decimal amount, params PluggyTransaction[] extraBusiness)
    {
        var p = await ArrangePairAsync(user,
            [Tx("pf-in", BankA, 3, amount, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX"), Tx("pf-expense", BankA, 3, -40m, operation: "PIX", description: "Padaria", category: "Groceries")],
            [Tx("pj-out", BankB, 3, -amount, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX"), Tx("pj-in", BankB, 3, 100m, operation: "TED", description: "Cliente", category: "Salary"), .. extraBusiness]);
        await SyncBothAsync(p);
        Assert.Equal(1, await ActiveTransfersAsync());
        return p;
    }

    private async Task AssertBridgeClosesAsync(Ctx c, JsonElement flow, Guid? account)
    {
        var opening = Dec(flow, "opening"); var inflows = Dec(flow, "inflows", "total"); var outflows = Dec(flow, "outflows", "total"); var closing = Dec(flow, "closing");
        Assert.Equal(opening + inflows - outflows, closing);
        Assert.True(flow.GetProperty("identityHolds").GetBoolean());
        Assert.True(flow.GetProperty("snapshotConsistent").GetBoolean());
        Assert.Equal(inflows, Dec(flow, "inflows", "credits") + Dec(flow, "inflows", "transfersReceived"));
        Assert.Equal(outflows, Dec(flow, "outflows", "debits") + Dec(flow, "outflows", "transfersSent"));
        if (account is { } id) // fechamento independente: saldo realizado da conta no último dia (limitado a hoje)
        {
            var last = DateOnly.Parse(flow.GetProperty("through").GetString()!, CultureInfo.InvariantCulture);
            var asOf = last > Today ? Today : last;
            var balance = decimal.Parse((await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{id}/balance?asOf={asOf:yyyy-MM-dd}")).GetProperty("balance").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal(balance, closing);
        }
    }

    [Theory]
    [InlineData("200.00")]
    [InlineData("12692.00")]
    public async Task TheCashFlowBridgeClosesForPfPjAndTheConsolidatedAndMatchesAccountsAndTheAgenda(string amount)
    {
        var value = decimal.Parse(amount, Inv);
        var p = await ArrangeCashPairAsync("t-cashflow-" + amount, value);
        await SetOpeningAsync(p.C, p.PersonalAccount, "Itaú PF", "PF", 1000m);
        await SetOpeningAsync(p.C, p.BusinessAccount, "Itaú PJ", "PJ", 5000m);

        var pf = await CashFlowAsync(p.C, "account", p.PersonalAccount);
        Assert.Equal(("Complete", "1000.00", amount, "0.00", amount, "40.00", "0.00"), (Text(pf, "state"), Text(pf, "opening"), Text(pf, "inflows", "total"), Text(pf, "inflows", "credits"), Text(pf, "inflows", "transfersReceived"), Text(pf, "outflows", "total"), Text(pf, "outflows", "transfersSent")));
        Assert.Equal((1000m + value - 40m), Dec(pf, "closing"));
        await AssertBridgeClosesAsync(p.C, pf, p.PersonalAccount);
        var pfTransfer = pf.GetProperty("transfers").EnumerateArray().Single();
        Assert.Equal(("Inbound", amount, "Itaú PJ", "Itaú PF"), (Text(pfTransfer, "relation"), Text(pfTransfer, "effect"), Text(pfTransfer, "source"), Text(pfTransfer, "destination")));

        var pj = await CashFlowAsync(p.C, "account", p.BusinessAccount);
        Assert.Equal(("5000.00", "100.00", amount, amount), (Text(pj, "opening"), Text(pj, "inflows", "total"), Text(pj, "outflows", "total"), Text(pj, "outflows", "transfersSent")));
        await AssertBridgeClosesAsync(p.C, pj, p.BusinessAccount);
        Assert.Equal("Outbound", Text(pj.GetProperty("transfers")[0], "relation"));

        // Consolidado PF+PJ: a transferência é interna e neutralizada; o efeito líquido é zero e o fechamento é a soma dos dois.
        var all = await CashFlowAsync(p.C, "all");
        Assert.Equal(("6000.00", "100.00", "40.00", "0.00", "0.00", amount), (Text(all, "opening"), Text(all, "inflows", "total"), Text(all, "outflows", "total"), Text(all, "inflows", "transfersReceived"), Text(all, "outflows", "transfersSent"), Text(all, "internalMoved")));
        Assert.Equal("Internal", Text(all.GetProperty("transfers")[0], "relation"));
        Assert.Equal("0.00", Text(all.GetProperty("transfers")[0], "effect"));
        await AssertBridgeClosesAsync(p.C, all, null);
        Assert.Equal(Dec(pf, "closing") + Dec(pj, "closing"), Dec(all, "closing"));

        // Visão Geral = Contas = Agenda para o mesmo escopo e período.
        var agendaPf = await AgendaTotalsAsync(p.C, p.PersonalAccount); var agendaPj = await AgendaTotalsAsync(p.C, p.BusinessAccount); var agendaAll = await AgendaTotalsAsync(p.C, null);
        Assert.Equal((agendaPf.Inflows, agendaPf.Outflows), (Text(pf, "inflows", "total"), Text(pf, "outflows", "total")));
        Assert.Equal((agendaPj.Inflows, agendaPj.Outflows), (Text(pj, "inflows", "total"), Text(pj, "outflows", "total")));
        Assert.Equal((agendaAll.Inflows, agendaAll.Outflows), (Text(all, "inflows", "total"), Text(all, "outflows", "total")));
        Assert.Equal(await AccountsTotalsAsync(p.C, p.PersonalAccount), (Text(pf, "inflows", "total"), Text(pf, "outflows", "total")));
        Assert.Equal(await AccountsTotalsAsync(p.C, p.BusinessAccount), (Text(pj, "inflows", "total"), Text(pj, "outflows", "total")));
    }

    [Fact]
    public async Task WithoutAReliableOpeningBalanceTheBridgeIsNotReconcilableAndNeverAssumesZero()
    {
        var p = await ArrangeCashPairAsync("t-cashflow-noopening", 200m);
        // Remove o saldo inicial efetivo da PF e da PJ: sem referência manual e sem âncora do banco aplicável, a abertura não existe.
        await using (var db = Db())
        {
            await db.FinancialAccountBalanceAnchors.ExecuteDeleteAsync();
            await db.FinancialAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.OpeningBalance, (decimal?)null).SetProperty(a => a.OpeningBalanceDate, (DateOnly?)null));
        }

        var all = await CashFlowAsync(p.C, "all");
        Assert.Equal("Partial", Text(all, "state"));
        Assert.Equal(JsonValueKind.Null, all.GetProperty("opening").ValueKind);
        Assert.Equal(JsonValueKind.Null, all.GetProperty("closing").ValueKind);
        Assert.Equal(JsonValueKind.Null, all.GetProperty("identityHolds").ValueKind);
        var missing = all.GetProperty("openingMissing").EnumerateArray().Select(m => (Text(m, "name"), Text(m, "reason"))).OrderBy(m => m.Item1).ToList();
        Assert.Equal([("Itaú PF", "OpeningBalanceNotConfigured"), ("Itaú PJ", "OpeningBalanceNotConfigured")], missing);
        // O fluxo do período continua exibido; só a conciliação deixa de ser possível.
        Assert.Equal(("100.00", "40.00"), (Text(all, "inflows", "total"), Text(all, "outflows", "total")));
        var pf = await CashFlowAsync(p.C, "account", p.PersonalAccount);
        Assert.Equal((JsonValueKind.Null, "200.00"), (pf.GetProperty("closing").ValueKind, Text(pf, "inflows", "total")));
        Assert.Equal("NotVerifiable", Text(pf, "reconciliation", "status"));
    }

    [Fact]
    public async Task AnInactiveAccountWithMovementStaysInTheConsolidatedBridge()
    {
        var p = await ArrangeCashPairAsync("t-cashflow-inactive", 12692m);
        await SetOpeningAsync(p.C, p.PersonalAccount, "Itaú PF", "PF", 1000m);
        await SetOpeningAsync(p.C, p.BusinessAccount, "Itaú PJ", "PJ", 5000m, active: false);

        var all = await CashFlowAsync(p.C, "all");
        Assert.Equal(("6000.00", "100.00", "40.00", "12692.00"), (Text(all, "opening"), Text(all, "inflows", "total"), Text(all, "outflows", "total"), Text(all, "internalMoved")));
        await AssertBridgeClosesAsync(p.C, all, null);
        var pj = await CashFlowAsync(p.C, "account", p.BusinessAccount);
        Assert.Equal(("5000.00", "100.00", "12692.00"), (Text(pj, "opening"), Text(pj, "inflows", "total"), Text(pj, "outflows", "total")));
        await AssertBridgeClosesAsync(p.C, pj, p.BusinessAccount);
    }

    [Fact]
    public async Task AnUncategorizedCreditStaysInTheCashInflows()
    {
        var p = await ArrangeCashPairAsync("t-cashflow-uncategorized", 200m, Tx("pj-unc", BankB, 3, 50m, operation: "TED", description: "Credito sem categoria", category: null));
        await SetOpeningAsync(p.C, p.PersonalAccount, "Itaú PF", "PF", 1000m);
        await SetOpeningAsync(p.C, p.BusinessAccount, "Itaú PJ", "PJ", 5000m);

        var pj = await CashFlowAsync(p.C, "account", p.BusinessAccount);
        var credited = (await CoreAsync(p.C.Workspace)).Where(t => t.AccountId == p.BusinessAccount && t.Amount > 0).Sum(t => t.Amount); // lançamentos efetivos da PJ, com ou sem categoria
        Assert.Equal(credited.ToString("0.00", Inv), Text(pj, "inflows", "credits"));
        await AssertBridgeClosesAsync(p.C, pj, p.BusinessAccount);
    }

    [Fact]
    public async Task APlannedTransferNeverEntersTheRealizedCashTotalsOfTheBridge()
    {
        var p = await ArrangePlannedAsync("t-cashflow-planned",
            [Tx("pf-in", BankA, 3, 200m, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX")],
            [Tx("pj-out", BankB, 3, -200m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")]);
        await SyncBothAsync(p);
        await SetOpeningAsync(p.C, p.PersonalAccount, "Itaú PF", "PF", 1000m);
        await SetOpeningAsync(p.C, p.BusinessAccount, "Itaú PJ", "PJ", 5000m);
        var before = (await CashFlowAsync(p.C, "account", p.PersonalAccount)).GetRawText();
        var beforePj = (await CashFlowAsync(p.C, "account", p.BusinessAccount)).GetRawText();

        await CreatePlannedAsync(p, p.BusinessAccount, p.PersonalAccount, 500m, 5);

        Assert.Equal(before, (await CashFlowAsync(p.C, "account", p.PersonalAccount)).GetRawText());
        Assert.Equal(beforePj, (await CashFlowAsync(p.C, "account", p.BusinessAccount)).GetRawText());
    }

    [Fact]
    public async Task ACardPurchaseIsNotABankOutflowAndThePaidBillIsCountedOnceAsADebitOfTheAccount()
    {
        var c = await ArrangeAsync("t-cashflow-card", seed: false);
        var out10 = PreviousMonthStart.AddDays(10);
        Pluggy.TransactionsByAccount[BankA] =
        [
            BankTx("seed", PreviousMonthStart.AddDays(2), 3000m, "SALARIO"),
            BankTx("own-out", out10, -500m) with { Description = "PIX ENVIADO", DescriptionRaw = "PIX ENVIADO", Category = "Transfer - PIX" },
            Tx("pay-bank", BankA, DaysAgo(PreviousMonthStart.AddDays(15)), -230m, operation: "PIX", description: "PAGAMENTO DE FATURA", category: null),
        ];
        Pluggy.BillsByAccount[CardA] = [TotalBill("bill-prev", PreviousMonthStart.AddDays(ClosingDay - 1), 230m)];
        Pluggy.TransactionsByAccount[CardA] = [CardTx("pay-card", "PAGAMENTO FATURA", 230m, PreviousMonthStart.AddDays(15), "") with { Type = "CREDIT", OperationType = "PAGAMENTO_FATURA", Category = null },
            CardTx("buy", "LOJA", 230m, PreviousMonthStart.AddDays(5), "bill-prev")];
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, 2270m), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var a = (await LinkBankAsync(c, extra: new { financialScope = "PF" })).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await LinkCardAsync(c, ClosingDay, DueDay);
        await SyncAsync(c);
        // Saldo inicial manual anterior ao mês: a abertura do mês passado passa a existir (o manual prevalece sobre a âncora).
        var edit = await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/accounts/{a}", c.Session,
            new { name = "Itaú PF", type = "Checking", financialScope = "PF", isActive = true, openingBalance = 100m, openingBalanceDate = PreviousMonthStart.AddDays(-5).ToString("yyyy-MM-dd") });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var flow = await CashFlowAsync(c, "account", a, $"{PreviousMonthStart:yyyy-MM}");

        // Banco: +3000 (crédito), -230 (pagamento da fatura) e -500 (Pix sem contrapartida: débito comum, não há outra conta para formar transferência). A compra de 230 do cartão NÃO é saída bancária e o pagamento só conta uma vez.
        Assert.Equal(("100.00", "3000.00", "730.00", "0.00", "730.00", "2370.00"),
            (Text(flow, "opening"), Text(flow, "inflows", "total"), Text(flow, "outflows", "debits"), Text(flow, "outflows", "transfersSent"), Text(flow, "outflows", "total"), Text(flow, "closing")));
        await AssertBridgeClosesAsync(c, flow, a);
    }

    [Fact]
    public async Task TheBankPositionAndReconciliationAreReportedWithoutAdjustingAnything()
    {
        var (c, account) = await ArrangeCleanHistoryAsync("t-cashflow-reconcile");
        var competence = $"{MonthStart:yyyy-MM}";

        var flow = await CashFlowAsync(c, "account", account, competence);
        Assert.Equal(("Complete", "4900.00", "100.00", "0.00", "5000.00"), (Text(flow, "state"), Text(flow, "opening"), Text(flow, "inflows", "total"), Text(flow, "outflows", "total"), Text(flow, "closing")));
        Assert.Equal(("Complete", "5000.00"), (Text(flow, "bankPosition", "state"), Text(flow, "bankPosition", "total")));
        Assert.NotEqual(JsonValueKind.Null, flow.GetProperty("bankPosition").GetProperty("observedAt").ValueKind);
        Assert.Equal(("Verified", "5000.00", "5000.00", "0.00"), (Text(flow, "reconciliation", "status"), Text(flow, "reconciliation", "bank"), Text(flow, "reconciliation", "calculated"), Text(flow, "reconciliation", "difference")));

        // Saldo inicial manual divergente: a diferença aparece (banco - calculado) e NADA é ajustado.
        await SetOpeningAsync(c, account, "Conta", "PF", 1000m);
        await using var db = Db();
        var transactions = await db.FinancialTransactions.CountAsync();
        var diverged = await CashFlowAsync(c, "account", account, competence);
        Assert.Equal("Divergent", Text(diverged, "reconciliation", "status"));
        Assert.Equal(Dec(diverged, "reconciliation", "bank") - Dec(diverged, "reconciliation", "calculated"), Dec(diverged, "reconciliation", "difference"));
        Assert.NotEqual(0m, Dec(diverged, "reconciliation", "difference"));
        Assert.Equal(transactions, await db.FinancialTransactions.CountAsync());
    }
}

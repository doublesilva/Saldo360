using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// Regra 1 e 6: a transferencia nunca some do extrato por causa de sincronizacao/reprocessamento, aparece na Agenda (itens e resumo, sempre separada de receitas/despesas)
/// e o consolidado continua neutralizado mesmo quando uma das contas esta inativa.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private async Task<JsonElement> CalendarAsync(Ctx c, Guid? account, bool includeTransfers = true)
    {
        var url = $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={D(10)}&through={D(0)}" + (account is null ? "" : $"&accountId={account}") + (includeTransfers ? "&includeTransfers=true" : "");
        return await JsonGetAsync(c, url);
    }

    [Fact]
    public async Task BothTransferEndsStayQueryableAfterRepeatedSyncAndReprocessWithoutDuplicatesAndTheAgendaListsThemApartFromIncomeAndExpense()
    {
        var p = await ArrangePairAsync("t-statement-stable", [Tx("pf-in", BankA, 3, 200m, operation: "PIX", description: "Pix recebido")], [Tx("pj-out", BankB, 3, -200m, operation: "PIX", description: "Pix enviado")]);

        for (var round = 0; round < 3; round++)
        {
            await SyncBothAsync(p);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess")).StatusCode);
            var ends = await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/transactions?pageSize=100&includeTransfers=true&type=Transfer");
            Assert.Equal(2, ends.GetProperty("totalCount").GetInt32());
            Assert.Equal(["TransferIn", "TransferOut"], ends.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()!).Order().ToArray());
            Assert.Equal(1, await ActiveTransfersAsync());
            Assert.Empty(await CoreAsync(p.C.Workspace)); // nenhum receita/despesa duplicado por tras da transferencia
            Assert.Equal((200m, -200m), (await CashEffectAsync(p.PersonalAccount), await CashEffectAsync(p.BusinessAccount)));
        }

        // Agenda da PF: a ponta aparece como item (Source Transfer) e no resumo como recebida, sem tocar em receitas realizadas.
        var pf = await CalendarAsync(p.C, p.PersonalAccount);
        var item = pf.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("source").GetString() == "Transfer");
        Assert.Equal(("Income", "200.00"), (item.GetProperty("type").GetString(), item.GetProperty("amount").GetString()));
        Assert.StartsWith("Transferência recebida de Itaú PJ", item.GetProperty("description").GetString());
        var summary = pf.GetProperty("summary");
        Assert.Equal(("200.00", "0.00", "0.00", "0.00"), (summary.GetProperty("transfersReceived").GetString(), summary.GetProperty("transfersSent").GetString(), summary.GetProperty("realizedIncome").GetString(), summary.GetProperty("realizedExpense").GetString()));
        var pj = (await CalendarAsync(p.C, p.BusinessAccount)).GetProperty("summary");
        Assert.Equal(("0.00", "200.00"), (pj.GetProperty("transfersReceived").GetString(), pj.GetProperty("transfersSent").GetString()));
        // Consolidado: neutralizada (recebidas/enviadas 0, interna 200) e as duas pontas listadas; sem o opt-in, o contrato antigo nao tem itens de transferencia.
        var all = await CalendarAsync(p.C, null);
        Assert.Equal(("0.00", "0.00", "200.00"), (all.GetProperty("summary").GetProperty("transfersReceived").GetString(), all.GetProperty("summary").GetProperty("transfersSent").GetString(), all.GetProperty("summary").GetProperty("transfersInternal").GetString()));
        Assert.Equal(2, all.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("source").GetString() == "Transfer"));
        Assert.DoesNotContain(await CalendarAsync(p.C, null, includeTransfers: false) is var legacy ? legacy.GetProperty("items").EnumerateArray() : [], i => i.GetProperty("source").GetString() == "Transfer");
    }

    [Fact]
    public async Task AnInactiveAccountDoesNotBreakTheConsolidatedNeutralization()
    {
        var p = await ArrangePairAsync("t-statement-inactive", [Tx("pf-in", BankA, 3, 200m, operation: "PIX", description: "Pix recebido")], [Tx("pj-out", BankB, 3, -200m, operation: "PIX", description: "Pix enviado")]);
        await SyncBothAsync(p);
        await using (var db = Db()) await db.FinancialAccounts.Where(a => a.Id == p.BusinessAccount).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));

        var all = (await TransferOverviewAsync(p.C, "all")).GetProperty("transfers");

        Assert.Equal(("0.00", "0.00", "200.00"), (all.GetProperty("received").GetString(), all.GetProperty("sent").GetString(), all.GetProperty("internalMoved").GetString()));
    }

    [Fact]
    public async Task TheAccountsFlowsEndpointShowsTwoHundredAsPersonalInflowAndBusinessOutflowWithNoConsolidatedChange()
    {
        var p = await ArrangePairAsync("t-accounts-flows", [Tx("pf-in", BankA, 3, 200m, operation: "PIX", description: "Pix recebido")], [Tx("pj-out", BankB, 3, -200m, operation: "PIX", description: "Pix enviado")]);
        await SyncBothAsync(p);

        var flows = await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/accounts/flows?competence={Today.AddDays(-3):yyyy-MM}");

        var accounts = flows.GetProperty("accounts").EnumerateArray().ToDictionary(a => a.GetProperty("accountId").GetGuid());
        var pf = accounts[p.PersonalAccount]; var pj = accounts[p.BusinessAccount];
        Assert.Equal(("0.00", "200.00", "200.00", "0.00"), (pf.GetProperty("income").GetString(), pf.GetProperty("transfersReceived").GetString(), pf.GetProperty("totalInflows").GetString(), pf.GetProperty("totalOutflows").GetString()));
        Assert.Equal(("0.00", "200.00", "200.00", "0.00"), (pj.GetProperty("expense").GetString(), pj.GetProperty("transfersSent").GetString(), pj.GetProperty("totalOutflows").GetString(), pj.GetProperty("totalInflows").GetString()));
        Assert.Equal(("0.00", "0.00", "200.00"), (flows.GetProperty("consolidatedIncome").GetString(), flows.GetProperty("consolidatedExpense").GetString(), flows.GetProperty("internalTransfers").GetString()));
        Assert.Equal((200m, -200m), (await CashEffectAsync(p.PersonalAccount), await CashEffectAsync(p.BusinessAccount))); // saldos: +200 na PF e -200 na PJ
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/accounts/flows?competence=2026-13")).StatusCode);
    }

    [Fact]
    public async Task RefundsAreCashCreditsInTheOverviewTotalsButNeverRevenueAndTheAccountsAndOverviewCashTotalsAgree()
    {
        var (p, account) = await ArrangeRefundAsync("t-cash-totals", 40, 55.99m, ("back", 2, 55.99m, MarketplaceBack));
        var originalId = await TransactionIdOfAsync("pay");
        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(p.C, "confirm", await PostingIdAsync("back"), originalId)).StatusCode);
        var month = Today.AddDays(-2).ToString("yyyy-MM");

        var overview = (await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/financial-dashboard/overview?competence={month}&scope=account&accountId={account}")).GetProperty("transfers");
        var flows = (await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/accounts/flows?competence={month}")).GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == account);

        Assert.Equal("0.00", overview.GetProperty("externalIncome").GetString()); // devolucao nao e receita
        Assert.Equal("55.99", overview.GetProperty("refunds").GetString());
        Assert.Equal(flows.GetProperty("totalInflows").GetString(), overview.GetProperty("totalInflows").GetString()); // mesma definicao de caixa nas duas telas
        Assert.Equal(flows.GetProperty("totalOutflows").GetString(), overview.GetProperty("totalOutflows").GetString());
        Assert.Equal("55.99", overview.GetProperty("totalInflows").GetString());
    }
}

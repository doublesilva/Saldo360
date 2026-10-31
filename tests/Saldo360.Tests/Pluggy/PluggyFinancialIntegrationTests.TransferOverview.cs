using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// Credito da PF ja lancado como receita + sincronizacao posterior da PJ com debito de mesmo valor e dia: vira transferencia (nao e receita), o efeito de caixa da PF
/// nao muda, o lancamento anterior fica cancelado e preservado, e a Visao Geral mostra a entrada por transferencia a parte, sem somar em Receitas.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private async Task<decimal> CashEffectAsync(Guid account)
    {
        await using var db = Db();
        var income = await db.FinancialTransactions.Where(t => t.AccountId == account && !t.IsCancelled && t.Type == CategoryType.Income).SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var expense = await db.FinancialTransactions.Where(t => t.AccountId == account && !t.IsCancelled && t.Type == CategoryType.Expense).SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var inbound = await db.FinancialTransfers.Where(t => t.DestinationAccountId == account && !t.IsCancelled).SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var outbound = await db.FinancialTransfers.Where(t => t.SourceAccountId == account && !t.IsCancelled).SumAsync(t => (decimal?)t.Amount) ?? 0m;
        return income - expense + inbound - outbound;
    }

    private async Task<JsonElement> TransferOverviewAsync(Ctx c, string scope, Guid? account = null)
    {
        var competence = Today.AddDays(-3).ToString("yyyy-MM");
        var url = $"/api/financial-workspaces/{c.Workspace}/financial-dashboard/overview?competence={competence}&scope={scope}" + (account is null ? "" : $"&accountId={account}");
        var response = await GetAsync(c, url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {await response.Content.ReadAsStringAsync()}");
        return await JsonOf(response);
    }

    [Fact]
    public async Task ABusinessDebitSyncedAfterAnAlreadyBookedPersonalCreditTurnsItIntoATransferWithoutChangingThePersonalCashAndTheOverviewShowsItSeparately()
    {
        var p = await ArrangePairAsync("t-transfer-overview", [Tx("pf-in", BankA, 3, 12692m, operation: "PIX", description: "Pix recebido EMPRESA TECNOLOGIA", category: "Salary")], []);
        await SyncAsync(p.C); // so a PF: o credito vira receita
        Assert.Equal(12692m, await CashEffectAsync(p.PersonalAccount));
        Assert.Equal((12692m, 0m), await RealizedAsync(p.C));

        Pluggy.TransactionsByAccount[BankB] = [Tx("pj-out", BankB, 3, -12692m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")];
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.C, $"{Root(p.C.Workspace)}/connections/{p.ItemBConnection}/sync")).StatusCode);

        // O efeito de caixa da PF e o mesmo; o credito deixou de ser receita (cancelado e preservado) e virou entrada por transferencia.
        Assert.Equal(12692m, await CashEffectAsync(p.PersonalAccount));
        Assert.Equal(-12692m, await CashEffectAsync(p.BusinessAccount));
        Assert.Equal(1, await ActiveTransfersAsync());
        var all = await CoreAsync(p.C.Workspace, includeCancelled: true);
        Assert.Contains(all, t => t.AccountId == p.PersonalAccount && t.Amount == 12692m && t.IsCancelled);
        Assert.DoesNotContain(await CoreAsync(p.C.Workspace), t => t.AccountId == p.PersonalAccount);
        Assert.Equal((0m, 0m), await RealizedAsync(p.C));

        // Visao Geral: PF mostra recebido (de Itau PJ) a parte; a PJ mostra enviado; consolidado: movimentado e liquido zero.
        var pf = (await TransferOverviewAsync(p.C, "account", p.PersonalAccount)).GetProperty("transfers");
        Assert.Equal(("12692.00", "0.00", "12692.00", 1), (pf.GetProperty("received").GetString(), pf.GetProperty("sent").GetString(), pf.GetProperty("net").GetString(), pf.GetProperty("count").GetInt32()));
        var line = pf.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("Received", "Itaú PJ", "12692.00"), (line.GetProperty("direction").GetString(), line.GetProperty("counterpart").GetString(), line.GetProperty("amount").GetString()));
        var pj = (await TransferOverviewAsync(p.C, "account", p.BusinessAccount)).GetProperty("transfers");
        Assert.Equal(("0.00", "12692.00", "-12692.00"), (pj.GetProperty("received").GetString(), pj.GetProperty("sent").GetString(), pj.GetProperty("net").GetString()));
        var consolidated = (await TransferOverviewAsync(p.C, "all")).GetProperty("transfers");
        Assert.Equal(("0.00", "0.00", "0.00", "12692.00"), (consolidated.GetProperty("received").GetString(), consolidated.GetProperty("sent").GetString(), consolidated.GetProperty("net").GetString(), consolidated.GetProperty("internalMoved").GetString())); // neutralizada no conjunto PF+PJ
        Assert.Equal(("Between", "Itaú PJ", "Itaú PF"), (consolidated.GetProperty("items")[0].GetProperty("direction").GetString(), consolidated.GetProperty("items")[0].GetProperty("counterpart").GetString(), consolidated.GetProperty("items")[0].GetProperty("counterpart2").GetString()));

        // A decisao do usuario: "Desfazer vinculo" devolve o credito como receita da PF e o debito como despesa da PJ, e o par nao e recriado.
        var undo = await LinkActionAsync(p.C, "undo", await PostingIdAsync("pf-in"));
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(0, await ActiveTransfersAsync());
        Assert.Equal((12692m, 12692m), await RealizedAsync(p.C));
        await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess");
        Assert.Equal(0, await ActiveTransfersAsync());
        var after = (await TransferOverviewAsync(p.C, "account", p.PersonalAccount)).GetProperty("transfers");
        Assert.Equal(("0.00", 0), (after.GetProperty("received").GetString(), after.GetProperty("count").GetInt32()));
    }
}

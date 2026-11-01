using System.Net;
using System.Text.Json;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// Total de entradas/saidas de caixa realizadas da Agenda: mesma definicao de Contas e Visao Geral (receitas + devolucoes + transferencias que cruzam o conjunto),
/// transferencias internas neutralizadas no consolidado, contas inativas incluidas e transferencias previstas fora dos totais realizados.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private sealed record AgendaTotals(string Inflows, string Outflows, string Received, string Sent, string Internal, string PlannedReceived, string PlannedSent);

    private static (string From, string Through) MonthOfTransfers()
    {
        var first = new DateOnly(Today.AddDays(-3).Year, Today.AddDays(-3).Month, 1);
        return (first.ToString("yyyy-MM-dd"), first.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"));
    }

    private async Task<AgendaTotals> AgendaTotalsAsync(Ctx c, Guid? account, string? from = null, string? through = null)
    {
        var month = MonthOfTransfers();
        var summary = (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={from ?? month.From}&through={through ?? month.Through}" + (account is null ? "" : $"&accountId={account}"))).GetProperty("summary");
        string S(string name) => summary.GetProperty(name).GetString()!;
        return new(S("totalInflows"), S("totalOutflows"), S("transfersReceived"), S("transfersSent"), S("transfersInternal"), S("plannedTransfersReceived"), S("plannedTransfersSent"));
    }

    private async Task<(string Inflows, string Outflows)> AccountsTotalsAsync(Ctx c, Guid account)
    {
        var flows = (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/flows?competence={Today.AddDays(-3):yyyy-MM}")).GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == account);
        return (flows.GetProperty("totalInflows").GetString()!, flows.GetProperty("totalOutflows").GetString()!);
    }

    private async Task<(string Inflows, string Outflows)> OverviewTotalsAsync(Ctx c, string scope, Guid? account = null)
    {
        var transfers = (await TransferOverviewAsync(c, scope, account)).GetProperty("transfers");
        return (transfers.GetProperty("totalInflows").GetString()!, transfers.GetProperty("totalOutflows").GetString()!);
    }

    private async Task<Pair> ArrangeAgendaPairAsync(string user, decimal amount)
    {
        var p = await ArrangePairAsync(user,
            [Tx("pf-in", BankA, 3, amount, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX"), Tx("pf-expense", BankA, 2, -40m, operation: "PIX", description: "Padaria", category: "Groceries")],
            [Tx("pj-out", BankB, 3, -amount, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX"), Tx("pj-in", BankB, 2, 100m, operation: "TED", description: "Cliente", category: "Salary")]);
        await SyncBothAsync(p);
        Assert.Equal(1, await ActiveTransfersAsync());
        return p;
    }

    [Theory]
    [InlineData("200.00")]
    [InlineData("12692.00")]
    public async Task TheAgendaTotalsFollowTheTransferPerspectiveOfTheSourceTheDestinationAndTheConsolidatedAndMatchAccountsAndTheOverview(string amount)
    {
        var value = decimal.Parse(amount, Inv);
        var p = await ArrangeAgendaPairAsync("t-agenda-totals-" + amount, value);

        // Destino (PF): a transferencia recebida e ENTRADA, separada; despesa externa de 40 e a unica saida.
        var pf = await AgendaTotalsAsync(p.C, p.PersonalAccount);
        Assert.Equal((amount, "40.00", amount, "0.00"), (pf.Inflows, pf.Outflows, pf.Received, pf.Sent));
        // Origem (PJ): a transferencia enviada e SAIDA; a receita externa de 100 e a unica entrada.
        var pj = await AgendaTotalsAsync(p.C, p.BusinessAccount);
        Assert.Equal(("100.00", amount, "0.00", amount), (pj.Inflows, pj.Outflows, pj.Received, pj.Sent));
        // Consolidado PF+PJ: a transferencia e interna e neutralizada; so externos (100 de entrada, 40 de saida); a movimentacao interna continua informada.
        var all = await AgendaTotalsAsync(p.C, null);
        Assert.Equal(("100.00", "40.00", "0.00", "0.00", amount), (all.Inflows, all.Outflows, all.Received, all.Sent, all.Internal));

        // Agenda == Contas == Visao Geral para o mesmo periodo (mes) e escopo.
        Assert.Equal((pf.Inflows, pf.Outflows), await AccountsTotalsAsync(p.C, p.PersonalAccount));
        Assert.Equal((pj.Inflows, pj.Outflows), await AccountsTotalsAsync(p.C, p.BusinessAccount));
        Assert.Equal((pf.Inflows, pf.Outflows), await OverviewTotalsAsync(p.C, "account", p.PersonalAccount));
        Assert.Equal((pj.Inflows, pj.Outflows), await OverviewTotalsAsync(p.C, "account", p.BusinessAccount));
        Assert.Equal((all.Inflows, all.Outflows), await OverviewTotalsAsync(p.C, "all"));
        // Nada disso vira receita/despesa: os indicadores operacionais seguem sem a transferencia.
        var month = MonthOfTransfers();
        var summary = (await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/financial-calendar?from={month.From}&through={month.Through}&accountId={p.PersonalAccount}")).GetProperty("summary");
        Assert.Equal(("0.00", "40.00"), (summary.GetProperty("realizedIncome").GetString(), summary.GetProperty("realizedExpense").GetString()));
    }

    [Fact]
    public async Task AnInactiveAccountWithMovementStaysInTheConsolidatedAgendaTotalsAndKeepsItsOwnPerspective()
    {
        var p = await ArrangeAgendaPairAsync("t-agenda-totals-inactive", 12692m);
        var edit = await Send(p.C.Client, HttpMethod.Put, $"/api/financial-workspaces/{p.C.Workspace}/accounts/{p.BusinessAccount}", p.C.Session,
            new { name = "Itaú PJ", type = "Checking", financialScope = "PJ", isActive = false, openingBalance = 0m, openingBalanceDate = D(60) });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var all = await AgendaTotalsAsync(p.C, null);
        Assert.Equal(("100.00", "40.00", "12692.00"), (all.Inflows, all.Outflows, all.Internal)); // a transferencia continua interna e neutralizada
        var pj = await AgendaTotalsAsync(p.C, p.BusinessAccount);
        Assert.Equal(("100.00", "12692.00"), (pj.Inflows, pj.Outflows));
        Assert.Equal((all.Inflows, all.Outflows), await OverviewTotalsAsync(p.C, "all"));
        Assert.Equal((pj.Inflows, pj.Outflows), await AccountsTotalsAsync(p.C, p.BusinessAccount));
    }

    [Fact]
    public async Task APlannedTransferStaysOutOfTheRealizedAgendaTotalsAndIsReportedSeparately()
    {
        var p = await ArrangePlannedAsync("t-agenda-totals-planned",
            [Tx("pf-in", BankA, 3, 200m, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX")],
            [Tx("pj-out", BankB, 3, -200m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")]);
        await SyncBothAsync(p);
        string from = D(10), through = Today.AddDays(10).ToString("yyyy-MM-dd");
        var before = (await AgendaTotalsAsync(p.C, p.PersonalAccount, from, through), await AgendaTotalsAsync(p.C, p.BusinessAccount, from, through));

        await CreatePlannedAsync(p, p.BusinessAccount, p.PersonalAccount, 500m, 5);

        var pf = await AgendaTotalsAsync(p.C, p.PersonalAccount, from, through);
        var pj = await AgendaTotalsAsync(p.C, p.BusinessAccount, from, through);
        Assert.Equal(("200.00", "0.00"), (pf.Inflows, pf.Outflows)); // so a realizada
        Assert.Equal(("0.00", "200.00"), (pj.Inflows, pj.Outflows));
        Assert.Equal(("500.00", "0.00"), (pf.PlannedReceived, pf.PlannedSent));
        Assert.Equal(("0.00", "500.00"), (pj.PlannedReceived, pj.PlannedSent));
        Assert.Equal((before.Item1.Inflows, before.Item1.Outflows, before.Item2.Inflows, before.Item2.Outflows), (pf.Inflows, pf.Outflows, pj.Inflows, pj.Outflows));
    }
}

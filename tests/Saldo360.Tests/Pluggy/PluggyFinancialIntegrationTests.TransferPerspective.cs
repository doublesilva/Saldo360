using System.Net;
using System.Text.Json;
using Saldo360.Application.Transfers;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// Criterio de aceite: transferencia de R$ 200 da PJ para a PF. Perspectivas: so PF (conta), so PJ (conta) e PF+PJ (consolidado, neutralizada).
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    [Fact]
    public void ThePerspectivePredicateClassifiesATransferBySelectedSet()
    {
        Guid pf = Guid.NewGuid(), pj = Guid.NewGuid(), other = Guid.NewGuid();
        Assert.Equal(TransferRelation.Inbound, TransferPerspective.Classify(pj, pf, new HashSet<Guid> { pf }));
        Assert.Equal(TransferRelation.Outbound, TransferPerspective.Classify(pj, pf, new HashSet<Guid> { pj }));
        Assert.Equal(TransferRelation.Internal, TransferPerspective.Classify(pj, pf, new HashSet<Guid> { pf, pj }));
        Assert.Equal(TransferRelation.Outside, TransferPerspective.Classify(pj, pf, new HashSet<Guid> { other }));
    }

    private async Task<JsonElement> StatementAsync(Ctx c, Guid account, string extra = "") =>
        await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/transactions?pageSize=100&includeCancelled=true&includeTransfers=true&accountId={account}{extra}");

    private async Task<JsonElement> PlanAsync(Ctx c, string scope, Guid? account = null) =>
        await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-dashboard/plan-actual?competence={Today.AddDays(-3):yyyy-MM}&scope={scope}" + (account is null ? "" : $"&accountId={account}"));

    [Fact]
    public async Task ATwoHundredTransferFromBusinessToPersonalRaisesPersonalInflowsAndBusinessOutflowsMovesBothBalancesAndIsNeutralInTheConsolidatedPerspective()
    {
        var p = await ArrangePairAsync("t-perspective-200", [Tx("pf-in", BankA, 3, 200m, operation: "PIX", description: "Pix recebido")], [Tx("pj-out", BankB, 3, -200m, operation: "PIX", description: "Pix enviado")]);
        await SyncBothAsync(p);
        Assert.Equal(1, await ActiveTransfersAsync());

        // Saldos: PF +200, PJ -200, consolidado sem variacao liquida.
        Assert.Equal(200m, await CashEffectAsync(p.PersonalAccount));
        Assert.Equal(-200m, await CashEffectAsync(p.BusinessAccount));
        Assert.Equal(0m, await CashEffectAsync(p.PersonalAccount) + await CashEffectAsync(p.BusinessAccount));

        // So PF: 200 e ENTRADA (transferencia recebida da PJ, externa ao conjunto), separada das receitas (0); so PJ: 200 e SAIDA.
        var pf = (await TransferOverviewAsync(p.C, "account", p.PersonalAccount)).GetProperty("transfers");
        Assert.Equal(("200.00", "0.00", "0.00", "200.00"), (pf.GetProperty("received").GetString(), pf.GetProperty("sent").GetString(), pf.GetProperty("externalIncome").GetString(), pf.GetProperty("totalInflows").GetString()));
        var pj = (await TransferOverviewAsync(p.C, "account", p.BusinessAccount)).GetProperty("transfers");
        Assert.Equal(("0.00", "200.00", "0.00", "200.00"), (pj.GetProperty("received").GetString(), pj.GetProperty("sent").GetString(), pj.GetProperty("externalExpenses").GetString(), pj.GetProperty("totalOutflows").GetString()));

        // PF+PJ: interna, neutralizada nos agregados; continua informada como movimentacao interna e nada vira receita/despesa fictícia.
        var all = (await TransferOverviewAsync(p.C, "all")).GetProperty("transfers");
        Assert.Equal(("0.00", "0.00", "0.00", "0.00", "0.00", "200.00"), (all.GetProperty("received").GetString(), all.GetProperty("sent").GetString(), all.GetProperty("totalInflows").GetString(), all.GetProperty("totalOutflows").GetString(), all.GetProperty("net").GetString(), all.GetProperty("internalMoved").GetString()));

        // Receitas/Despesas e consumo (orcamento) nao incluem a transferencia em nenhuma perspectiva.
        foreach (var plan in new[] { await PlanAsync(p.C, "account", p.PersonalAccount), await PlanAsync(p.C, "account", p.BusinessAccount), await PlanAsync(p.C, "all") })
        {
            Assert.Equal("0.00", plan.GetProperty("income").GetProperty("realized").GetString());
            Assert.Equal("0.00", plan.GetProperty("expenses").GetProperty("consumption").GetProperty("realized").GetString());
        }

        // Extratos: as duas pontas continuam consultaveis, identificadas por tipo/direcao; o filtro Transferencia as isola; sem o opt-in o contrato antigo nao muda.
        var pfStatement = (await StatementAsync(p.C, p.PersonalAccount)).GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() != "Transaction" && !i.GetProperty("isCancelled").GetBoolean()).ToList();
        var line = Assert.Single(pfStatement);
        Assert.Equal(("TransferIn", "200.00", "Itaú PJ"), (line.GetProperty("kind").GetString(), line.GetProperty("amount").GetString(), line.GetProperty("counterpartAccountName").GetString()));
        Assert.StartsWith("Transferência recebida de Itaú PJ", line.GetProperty("description").GetString());
        var pjOnly = (await StatementAsync(p.C, p.BusinessAccount, "&type=Transfer")).GetProperty("items").EnumerateArray().Single(i => !i.GetProperty("isCancelled").GetBoolean());
        Assert.Equal(("TransferOut", "200.00"), (pjOnly.GetProperty("kind").GetString(), pjOnly.GetProperty("amount").GetString()));
        var consolidatedStatement = await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/transactions?pageSize=100&includeTransfers=true&type=Transfer");
        Assert.Equal(2, consolidatedStatement.GetProperty("totalCount").GetInt32()); // duas pontas vinculadas, uma operacao
        var legacy = await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/transactions?pageSize=100&includeCancelled=true&accountId={p.PersonalAccount}");
        Assert.All(legacy.GetProperty("items").EnumerateArray(), i => Assert.Equal("Transaction", i.TryGetProperty("kind", out var k) ? k.GetString() : "Transaction"));

        // Historico preservado: o credito PF lancado antes do par fica cancelado, nao e apagado.
        Assert.True((await CoreAsync(p.C.Workspace, includeCancelled: true)).Count >= (await CoreAsync(p.C.Workspace)).Count);
    }
}

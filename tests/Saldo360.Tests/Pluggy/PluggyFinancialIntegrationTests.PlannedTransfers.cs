using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// Transferencias PREVISTAS entre contas proprias: criacao, projecao (saida na origem/entrada no destino, neutra no consolidado), Agenda, cancelamento, associacao com a transferencia
/// realizada (so quando inequivoca), idempotencia e sincronizacao Pluggy. API real sobre PostgreSQL descartavel; Pluggy simulada.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    private async Task<Pair> ArrangePlannedAsync(string user, PluggyTransaction[]? personal = null, PluggyTransaction[]? business = null)
    {
        var p = await ArrangePairAsync(user, personal ?? [], business ?? []);
        foreach (var (account, name, scope) in new[] { (p.PersonalAccount, "Itaú PF", "PF"), (p.BusinessAccount, "Itaú PJ", "PJ") })
        {
            var edit = await Send(p.C.Client, HttpMethod.Put, $"/api/financial-workspaces/{p.C.Workspace}/accounts/{account}", p.C.Session,
                new { name, type = "Checking", financialScope = scope, isActive = true, openingBalance = 1000m, openingBalanceDate = D(60) });
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        }
        return p;
    }

    private async Task<JsonElement> CreatePlannedAsync(Pair p, Guid source, Guid destination, decimal amount, int daysFromToday, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await PostAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/planned-transfers",
            new { sourceAccountId = source, destinationAccountId = destination, amount, dueDate = Today.AddDays(daysFromToday).ToString("yyyy-MM-dd"), description = "Aporte previsto" });
        Assert.Equal(expected, response.StatusCode);
        return await JsonOf(response);
    }

    private Task<JsonElement> PlannedListAsync(Pair p, bool includeCancelled = false) =>
        JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/planned-transfers?from={Today.AddDays(-30):yyyy-MM-dd}&through={Today.AddDays(60):yyyy-MM-dd}&includeCancelled={(includeCancelled ? "true" : "false")}");

    /// <summary>Saldo projetado (fechamento) da conta no ultimo dia da janela; sem conta = consolidado.</summary>
    private async Task<decimal> ProjectedClosingAsync(Pair p, Guid? account, int throughDays = 10)
    {
        var url = $"/api/financial-workspaces/{p.C.Workspace}/financial-calendar/balances?from={Today:yyyy-MM-dd}&through={Today.AddDays(throughDays):yyyy-MM-dd}" + (account is null ? "" : $"&accountId={account}");
        var days = (await JsonGetAsync(p.C, url)).GetProperty("days").EnumerateArray().ToList();
        return decimal.Parse(days[^1].GetProperty("closingBalance").GetString()!, Inv);
    }

    private async Task<string> PlannedStatusAsync(Pair p, Guid id) =>
        (await PlannedListAsync(p, true)).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id).GetProperty("status").GetString()!;

    [Fact]
    public async Task APlannedTransferProjectsOutflowAtTheSourceAndInflowAtTheDestinationIsNeutralInTheConsolidatedAndIsListedInTheAgendaUntilCancelled()
    {
        var p = await ArrangePlannedAsync("t-planned-projection");
        var (pfBefore, pjBefore, allBefore) = (await ProjectedClosingAsync(p, p.PersonalAccount), await ProjectedClosingAsync(p, p.BusinessAccount), await ProjectedClosingAsync(p, null));

        var created = await CreatePlannedAsync(p, p.BusinessAccount, p.PersonalAccount, 500m, 5);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(("Planned", "500.00"), (created.GetProperty("status").GetString(), created.GetProperty("amount").GetString()));

        // Projecao: a PF recebe 500, a PJ envia 500 e o consolidado nao muda; o saldo realizado nao muda.
        Assert.Equal((pfBefore + 500m, pjBefore - 500m, allBefore), (await ProjectedClosingAsync(p, p.PersonalAccount), await ProjectedClosingAsync(p, p.BusinessAccount), await ProjectedClosingAsync(p, null)));
        Assert.Equal(0, await ActiveTransfersAsync());
        Assert.Empty(await CoreAsync(p.C.Workspace));
        // Antes da data prevista a projecao ainda nao inclui a transferencia.
        Assert.Equal(pfBefore, await ProjectedClosingAsync(p, p.PersonalAccount, throughDays: 2));

        // Agenda: as duas pontas (consolidado) ou so a da conta, como Pendente, e o resumo a parte de receitas/despesas.
        var url = $"/api/financial-workspaces/{p.C.Workspace}/financial-calendar?from={Today:yyyy-MM-dd}&through={Today.AddDays(10):yyyy-MM-dd}&includeTransfers=true";
        var all = await JsonGetAsync(p.C, url);
        Assert.Equal(2, all.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("source").GetString() == "Transfer" && i.GetProperty("status").GetString() == "Pending"));
        var pf = await JsonGetAsync(p.C, url + $"&accountId={p.PersonalAccount}");
        var leg = pf.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("source").GetString() == "Transfer");
        Assert.Equal(("Income", "500.00", id), (leg.GetProperty("type").GetString(), leg.GetProperty("amount").GetString(), leg.GetProperty("plannedTransferId").GetGuid()));
        Assert.Contains("CancelPlannedTransfer", leg.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(("500.00", "0.00", "0.00", "0.00"), (pf.GetProperty("summary").GetProperty("plannedTransfersReceived").GetString(), pf.GetProperty("summary").GetProperty("plannedTransfersSent").GetString(),
            pf.GetProperty("summary").GetProperty("pendingIncome").GetString(), pf.GetProperty("summary").GetProperty("pendingExpense").GetString()));

        // Cancelamento: a previsao deixa de projetar e de aparecer; o registro e preservado e a repeticao e idempotente.
        var cancel = await PostAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/planned-transfers/{id}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("Cancelled", (await JsonOf(cancel)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/planned-transfers/{id}/cancel")).StatusCode);
        Assert.Equal((pfBefore, pjBefore, allBefore), (await ProjectedClosingAsync(p, p.PersonalAccount), await ProjectedClosingAsync(p, p.BusinessAccount), await ProjectedClosingAsync(p, null)));
        Assert.DoesNotContain((await JsonGetAsync(p.C, url)).GetProperty("items").EnumerateArray(), i => i.GetProperty("source").GetString() == "Transfer");
        Assert.Empty((await PlannedListAsync(p)).EnumerateArray());
        Assert.Equal("Cancelled", await PlannedStatusAsync(p, id));
    }

    [Fact]
    public async Task APlannedTransferIsValidatedAndAnOverdueOneStillProjectsAsOverdue()
    {
        var p = await ArrangePlannedAsync("t-planned-validation");
        await CreatePlannedAsync(p, p.PersonalAccount, p.PersonalAccount, 10m, 3, HttpStatusCode.BadRequest);
        await CreatePlannedAsync(p, p.PersonalAccount, p.BusinessAccount, 0m, 3, HttpStatusCode.BadRequest);
        var before = await ProjectedClosingAsync(p, p.PersonalAccount, 0);

        var overdue = await CreatePlannedAsync(p, p.PersonalAccount, p.BusinessAccount, 100m, -2);

        Assert.Equal("Overdue", overdue.GetProperty("status").GetString());
        Assert.Equal(before - 100m, await ProjectedClosingAsync(p, p.PersonalAccount, 0)); // atrasada: tratada como a de hoje
    }

    [Fact]
    public async Task ARealizedTransferFulfilsTheSingleMatchingPlanWithoutDoubleCountingAndCancellingTheTransferReleasesIt()
    {
        var p = await ArrangePlannedAsync("t-planned-realized");
        var before = await ProjectedClosingAsync(p, p.PersonalAccount);
        var plan = (await CreatePlannedAsync(p, p.BusinessAccount, p.PersonalAccount, 300m, 2)).GetProperty("id").GetGuid();
        Assert.Equal(before + 300m, await ProjectedClosingAsync(p, p.PersonalAccount));

        var transfer = await PostAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/transfers",
            new { sourceAccountId = p.BusinessAccount, destinationAccountId = p.PersonalAccount, amount = 300m, transferDate = Today.ToString("yyyy-MM-dd"), description = "Aporte" });
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        var transferId = (await JsonOf(transfer)).GetProperty("id").GetGuid();

        // Associada com seguranca (uma previsao, uma transferencia, mesmas contas/valor, datas proximas): realizada conta uma vez so; nada duplica.
        var listed = (await PlannedListAsync(p, true)).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == plan);
        Assert.Equal(("Realized", transferId, "Inferred"), (listed.GetProperty("status").GetString(), listed.GetProperty("realizedTransferId").GetGuid(), listed.GetProperty("linkOrigin").GetString()));
        Assert.Equal(before + 300m, await ProjectedClosingAsync(p, p.PersonalAccount));
        Assert.Equal(1, await ActiveTransfersAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/planned-transfers/{plan}/cancel")).StatusCode);
        var calendar = await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/financial-calendar?from={Today:yyyy-MM-dd}&through={Today.AddDays(10):yyyy-MM-dd}&includeTransfers=true&accountId={p.PersonalAccount}");
        Assert.DoesNotContain(calendar.GetProperty("items").EnumerateArray(), i => i.GetProperty("status").GetString() is "Pending" or "Overdue" && i.GetProperty("source").GetString() == "Transfer");

        // Cancelar a transferencia realizada devolve a previsao ao estado de prevista (volta a projetar) e preserva o historico.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/transfers/{transferId}/cancel")).StatusCode);
        Assert.Equal("Planned", await PlannedStatusAsync(p, plan));
        Assert.Equal(before + 300m, await ProjectedClosingAsync(p, p.PersonalAccount));
    }

    [Fact]
    public async Task AnAmbiguousMatchIsNeverLinkedAutomaticallyAndTheUserConfirmsOneOptionWithoutDuplicates()
    {
        var p = await ArrangePlannedAsync("t-planned-ambiguous");
        var first = (await CreatePlannedAsync(p, p.BusinessAccount, p.PersonalAccount, 200m, 1)).GetProperty("id").GetGuid();
        var second = (await CreatePlannedAsync(p, p.BusinessAccount, p.PersonalAccount, 200m, 2)).GetProperty("id").GetGuid();

        var transfer = await PostAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/transfers",
            new { sourceAccountId = p.BusinessAccount, destinationAccountId = p.PersonalAccount, amount = 200m, transferDate = Today.ToString("yyyy-MM-dd"), description = "Aporte" });
        var transferId = (await JsonOf(transfer)).GetProperty("id").GetGuid();

        // Duas previsoes cabem na mesma transferencia: nada e escolhido sozinho; as candidatas ficam para o usuario.
        Assert.Equal(("Planned", "Planned"), (await PlannedStatusAsync(p, first), await PlannedStatusAsync(p, second)));
        var candidates = (await PlannedListAsync(p)).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == first).GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(transferId, Assert.Single(candidates).GetProperty("transferId").GetGuid());

        var link = await PostAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/planned-transfers/{first}/link", new { transferId });
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        Assert.Equal(("Realized", "UserConfirmed"), ((await JsonOf(link)).GetProperty("status").GetString(), (await JsonOf(link)).GetProperty("linkOrigin").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/planned-transfers/{second}/link", new { transferId })).StatusCode);
        Assert.Equal("Planned", await PlannedStatusAsync(p, second));
        // Desfazer a associacao devolve a previsao a prevista (a transferencia realizada nao muda).
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/planned-transfers/{first}/unlink")).StatusCode);
        Assert.Equal("Planned", await PlannedStatusAsync(p, first));
        Assert.Equal(1, await ActiveTransfersAsync());
    }

    [Fact]
    public async Task ThePluggySyncPairFulfilsTheMatchingPlanOnceAndRepeatedSyncAndReprocessKeepItStable()
    {
        var p = await ArrangePlannedAsync("t-planned-pluggy", [Tx("pf-in", BankA, 3, 200m, operation: "PIX", description: "Pix recebido")], [Tx("pj-out", BankB, 3, -200m, operation: "PIX", description: "Pix enviado")]);
        var plan = (await CreatePlannedAsync(p, p.BusinessAccount, p.PersonalAccount, 200m, -3)).GetProperty("id").GetGuid();
        Assert.Equal("Overdue", await PlannedStatusAsync(p, plan));

        for (var round = 0; round < 3; round++)
        {
            await SyncBothAsync(p);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess")).StatusCode);
            Assert.Equal("Realized", await PlannedStatusAsync(p, plan));
            Assert.Equal(1, await ActiveTransfersAsync());
        }

        await using var db = Db();
        var planned = await db.FinancialPlannedTransfers.AsNoTracking().SingleAsync();
        Assert.Equal(Saldo360.Domain.Transfers.PlannedTransferLinkOrigin.Inferred, planned.LinkOrigin);
        Assert.False(planned.IsCancelled);
        Assert.Equal(200m, await CashEffectAsync(p.PersonalAccount));
    }
}

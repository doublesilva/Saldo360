using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// Entrada 1.960,49 (classe "Transferencia potencial") e saida 748,31 ("Informacao insuficiente"): ficam importadas e em revisao, a decisao manual as lanca uma unica
/// vez, sobrevive a nova sincronizacao e ao aparecimento posterior do par na PJ, e a situacao financeira do importado diz sempre onde cada movimento esta.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private async Task<Dictionary<string, (string Status, string? Reason)>> SituationsAsync(Ctx c)
    {
        var page = await JsonOf(await GetAsync(c, $"{Root(c.Workspace)}/imported-transactions?pageSize=200"));
        return page.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("pluggyTransactionId").GetString()!,
            i => (i.GetProperty("financialStatus").GetString()!, i.GetProperty("financialReason").ValueKind == JsonValueKind.String ? i.GetProperty("financialReason").GetString() : null));
    }

    private async Task ResolveAsync(Ctx c, Guid posting, string resolution)
    {
        var response = await PostAsync(c, $"{Integration(c.Workspace)}/review/{posting}/resolve", new { resolution });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheSameInstitutionTransferCreditAndTheCreditOperationDebitStayInReviewWithAStatusUntilTheUserDecidesAndTheDecisionIsAppliedOnce()
    {
        var c = await ArrangeAsync("t-manual-decisions", seed: false);
        Pluggy.TransactionsByAccount[BankA] =
        [
            Tx("entrada", BankA, 8, 1960.49m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "GRPQA LTDA", category: null),
            Tx("crediario", BankA, 6, -748.31m, operation: "OPERACAO_CREDITO", description: "CREDIARIO AUTOM", category: null),
            Tx("normal", BankA, 5, -80m, operation: "PIX", description: "Pix enviado"),
            Tx("pendente", BankA, 1, -9m, "PENDING", operation: "PIX", description: "Pendente"),
        ];
        await LinkBankAsync(c);

        await SyncAsync(c);

        var before = await SituationsAsync(c);
        Assert.Equal(("InReview", "TransferWithoutCounterpart"), before["entrada"]);
        Assert.Equal(("InReview", "CreditOperation"), before["crediario"]);
        Assert.Equal("Accounted", before["normal"].Status);
        Assert.Equal("Waiting", before["pendente"].Status);
        var listed = await ListedAsync(c);
        Assert.False(Listed(listed, 1960.49m) || Listed(listed, 748.31m));
        var review = (await ReviewAsync(c)).GetProperty("items").EnumerateArray().ToList();
        var income = review.Single(i => i.GetProperty("amount").GetString() == "1960.49");
        var expense = review.Single(i => i.GetProperty("amount").GetString() == "748.31");
        Assert.Contains("AsIncome", income.GetProperty("allowedResolutions").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("AsExpense", expense.GetProperty("allowedResolutions").EnumerateArray().Select(x => x.GetString()));

        await ResolveAsync(c, income.GetProperty("postingId").GetGuid(), "AsIncome");
        await ResolveAsync(c, expense.GetProperty("postingId").GetGuid(), "AsExpense");

        var page = await ListedAsync(c);
        Assert.True(Listed(page, 1960.49m) && Listed(page, 748.31m));
        var after = await SituationsAsync(c);
        Assert.Equal(("Accounted", "Accounted"), (after["entrada"].Status, after["crediario"].Status));
        // Repetir a decisao nao duplica e nova sincronizacao/reprocessamento mantem um unico efeito por movimento.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(c, $"{Integration(c.Workspace)}/review/{income.GetProperty("postingId").GetGuid()}/resolve", new { resolution = "AsIncome" })).StatusCode);
        await SyncAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");
        var core = await CoreAsync(c.Workspace);
        Assert.Equal(1, core.Count(t => t.Amount == 1960.49m));
        Assert.Equal(1, core.Count(t => t.Amount == 748.31m));
        Assert.Empty(Reasons(await ReviewAsync(c)));
    }

    [Fact]
    public async Task AManualIncomeDecisionIsNotOverwrittenNorDuplicatedWhenTheBusinessAccountLaterBringsAMatchingDebit()
    {
        var p = await ArrangePairAsync("t-manual-then-pair", [Tx("entrada", BankA, 8, 1960.49m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "GRPQA LTDA", category: null)], []);
        await SyncAsync(p.C);
        var posting = await PostingIdAsync("entrada");
        await ResolveAsync(p.C, posting, "AsIncome");
        Assert.Equal(1, (await CoreAsync(p.C.Workspace)).Count(t => t.Amount == 1960.49m));

        Pluggy.TransactionsByAccount[BankB] = [Tx("saida-pj", BankB, 8, -1960.49m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "PARA PF", category: null)];
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.C, $"{Root(p.C.Workspace)}/connections/{p.ItemBConnection}/sync")).StatusCode);
        await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess");

        Assert.Equal(0, await ActiveTransfersAsync()); // a decisao manual vale: nenhum par e criado por cima dela
        var core = await CoreAsync(p.C.Workspace);
        Assert.Equal(1, core.Count(t => t.Amount == 1960.49m && t.Type == Saldo360.Domain.Categories.CategoryType.Income));
        await using var db = Db();
        Assert.Equal(PluggyResolution.AsIncome, (await db.PluggyPostings.AsNoTracking().SingleAsync(x => x.Id == posting)).Resolution);
        var situations = await SituationsAsync(p.C);
        Assert.Equal("Accounted", situations["entrada"].Status);
        Assert.Equal("InReview", situations["saida-pj"].Status); // a ponta PJ segue sem par e em revisao, sem efeito duplicado
    }

    [Fact]
    public async Task TheFinancialSituationOfAnImportedMovementCoversEveryState()
    {
        var p = await ArrangePairAsync("t-situation-states",
            [Tx("ok", BankA, 5, -5m, operation: "PIX"), Tx("espera", BankA, 1, -3m, "PENDING", operation: "PIX"), Tx("rev", BankA, 4, -6m, operation: "OPERACAO_CREDITO"), Tx("par-pf", BankA, 3, 40m, operation: "PIX", description: "Pix recebido")],
            [Tx("par-pj", BankB, 3, -40m, operation: "PIX"), Tx("solto", BankB, 2, 8m, operation: "PIX")], linkBusiness: false);
        await SyncBothAsync(p);
        var s = await SituationsAsync(p.C);
        Assert.Equal("Accounted", s["ok"].Status);
        Assert.Equal("Waiting", s["espera"].Status);
        Assert.Equal(("InReview", "CreditOperation"), s["rev"]);
        Assert.Equal("NotIntegrated", s["par-pj"].Status);
        Assert.Equal("NotIntegrated", s["solto"].Status);

        await LinkBusinessAsync(p.C, p.ItemBConnection);
        await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess");
        s = await SituationsAsync(p.C);
        Assert.Equal("Transfer", s["par-pf"].Status);
        Assert.Equal("Transfer", s["par-pj"].Status);
        Assert.Equal("Accounted", s["solto"].Status);
    }

    [Fact]
    public async Task AMovementBeforeTheImportStartReportsOutOfRangeInTheImportedList()
    {
        var c = await ArrangeAsync("t-situation-oor", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("cedo", BankA, 9, 12m, operation: "PIX"), Tx("tarde", BankA, 2, 13m, operation: "PIX")];
        await LinkBankAsync(c, extra: new { ImportFrom = D(5) });
        await SyncAsync(c);
        var s = await SituationsAsync(c);
        Assert.Equal("OutOfRange", s["cedo"].Status);
        Assert.Equal("Accounted", s["tarde"].Status);
    }
}

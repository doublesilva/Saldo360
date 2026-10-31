using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// Transferências PJ ↔ PF, receita externa, devoluções e recuperação: pareamento do mesmo dia, confirmação/rejeição/desfazer manuais, revisão que mostra
/// todas as pontas e prévia do reprocessamento. API real sobre PostgreSQL descartável, cliente Pluggy simulado (nenhum dado real).
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private sealed record Pair(Ctx C, Guid ItemBConnection, Guid PersonalAccount, Guid BusinessAccount);

    /// <summary>Duas contas integradas: BankA = "Itaú PF" e BankB = "Itaú PJ" (conexões diferentes, como no uso real).</summary>
    private async Task<Pair> ArrangePairAsync(string user, PluggyTransaction[] personal, PluggyTransaction[] business, bool linkBusiness = true)
    {
        var c = await ArrangeAsync(user, seed: false);
        var itemB = await LinkAsync(c.Client, c.Session, c.Workspace, ItemB, "Itaú PJ");
        Pluggy.TransactionsByAccount[BankA] = [.. personal];
        Pluggy.TransactionsByAccount[BankB] = [.. business];
        var personalAccount = (await LinkBankAsync(c, name: "Itaú PF")).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        var businessAccount = Guid.Empty;
        if (linkBusiness) businessAccount = await LinkBusinessAsync(c, itemB);
        return new(c, itemB, personalAccount, businessAccount);
    }

    private async Task<Guid> LinkBusinessAsync(Ctx c, Guid itemB)
    {
        var response = await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = itemB, pluggyAccountId = BankB, mode = "CreateNew", name = "Itaú PJ", financialScope = "PJ" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
    }

    private async Task SyncBothAsync(Pair p)
    {
        await SyncAsync(p.C);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.C, $"{Root(p.C.Workspace)}/connections/{p.ItemBConnection}/sync")).StatusCode);
    }

    private async Task<Guid> PostingIdAsync(string externalTransactionId)
    {
        await using var db = Db();
        return await (from posting in db.PluggyPostings join imported in db.PluggyImportedTransactions on posting.ImportedTransactionId equals imported.Id
                      where imported.PluggyTransactionId == externalTransactionId select posting.Id).SingleAsync();
    }

    private Task<HttpResponseMessage> LinkActionAsync(Ctx c, string action, Guid posting, Guid? counterpart = null) =>
        PostAsync(c, $"{Integration(c.Workspace)}/review/{posting}/transfer/{action}", counterpart is null ? new { } : new { counterpartPostingId = counterpart });

    private async Task<(decimal Income, decimal Expense)> RealizedAsync(Ctx c)
    {
        var summary = (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={D(60)}&through={D(0)}")).GetProperty("summary");
        return (decimal.Parse(summary.GetProperty("realizedIncome").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(summary.GetProperty("realizedExpense").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<int> ActiveTransfersAsync() { await using var db = Db(); return await db.FinancialTransfers.CountAsync(t => !t.IsCancelled); }

    // ---- A. Receita externa na PJ ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExternalReceiptOnTheBusinessAccountIsRevenueOfThatAccountOnceAndNeverATransfer()
    {
        var p = await ArrangePairAsync("t-link-external", [], [Tx("ted-in", BankB, 3, 17000m, operation: "TED", description: "TED 237.2276.WK R H E", category: "Transfer - TED")]);

        await SyncBothAsync(p);
        await SyncBothAsync(p);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess")).StatusCode);

        var core = await CoreAsync(p.C.Workspace);
        Assert.Equal([(17000m, p.BusinessAccount)], core.Select(t => (t.Amount, t.AccountId)));
        Assert.Equal((17000m, 0m), await RealizedAsync(p.C));
        Assert.Equal(0, await ActiveTransfersAsync());
        Assert.Empty(Reasons(await ReviewAsync(p.C)));
    }

    // ---- B. Transferência PJ → PF: nenhuma ponta contabilizada antes ------------------------------------------------------------

    [Fact]
    public async Task ATransferFromTheBusinessToThePersonalAccountMovesBothBalancesWithoutRevenueOrExpenseAndNeverDuplicates()
    {
        var p = await ArrangePairAsync("t-link-pj-pf", [Tx("pf-in", BankA, 5, 5000m, operation: "PIX", description: "Pix recebido ITAU PJ", category: "Transfer - PIX")],
            [Tx("pj-out", BankB, 5, -5000m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")]);

        await SyncBothAsync(p);
        await SyncBothAsync(p);
        await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess");

        await using var db = Db();
        var transfer = await db.FinancialTransfers.AsNoTracking().SingleAsync();
        Assert.Equal((p.BusinessAccount, p.PersonalAccount, 5000m, false), (transfer.SourceAccountId, transfer.DestinationAccountId, transfer.Amount, transfer.IsCancelled));
        Assert.Empty(await CoreAsync(p.C.Workspace));
        Assert.Equal((0m, 0m), await RealizedAsync(p.C)); // sem receita nem despesa consolidada adicional
        var postings = await db.PluggyPostings.AsNoTracking().ToListAsync();
        Assert.All(postings, x => Assert.Equal((PluggyPostingKind.Transfer, PluggyLinkOrigin.Inferred, "transfer-v2"), (x.Kind, x.LinkOrigin!.Value, x.LinkRuleVersion)));
        Assert.Empty(Reasons(await ReviewAsync(p.C)));
        // As transferências têm acesso próprio, com origem e destino identificados (PJ enviada / PF recebida).
        var transfers = await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/transfers");
        var listed = transfers.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("Itaú PJ", "Itaú PF"), (listed.GetProperty("sourceAccount").GetProperty("name").GetString(), listed.GetProperty("destinationAccount").GetProperty("name").GetString()));
    }

    // ---- C. Ambas as pontas contabilizadas: desfazer, rejeição que sobrevive e confirmação manual -------------------------------------------

    [Fact]
    public async Task UndoingATransferPreservesTheRealMovementsThePairIsNeverRecreatedAndAManualConfirmationReplacesBothAccountedEnds()
    {
        var p = await ArrangePairAsync("t-link-undo", [Tx("pf-in", BankA, 4, 700m, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX")],
            [Tx("pj-out", BankB, 4, -700m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")]);
        await SyncBothAsync(p);
        Assert.Equal(1, await ActiveTransfersAsync());
        var pfIn = await PostingIdAsync("pf-in"); var pjOut = await PostingIdAsync("pj-out");

        // Desfazer: a transferência é cancelada, os dois movimentos reais voltam a existir (receita na PF, despesa na PJ) e nada é apagado.
        var undo = await LinkActionAsync(p.C, "undo", pfIn);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal("Done", (await JsonOf(undo)).GetProperty("outcome").GetString());
        Assert.Equal(0, await ActiveTransfersAsync());
        Assert.Equal(new[] { (700m, p.PersonalAccount), (700m, p.BusinessAccount) }.Order(), (await CoreAsync(p.C.Workspace)).Select(t => (t.Amount, t.AccountId)).Order());
        Assert.Equal((700m, 700m), await RealizedAsync(p.C));
        await using (var db = Db())
        {
            Assert.Equal(1, await db.FinancialTransfers.CountAsync(t => t.IsCancelled)); // histórico preservado
            Assert.Equal(1, await db.PluggyTransferRejections.CountAsync());
            Assert.Equal(2, await db.PluggyImportedTransactions.CountAsync()); // evidências intactas
        }

        // Repetir é idempotente; sincronizações e reprocessamento não recriam o par rejeitado.
        var again = await LinkActionAsync(p.C, "undo", pfIn);
        Assert.Equal(("OK", "AlreadyDone"), (again.StatusCode.ToString(), (await JsonOf(again)).GetProperty("outcome").GetString()));
        await SyncBothAsync(p);
        await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess");
        Assert.Equal(0, await ActiveTransfersAsync());
        Assert.Equal((700m, 700m), await RealizedAsync(p.C));
        Assert.Equal(2, (await CoreAsync(p.C.Workspace)).Count);

        // Cenário "ambas contabilizadas": o usuário confirma o par; os dois lançamentos são substituídos pela transferência (um débito e um crédito).
        var confirm = await LinkActionAsync(p.C, "confirm", pjOut, pfIn);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(1, await ActiveTransfersAsync());
        Assert.Empty(await CoreAsync(p.C.Workspace));
        Assert.Equal((0m, 0m), await RealizedAsync(p.C));
        await using var final = Db();
        Assert.Equal(3, await final.FinancialTransactions.CountAsync(t => t.IsCancelled)); // a receita da PF substituída no 1o pareamento + os dois lançamentos da confirmação: tudo no histórico
        Assert.Equal(0, await final.PluggyTransferRejections.CountAsync()); // a confirmação do usuário supera a rejeição anterior
        var postings = await final.PluggyPostings.AsNoTracking().ToListAsync();
        Assert.All(postings, x => Assert.Equal((PluggyLinkOrigin.UserConfirmed, null as string), (x.LinkOrigin!.Value, x.LinkRuleVersion)));
        Assert.All(postings, x => Assert.NotNull(x.ResolvedByUserId));

        // Confirmar de novo não gera efeito algum.
        var repeat = await LinkActionAsync(p.C, "confirm", pfIn, pjOut);
        Assert.Equal("AlreadyDone", (await JsonOf(repeat)).GetProperty("outcome").GetString());
        Assert.Equal(1, await ActiveTransfersAsync());
        await SyncBothAsync(p);
        Assert.Equal(1, await ActiveTransfersAsync());
        Assert.Empty(await CoreAsync(p.C.Workspace));
    }

    // ---- Datas diferentes -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EndsWithDifferentDatesAreNeverForcedIntoACommonDateAndTheAccountedEndStaysInTheTotalsUntilTheDecision()
    {
        var p = await ArrangePairAsync("t-link-dates", [Tx("pf-in", BankA, 4, 300m, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX")],
            [Tx("pj-out", BankB, 5, -300m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")], linkBusiness: false);
        await SyncAsync(p.C); // só a PF existe: a entrada já é receita contabilizada
        Assert.Equal((300m, 0m), await RealizedAsync(p.C));
        await LinkBusinessAsync(p.C, p.ItemBConnection);

        await SyncBothAsync(p);

        Assert.Equal(0, await ActiveTransfersAsync());
        var review = await ReviewAsync(p.C);
        Assert.Equal(["TransferDateMismatch", "TransferDateMismatch"], Reasons(review));
        var items = review.GetProperty("items").EnumerateArray().ToList();
        var accounted = items.Single(i => i.GetProperty("accounted").GetBoolean());
        var notAccounted = items.Single(i => !i.GetProperty("accounted").GetBoolean());
        Assert.Equal(("InTotals", "Income"), (accounted.GetProperty("impact").GetString(), accounted.GetProperty("direction").GetString()));
        Assert.Equal(("OutOfTotals", "Expense"), (notAccounted.GetProperty("impact").GetString(), notAccounted.GetProperty("direction").GetString()));
        Assert.Equal((300m, 0m), await RealizedAsync(p.C)); // a despesa da ponta nova ainda não conta; a receita já contabilizada não foi removida em silêncio
        var candidate = notAccounted.GetProperty("candidates").EnumerateArray().Single();
        Assert.Equal((false, false, "DifferentDate"), (candidate.GetProperty("sameDay").GetBoolean(), candidate.GetProperty("canConfirm").GetBoolean(), candidate.GetProperty("blockedReason").GetString()));

        // Confirmar um par de datas diferentes é recusado (não há data comum a gravar).
        var refused = await LinkActionAsync(p.C, "confirm", await PostingIdAsync("pj-out"), await PostingIdAsync("pf-in"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("DifferentDate", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, await ActiveTransfersAsync());

        // Rejeitar a sugestão: cada ponta segue como o movimento comum que é, e a rejeição sobrevive às sincronizações.
        var reject = await LinkActionAsync(p.C, "reject", await PostingIdAsync("pj-out"), await PostingIdAsync("pf-in"));
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.Equal((300m, 300m), await RealizedAsync(p.C));
        Assert.Empty(Reasons(await ReviewAsync(p.C)));
        await SyncBothAsync(p);
        Assert.Equal((300m, 300m), await RealizedAsync(p.C));
        Assert.Equal(0, await ActiveTransfersAsync());
    }

    // ---- Ambiguidade, confirmação idempotente e concorrente ------------------------------------------------------------------------

    [Fact]
    public async Task AnAmbiguousCounterpartIsResolvedByAConfirmationThatIsIdempotentAndConcurrentConfirmationsOfTheSameEndConflict()
    {
        var p = await ArrangePairAsync("t-link-concurrent", [Tx("pf-out", BankA, 6, -300m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")],
            [Tx("pj-in1", BankB, 6, 300m, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX"), Tx("pj-in2", BankB, 6, 300m, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX")],
            linkBusiness: false);
        await SyncAsync(p.C); // a saída da PF já é despesa contabilizada
        await LinkBusinessAsync(p.C, p.ItemBConnection);
        await SyncBothAsync(p);
        Assert.Equal(0, await ActiveTransfersAsync());
        Assert.Equal((0m, 300m), await RealizedAsync(p.C)); // as duas entradas ambíguas ficam fora dos totais; a saída contabilizada permanece

        var pfOut = await PostingIdAsync("pf-out"); var in1 = await PostingIdAsync("pj-in1"); var in2 = await PostingIdAsync("pj-in2");
        var review = await ReviewAsync(p.C);
        var own = review.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("postingId").GetGuid() == pfOut);
        Assert.Equal(2, own.GetProperty("candidates").GetArrayLength());
        Assert.All(own.GetProperty("candidates").EnumerateArray(), x => Assert.True(x.GetProperty("canConfirm").GetBoolean()));

        var results = await Task.WhenAll(LinkActionAsync(p.C, "confirm", pfOut, in1), LinkActionAsync(p.C, "confirm", pfOut, in2));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await ActiveTransfersAsync());
        // A ponta que sobrou não foi usada duas vezes: entra como receita comum, uma única vez.
        var core = await CoreAsync(p.C.Workspace);
        Assert.Equal([300m], core.Select(t => t.Amount));
        Assert.Equal(CategoryType.Income, core.Single().Type);
        Assert.Equal((300m, 0m), await RealizedAsync(p.C));
        Assert.Empty(Reasons(await ReviewAsync(p.C)));

        // Repetição da confirmação vencedora é idempotente.
        var winner = results.Select((r, i) => (r, i)).Single(x => x.r.StatusCode == HttpStatusCode.OK).i == 0 ? in1 : in2;
        var repeat = await LinkActionAsync(p.C, "confirm", pfOut, winner);
        Assert.Equal("AlreadyDone", (await JsonOf(repeat)).GetProperty("outcome").GetString());
        Assert.Equal(1, await ActiveTransfersAsync());
    }

    [Fact]
    public async Task LinkActionsAreValidatedAgainstTheWorkspaceTheDirectionsAndTheCounterpart()
    {
        var p = await ArrangePairAsync("t-link-validate", [Tx("pf-out", BankA, 6, -300m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX"), Tx("pf-out2", BankA, 6, -40m, operation: "PIX", category: "Transfer - PIX")],
            [Tx("pj-out", BankB, 6, -300m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")]);
        await SyncBothAsync(p);
        var pfOut = await PostingIdAsync("pf-out"); var pjOut = await PostingIdAsync("pj-out");
        var other = await SignInAsync("t-link-validate-other");
        var otherWorkspace = await CreateWorkspaceAsync(other);
        var intruder = new Ctx(other, otherWorkspace.Id, p.C.Client, p.C.Connection);

        Assert.Equal(HttpStatusCode.BadRequest, (await LinkActionAsync(p.C, "confirm", pfOut, pjOut)).StatusCode); // dois débitos
        Assert.Equal(HttpStatusCode.BadRequest, (await LinkActionAsync(p.C, "confirm", pfOut, pfOut)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LinkActionAsync(p.C, "confirm", pfOut, null)).StatusCode); // sem a outra ponta
        Assert.Equal(HttpStatusCode.NotFound, (await LinkActionAsync(p.C, "confirm", pfOut, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await LinkActionAsync(intruder, "confirm", pfOut, pjOut)).StatusCode); // outro orçamento não alcança os rastros
        Assert.Equal(HttpStatusCode.NotFound, (await LinkActionAsync(intruder, "undo", pfOut)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LinkActionAsync(p.C, "undo", pfOut)).StatusCode); // não é uma transferência aplicada
        Assert.Equal(0, await ActiveTransfersAsync());
        Assert.Equal(3, (await CoreAsync(p.C.Workspace)).Count); // nada mudou
    }

    // ---- Devolução bancária: dica de descrição e prévia -----------------------------------------------------------------------------

    [Fact]
    public async Task ACreditDescribedAsAReturnGoesToReviewInsteadOfBecomingRevenueAndStaysOutOfTheTotals()
    {
        var p = await ArrangePairAsync("t-link-refund-new", [Tx("pay", BankA, 12, -55.99m, operation: "PIX", description: "Pagamento de Pix QR Code Pix Marketplace", category: "Shopping"),
            Tx("back", BankA, 2, 55.99m, operation: "PIX", description: "Pix devolvido Pix Marketplace", category: "Shopping")], [], linkBusiness: false);

        await SyncAsync(p.C);

        Assert.Equal((0m, 55.99m), await RealizedAsync(p.C)); // a devolução não entra como receita
        var review = await ReviewAsync(p.C);
        Assert.Equal(["PossibleRefundByDescription"], Reasons(review));
        var item = review.GetProperty("items")[0];
        Assert.Equal(("Evidence", "OutOfTotals", false), (item.GetProperty("reviewKind").GetString(), item.GetProperty("impact").GetString(), item.GetProperty("accounted").GetBoolean()));
    }

    [Fact]
    public async Task ThePreviewNeverPersistsListsWhatNeedsAManualDecisionAndTracesAMovementByAmount()
    {
        var p = await ArrangePairAsync("t-link-preview-refund", [Tx("pay", BankA, 12, -55.99m, operation: "PIX", description: "Pagamento de Pix QR Code Pix Marketplace", category: "Shopping"),
            Tx("back", BankA, 2, 55.99m, operation: "PIX", description: "Pix recebido", category: "Shopping")], [], linkBusiness: false);
        await SyncAsync(p.C);
        Assert.Equal((55.99m, 55.99m), await RealizedAsync(p.C)); // estado herdado: a devolução foi contabilizada como receita
        // O banco passa a descrever o crédito como devolução: o efeito aplicado é preservado (estabilidade), mas a regra atual leria diferente.
        Pluggy.TransactionsByAccount[BankA] = [.. Pluggy.TransactionsByAccount[BankA].Select(t => t.Id == "back" ? t with { Description = "Pix devolvido Pix Marketplace", DescriptionRaw = "PIX DEVOLVIDO" } : t)];
        await SyncAsync(p.C);
        Assert.Equal((55.99m, 55.99m), await RealizedAsync(p.C));
        await using var db = Db();
        var before = (await db.FinancialTransactions.CountAsync(), await db.PluggyPostings.CountAsync(), await db.FinancialTransfers.CountAsync(), await db.PluggyTransferRejections.CountAsync());

        var response = await PostAsync(p.C, $"{Integration(p.C.Workspace)}/recovery/preview", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await JsonOf(response);
        Assert.False(preview.GetProperty("persisted").GetBoolean());
        var decision = preview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("outcome").GetString() == "NeedsDecision");
        Assert.Equal(("Income", "Rule:PossibleRefundByDescription", "55.99"), (decision.GetProperty("current").GetProperty("effect").GetString(), decision.GetProperty("note").GetString(), decision.GetProperty("amount").GetString()));
        Assert.Equal(1, preview.GetProperty("needsDecision").GetInt32());
        Assert.Equal(before, (await db.FinancialTransactions.CountAsync(), await db.PluggyPostings.CountAsync(), await db.FinancialTransfers.CountAsync(), await db.PluggyTransferRejections.CountAsync()));
        Assert.Equal((55.99m, 55.99m), await RealizedAsync(p.C)); // nada mudou

        // Rastreio por valor: cadeia completa dos dois movimentos de 55,99 (débito original e crédito), com a interpretação atual e a proposta.
        var trace = await JsonOf(await PostAsync(p.C, $"{Integration(p.C.Workspace)}/recovery/preview", new { amount = 55.99m }));
        var chain = trace.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, chain.Count);
        Assert.Equal(new[] { "Income", "Expense" }.Order(), chain.Select(i => i.GetProperty("current").GetProperty("effect").GetString()!).Order());
        Assert.All(chain, i => Assert.Equal("Applied", i.GetProperty("current").GetProperty("state").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.C, $"{Integration(p.C.Workspace)}/recovery/preview", new { from = D(30), to = D(0) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(p.C, $"{Integration(p.C.Workspace)}/recovery/preview", new { amount = -1m })).StatusCode);
    }

    [Fact]
    public async Task ThePreviewAnticipatesExactlyWhatTheIdempotentReprocessingAppliesAndPreservesManualDecisions()
    {
        var p = await ArrangePairAsync("t-link-preview-recovery", [Tx("pf-in", BankA, 4, 700m, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX")],
            [Tx("pj-out", BankB, 4, -700m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")]);
        await SyncBothAsync(p);
        var pfIn = await PostingIdAsync("pf-in");
        await LinkActionAsync(p.C, "undo", pfIn); // estado "legado": as duas pontas contabilizadas como receita e despesa
        await using (var db = Db()) await db.PluggyTransferRejections.ExecuteDeleteAsync(); // emula o estado anterior à rejeição (o motor voltaria a inferir o par)
        Assert.Equal((700m, 700m), await RealizedAsync(p.C));

        var preview = await JsonOf(await PostAsync(p.C, $"{Integration(p.C.Workspace)}/recovery/preview", new { }));

        Assert.False(preview.GetProperty("persisted").GetBoolean());
        Assert.Equal(1, preview.GetProperty("wouldApply").GetProperty("transfersPaired").GetInt32());
        Assert.Equal(2, preview.GetProperty("willChange").GetInt32());
        var before = preview.GetProperty("totalBefore"); var after = preview.GetProperty("totalAfter");
        Assert.Equal(("700.00", "700.00", "0.00", "0.00"), (before.GetProperty("income").GetString(), before.GetProperty("expense").GetString(), before.GetProperty("transfersIn").GetString(), before.GetProperty("transfersOut").GetString()));
        Assert.Equal(("0.00", "0.00", "700.00", "700.00", "0.00"), (after.GetProperty("income").GetString(), after.GetProperty("expense").GetString(), after.GetProperty("transfersIn").GetString(), after.GetProperty("transfersOut").GetString(), after.GetProperty("net").GetString()));
        Assert.Equal(0, await ActiveTransfersAsync()); // a prévia não gravou nada
        Assert.Equal((700m, 700m), await RealizedAsync(p.C));

        // A aplicação real (reprocessamento) produz exatamente o previsto e repeti-la não muda mais nada.
        var applied = await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess");
        Assert.Equal(1, (await JsonOf(applied)).GetProperty("transfersPaired").GetInt32());
        Assert.Equal(1, await ActiveTransfersAsync());
        Assert.Equal((0m, 0m), await RealizedAsync(p.C));
        var second = await JsonOf(await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess"));
        Assert.Equal(0, second.GetProperty("transfersPaired").GetInt32());
        Assert.Equal(1, await ActiveTransfersAsync());
        var again = await JsonOf(await PostAsync(p.C, $"{Integration(p.C.Workspace)}/recovery/preview", new { }));
        Assert.Equal((0, 0), (again.GetProperty("willChange").GetInt32(), again.GetProperty("needsDecision").GetInt32()));
    }
}

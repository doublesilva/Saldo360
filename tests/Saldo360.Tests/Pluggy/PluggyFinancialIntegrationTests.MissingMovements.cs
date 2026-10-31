using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// "Lançamento que não aparece em Lançamentos": cada forma de um movimento efetivado importado da Pluggy não virar lançamento de conta, e a garantia de que
/// nenhuma delas é silenciosa (sempre há posting com estado/motivo, e a prévia de recuperação por valor localiza o movimento). Pluggy simulada, sem dado real.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private async Task<JsonElement> ListedAsync(Ctx c) =>
        await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/transactions?pageSize=100&includeCancelled=true");

    private static bool Listed(JsonElement page, decimal amount) =>
        page.GetProperty("items").EnumerateArray().Any(i => decimal.Parse(i.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture) == amount);

    private async Task<JsonElement> TraceAsync(Ctx c, decimal amount) =>
        await JsonOf(await PostAsync(c, $"{Integration(c.Workspace)}/recovery/preview", new { amount }));

    [Fact]
    public async Task AnIncomingPixWithAnOppositeSameDaySameAmountDebitInAnotherIntegratedAccountBecomesATransferAndIsTraceableByAmount()
    {
        var p = await ArrangePairAsync("t-missing-transfer-end", [Tx("pf-in", BankA, 8, 4321.55m, operation: "PIX", description: "Pix recebido EMPRESA", category: "Transfer - PIX")],
            [Tx("pj-out", BankB, 8, -4321.55m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")]);

        await SyncBothAsync(p);

        // Sai de Lançamentos ativos: vive em Transferências. Se a ponta já tinha sido lançada como receita antes do par existir (A sincronizou antes de B), o lançamento
        // fica CANCELADO e visível só com "Mostrar cancelados"; se B sincroniza primeiro, nem isso existe.
        var active = await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/transactions?pageSize=100");
        Assert.False(Listed(active, 4321.55m));
        Assert.Equal(1, await ActiveTransfersAsync());
        var trace = (await TraceAsync(p.C, 4321.55m)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, trace.Count);
        Assert.All(trace, i => Assert.Equal(("Applied", "Transfer"), (i.GetProperty("current").GetProperty("state").GetString(), i.GetProperty("current").GetProperty("kind").GetString())));
    }

    [Fact]
    public async Task ACreditOperationDebitNeverBecomesAMovementButWaitsInReviewAndIsTraceableByAmount()
    {
        var c = await ArrangeAsync("t-missing-credit-op", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("crediario", BankA, 6, -389.9m, operation: "OPERACAO_CREDITO", description: "Crediario Solar", category: "Loans")];
        await LinkBankAsync(c);

        await SyncAsync(c);

        Assert.False(Listed(await ListedAsync(c), 389.9m));
        Assert.Equal(["CreditOperation"], Reasons(await ReviewAsync(c))); // não é silencioso: está na revisão com motivo
        var item = (await TraceAsync(c, 389.9m)).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("NeedsReview", "CreditOperation"), (item.GetProperty("current").GetProperty("state").GetString(), item.GetProperty("current").GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task AnUnknownOperationTypeDebitIsAnOrdinaryExpenseAndAppearsInTheMovementList()
    {
        var c = await ArrangeAsync("t-missing-unknown-op", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("crediario2", BankA, 6, -389.9m, operation: "CREDIARIO_LOJA", description: "Crediario Solar", category: null)];
        await LinkBankAsync(c);

        await SyncAsync(c);

        Assert.True(Listed(await ListedAsync(c), 389.9m));
        Assert.Empty(Reasons(await ReviewAsync(c)));
    }

    [Fact]
    public async Task AMovementBeforeTheImportStartHasNoPostingButTheTraceByAmountAndPeriodStillFindsItAsOutOfRange()
    {
        var c = await ArrangeAsync("t-missing-out-of-range", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("early", BankA, 9, 777.7m, operation: "PIX", description: "Pix recebido"), Tx("late", BankA, 2, 11m, operation: "PIX", description: "Pix recebido")];
        await LinkBankAsync(c, extra: new { ImportFrom = D(5) });

        await SyncAsync(c);

        var page = await ListedAsync(c);
        Assert.False(Listed(page, 777.7m));
        Assert.True(Listed(page, 11m));
        await using (var db = Db()) Assert.Equal(1, await db.PluggyPostings.CountAsync());
        var item = (await TraceAsync(c, 777.7m)).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("OutOfRange", "BeforeImportFrom", "NoPosting"), (item.GetProperty("proposed").GetProperty("state").GetString(), item.GetProperty("proposed").GetProperty("reason").GetString(), item.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task AMovementOfAnAccountThatWasNotLinkedIsImportedButNeverFeedsAnything()
    {
        var p = await ArrangePairAsync("t-missing-unlinked", [], [Tx("pj-credit", BankB, 4, 1500m, operation: "PIX", description: "Pix recebido")], linkBusiness: false);

        await SyncBothAsync(p);

        Assert.False(Listed(await ListedAsync(p.C), 1500m));
        await using var db = Db();
        Assert.Equal(1, await db.PluggyImportedTransactions.CountAsync(t => t.Amount == 1500m));
        Assert.Equal(0, await db.PluggyPostings.CountAsync());
    }

    [Fact]
    public async Task APendingDebitWaitsAndADebitCitingAnInvoicePaymentWithoutTheCardEvidenceWaitsInReviewInsteadOfDisappearing()
    {
        var c = await ArrangeAsync("t-missing-wait-bill", seed: false);
        Pluggy.TransactionsByAccount[BankA] =
        [
            Tx("pending", BankA, 1, -42m, "PENDING", operation: "PIX", description: "Compra pendente"),
            Tx("bill", BankA, 3, -250m, operation: "BOLETO", description: "PAGAMENTO FATURA CARTAO"),
        ];
        await LinkBankAsync(c);

        await SyncAsync(c);

        var page = await ListedAsync(c);
        Assert.False(Listed(page, 42m));
        Assert.False(Listed(page, 250m));
        Assert.Equal(["UnconfirmedCardBillPayment"], Reasons(await ReviewAsync(c)));
        await using var db = Db();
        Assert.Equal(1, await db.PluggyPostings.CountAsync(p => p.State == PluggyPostingState.WaitingPosted));
        Assert.Equal(0, await db.PluggyPostings.CountAsync(p => p.State == PluggyPostingState.Applied));
    }
}

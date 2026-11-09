using System.Text.Json;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.Pluggy;

// T13.6: hierarquia de fontes do valor da fatura pela API real. Fatura fechada = total oficial; fatura aberta com total válido do banco = esse total (substitui a composição local, nunca soma);
// sem total do banco = projeção local. Uma nova observação atualiza o valor sem duplicar compras ou parcelas.
public sealed partial class PluggyFinancialIntegrationTests
{
    private async Task<Ctx> ArrangeOpenBillAsync(string user, decimal? openBankTotal)
    {
        var c = await ArrangeAsync(user, seed: false);
        var closings = Enumerable.Range(0, 4).Select(i => LatestClosing.AddMonths(-i)).ToList();
        var bills = closings.Select((closing, i) => TotalBill($"bill-{i}", closing, i == 0 ? 230m : 100m)).ToList();
        if (openBankTotal is { } total) bills.Add(TotalBill("bill-open", NextClosing, total));      // fatura ainda aberta: fechamento por vir
        Pluggy.BillsByAccount[CardA] = bills;
        Pluggy.TransactionsByAccount[CardA] =
        [
            CardTx("c1", "LOJA", 150m, LatestClosing.AddDays(-12), "bill-0"), CardTx("c2", "TV", 80m, LatestClosing.AddDays(-9), "bill-0"), CardTx("old", "ANTIGA", 100m, closings[1].AddDays(-3), "bill-1"),
            CardTx("n1", "MERCADO", 100m, NextClosing.AddDays(-3), "", status: "PENDING"),     // compra local da fatura aberta
        ];
        Pluggy.TransactionsByAccount[BankA] = [];
        var bank = await LinkBankAsync(c);
        var card = await LinkCardAsync(c, ClosingDay, DueDay);
        await SetPayerAsync(c, card.GetProperty("link").GetProperty("creditCardId").GetGuid(), bank.GetProperty("link").GetProperty("financialAccountId").GetGuid());
        await SyncAsync(c);
        return c;
    }

    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task AnOpenInvoiceWithAValidBankTotalUsesThatTotalInTheBudgetAndANewObservationUpdatesItWithoutDuplicatingAnything()
    {
        var c = await ArrangeOpenBillAsync("t136-aberta", openBankTotal: 750m);
        var closed = Competence(LatestClosing); var next = Competence(NextClosing);

        var invoice = InvoiceOf(await OverviewAsync(c, next), next);
        Assert.Equal(("BankOpen", "750.00", "100.00", "650.00"), (S(invoice, "source"), Money(invoice, "obligation"), Money(invoice, "localTotal"), Money(invoice, "undistributed")));
        Assert.Equal("750.00", Money(invoice, "bankTotal"));
        Assert.Equal("750.00", Money((await PfBudgetAsync(c, next)).GetProperty("cards"), "realized"));    // consumo = total do banco; a compra local de 100 não é somada por cima
        Assert.Equal("230.00", Money(InvoiceOf(await OverviewAsync(c, closed), closed), "obligation"));  // a fatura fechada segue o total oficial

        // Nova observação do banco: o valor é atualizado, não acrescentado.
        Pluggy.BillsByAccount[CardA] = Pluggy.BillsByAccount[CardA].Select(b => b.Id == "bill-open" ? TotalBill("bill-open", NextClosing, 820m) : b).ToList();
        await SyncAsync(c);
        var updated = InvoiceOf(await OverviewAsync(c, next), next);
        Assert.Equal(("BankOpen", "820.00", "100.00"), (S(updated, "source"), Money(updated, "obligation"), Money(updated, "localTotal")));
        Assert.Equal("820.00", Money((await PfBudgetAsync(c, next)).GetProperty("cards"), "realized"));
    }

    [Fact]
    public async Task WithoutABankTotalTheOpenInvoiceFallsBackToTheLocalProjectionAndTheLedgerReconcilesTheCard()
    {
        var c = await ArrangeOpenBillAsync("t136-local", openBankTotal: null);
        var next = Competence(NextClosing);

        var invoice = InvoiceOf(await OverviewAsync(c, next), next);
        Assert.Equal(("LocalProjection", "100.00"), (S(invoice, "source"), Money(invoice, "obligation")));
        Assert.Equal(JsonValueKind.Null, invoice.GetProperty("bankTotal").ValueKind);
        Assert.Equal("100.00", Money(((await PfBudgetAsync(c, next)).GetProperty("cards")), "committed"));   // sem total do banco: a compra pendente segue como compromisso local

        var group = (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-entries?competence={next}&scope=pf&groupBy=card&pageSize=100")).GetProperty("groups").EnumerateArray()
            .Single(g => S(g, "kind") == "Card");
        var reconciliation = group.GetProperty("invoice");
        Assert.Equal(("Open", "LocalProjection", "100.00", "100.00", "0.00"), (S(reconciliation, "phase"), S(reconciliation, "source"), S(reconciliation, "budgetTotal"), S(reconciliation, "identifiedTotal"), S(reconciliation, "difference")));
    }
}

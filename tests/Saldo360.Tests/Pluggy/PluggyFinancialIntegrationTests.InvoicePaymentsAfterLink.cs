using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// FAT-01: o pagamento bancário já importado (como saída comum) é reconhecido como pagamento da fatura quando a fatura do banco chega depois, sem nova saída e sem segundo consumo.
/// Reproduz a ordem vista em produção: conta bancária sincronizada primeiro, cartão vinculado depois e a fatura do banco só na sincronização seguinte.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private async Task<(int Transactions, int Payments, string RealizedExpense)> PaymentStateAsync(Ctx c)
    {
        await using var db = Db();
        var calendar = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={D(40)}&through={D(0)}");
        return ((await CoreAsync(c.Workspace)).Count, await db.FinancialCreditCardInvoicePayments.CountAsync(), calendar.GetProperty("summary").GetProperty("realizedExpense").GetString()!);
    }

    [Fact]
    public async Task ABankPaymentImportedBeforeTheCardAndItsBillIsRecognizedLaterWithoutANewOutflowOrASecondConsumption()
    {
        var c = await ArrangeAsync("fat01-order", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("pay-bank", BankA, 10, -230m, operation: "PIX", description: "DEBITO AUTOMATICO FATURA", category: null), Tx("other", BankA, 4, -12m)];
        var bankLink = await LinkBankAsync(c);
        await SyncAsync(c);
        var beforeCard = await PaymentStateAsync(c);
        Assert.Equal((2, 0), (beforeCard.Transactions, beforeCard.Payments)); // o débito é, por enquanto, uma saída comum

        // O cartão é vinculado depois e o banco ainda não devolveu nenhuma fatura (como em produção, logo após o vínculo).
        SeedCard(Tx("pay-card", CardA, 10, 230m, operation: "PAGAMENTO_FATURA", description: "PAGAMENTO FATURA", type: "CREDIT", category: null));
        Pluggy.BillsByAccount[CardA] = [];
        var cardLink = await LinkCardAsync(c, Today.AddDays(-15).Day, Today.AddDays(-8).Day, payer: bankLink.GetProperty("link").GetProperty("financialAccountId").GetGuid());
        await SyncAsync(c);
        var withoutBills = await PaymentStateAsync(c);
        Assert.Equal(0, withoutBills.Payments); // sem fatura do banco, nada é quitado nem inventado
        Assert.Equal(beforeCard.RealizedExpense, withoutBills.RealizedExpense);
        await using (var db = Db()) Assert.Contains(await db.PluggyPostings.AsNoTracking().Where(p => p.State == PluggyPostingState.NeedsReview).Select(p => p.Reason).ToListAsync(), reason => reason is "NoInvoiceForPayment" or "CreditWithoutBillReference");

        // A fatura do banco chega na próxima sincronização.
        SeedBill(230m);
        await SyncAsync(c);
        var recognized = await PaymentStateAsync(c);

        Assert.Equal(1, recognized.Payments);
        Assert.Equal((beforeCard.Transactions, beforeCard.RealizedExpense), (recognized.Transactions, recognized.RealizedExpense)); // a saída já registrada foi preservada, sem outra
        var cardId = cardLink.GetProperty("link").GetProperty("creditCardId").GetGuid();
        var invoices = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}/invoices?from={Today.AddMonths(-3):yyyy-MM}&through={Today.AddMonths(3):yyyy-MM}");
        var invoice = invoices.EnumerateArray().Single();
        Assert.Equal(("230.00", "230.00", "0.00", "Paid"), (invoice.GetProperty("totalAmount").GetString(), invoice.GetProperty("paidAmount").GetString(), invoice.GetProperty("remainingAmount").GetString(), invoice.GetProperty("paymentStatus").GetString()));

        await SyncAsync(c); // repetir não duplica
        Assert.Equal(recognized, await PaymentStateAsync(c));
    }
}

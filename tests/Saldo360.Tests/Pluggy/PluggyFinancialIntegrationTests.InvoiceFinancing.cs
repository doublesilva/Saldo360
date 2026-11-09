using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.Pluggy;

// T13.3 (item 2): parcelamento de saldo de fatura vindo da Pluggy. Evidência da série → compra marcada como InvoiceFinancing (migration), fora do consumo do orçamento,
// dentro da obrigação da fatura e do caixa, visível no Razão sem impacto no orçamento. Sem confirmação, continua compra comum.
public sealed partial class PluggyFinancialIntegrationTests
{
    private const string FinancingCategory = "Credit card payment";

    private PluggyTransaction FinancingTx(string id, int number, DateOnly date, string billId, string status = "POSTED") =>
        CardTx(id, $"PARCELAMEN FATURA {number:00}/03", 600m, date, billId, number, 3, status) with { Category = FinancingCategory };

    private async Task<(Ctx C, Guid Card)> ArrangeFinancingAsync(string user, bool confirmedSeries)
    {
        var c = await ArrangeAsync(user, seed: false);
        var closings = Enumerable.Range(0, 4).Select(i => LatestClosing.AddMonths(-i)).ToList();
        Pluggy.BillsByAccount[CardA] = closings.Select((closing, i) => TotalBill($"bill-{i}", closing, i == 0 ? 830m : 100m)).ToList();   // 830 = 150 + 80 + 600
        var transactions = new List<PluggyTransaction>
        {
            CardTx("c1", "LOJA", 150m, LatestClosing.AddDays(-12), "bill-0"), CardTx("c2", "TV", 80m, LatestClosing.AddDays(-9), "bill-0"), CardTx("old", "ANTIGA", 100m, closings[1].AddDays(-3), "bill-1"),
            FinancingTx("f1", 1, LatestClosing.AddDays(-6), "bill-0"),
        };
        if (confirmedSeries)
        {
            transactions.Add(FinancingTx("f2", 2, NextClosing.AddDays(-3), "", "PENDING"));
            transactions.Add(FinancingTx("f3", 3, NextClosing.AddMonths(1).AddDays(-3), "", "PENDING"));
        }
        Pluggy.TransactionsByAccount[CardA] = transactions;
        Pluggy.TransactionsByAccount[BankA] = [];
        var bank = await LinkBankAsync(c);
        var card = await LinkCardAsync(c, ClosingDay, DueDay);
        var cardId = card.GetProperty("link").GetProperty("creditCardId").GetGuid();
        await SetPayerAsync(c, cardId, bank.GetProperty("link").GetProperty("financialAccountId").GetGuid());
        await SyncAsync(c);
        return (c, cardId);
    }

    private async Task<JsonElement> PfBudgetAsync(Ctx c, string competence) =>
        (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-dashboard/overview?competence={competence}&scope=all")).GetProperty("pfBudgetControl");

    private async Task<List<JsonElement>> LedgerAsync(Ctx c, string competence) =>
        (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-entries?competence={competence}&scope=pf&pageSize=100")).GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();

    [Fact]
    public async Task ASeriesWithAConfirmedScheduleIsMarkedFinancingAndStaysOutOfConsumptionButInsideTheObligationAndTheLedger()
    {
        var (c, cardId) = await ArrangeFinancingAsync("t133-financiamento", confirmedSeries: true);
        var current = Competence(LatestClosing); var next = Competence(NextClosing);

        await using var db = Db();
        var purchases = await db.FinancialCreditCardPurchases.AsNoTracking().ToListAsync();
        var financed = Assert.Single(purchases, p => p.OperationKind == CreditCardOperationKind.InvoiceFinancing);
        Assert.StartsWith("PARCELAMEN", financed.Description);
        Assert.All(purchases.Where(p => p.Id != financed.Id), p => Assert.Equal(CreditCardOperationKind.Normal, p.OperationKind));   // as demais compras não foram reclassificadas

        // Obrigação da fatura fechada pelo banco: total integral (830), inclusive o financiamento.
        var closed = InvoiceOf(await OverviewAsync(c, current), current);
        Assert.Equal("830.00", Money(closed, "obligation"));
        // Orçamento PF: o consumo do cartão no mês só tem as compras comuns (150 + 80); os 600 não consomem de novo.
        var budget = await PfBudgetAsync(c, current);
        Assert.Equal("230.00", Money(budget.GetProperty("cards"), "realized"));
        // Mês seguinte: a parcela 2/3 é obrigação (600) mas não é comprometido do orçamento.
        var nextInvoice = InvoiceOf(await OverviewAsync(c, next), next);
        Assert.Equal("600.00", Money(nextInvoice, "obligation"));
        Assert.Equal("0.00", Money((await PfBudgetAsync(c, next)).GetProperty("cards"), "committed"));

        // Razão: aparece, sem impacto no orçamento, com a ressalva de composição desconhecida.
        var line = (await LedgerAsync(c, current)).Single(e => e.GetProperty("origin").GetString() == "InvoiceFinancing");
        Assert.Equal(("None", "InvoiceFinancing", "600.00"), (line.GetProperty("budgetImpact").GetString(), line.GetProperty("stage").GetString(), line.GetProperty("amount").GetString()));
        Assert.Contains("PrincipalInterestUnknown", line.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        var projected = (await LedgerAsync(c, next)).Single(e => e.GetProperty("origin").GetString() == "InvoiceFinancing");
        Assert.Equal(("CardProjected", "None", "600.00"), (projected.GetProperty("source").GetString(), projected.GetProperty("budgetImpact").GetString(), projected.GetProperty("amount").GetString()));

        // Reprocessar não duplica nem reclassifica nada.
        await SyncAsync(c);
        await using var again = Db();
        Assert.Equal((purchases.Count, 1), (await again.FinancialCreditCardPurchases.CountAsync(), await again.FinancialCreditCardPurchases.CountAsync(p => p.OperationKind == CreditCardOperationKind.InvoiceFinancing)));
    }

    [Fact]
    public async Task ACandidateWithoutAConfirmedScheduleStaysAnOrdinaryPurchaseAndKeepsConsumingTheBudget()
    {
        var (c, _) = await ArrangeFinancingAsync("t133-candidata", confirmedSeries: false);
        var current = Competence(LatestClosing);

        await using var db = Db();
        Assert.DoesNotContain(await db.FinancialCreditCardPurchases.AsNoTracking().ToListAsync(), p => p.OperationKind == CreditCardOperationKind.InvoiceFinancing);   // uma parcela só: sem cronograma comprovado
        Assert.Equal("830.00", Money(budgetCards(await PfBudgetAsync(c, current)), "realized"));   // 150 + 80 + 600 continuam consumo: nada foi reclassificado em silêncio

        static JsonElement budgetCards(JsonElement budget) => budget.GetProperty("cards");
    }
}

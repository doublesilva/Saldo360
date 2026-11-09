using Saldo360.Application.Entries;

namespace Saldo360.Tests.Entries;

/// <summary>T13.2: precedência única entre fato efetivo, obrigação registrada, previsão e estimativa, e os estágios que não confundem compra lançada, fatura e pagamento. Sem banco.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Transactions")]
public sealed class FinancialEntryPrecedenceTests
{
    private static readonly Guid CardId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static FinancialEntryDto Entry(string id, string source, string description, string origin = "Manual", string status = "Realized", string type = "Expense", Guid? card = null, string competence = "2026-12") =>
        new(id, source, competence, new DateOnly(2026, 12, 2), "Invoice", description, type, "Out", status, "10.00", null, null, card is { } c ? new EntryReference(c, "Cartão") : null, null, null, origin,
            "Consumes", "None", null, null, null, null, null, competence, [], []);

    [Fact]
    public void TwoLegitimateChargesWithTheSameNameAreBothKeptAndOnlyTheSameIdentityCollapses()
    {
        var registered = Entry("CardInstallment:1", "CardPurchase", "Streaming", "CardInstallment", card: CardId);
        var first = Entry($"CardProjected:{CardId}:{Guid.NewGuid()}:1", "CardProjected", "Streaming", "ManualRecurring", "Pending", card: CardId);
        var second = Entry($"CardProjected:{CardId}:{Guid.NewGuid()}:1", "CardProjected", "Streaming", "ManualRecurring", "Pending", card: CardId);

        var resolved = FinancialEntryPrecedence.Resolve([registered, first, second, first]);

        Assert.Equal(new[] { registered.Id, first.Id, second.Id }.Order(StringComparer.Ordinal), resolved.Select(e => e.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheSameLogicalIdentityKeepsOnlyTheHighestPrecedenceEntry()
    {
        var planned = Entry("X:1", "Planned", "Aluguel", status: "Pending");
        var effective = Entry("X:1", "Transaction", "Aluguel");

        var only = Assert.Single(FinancialEntryPrecedence.Resolve([planned, effective]));

        Assert.Equal("Transaction", only.Source);
        Assert.Equal(1, FinancialEntryPrecedence.Rank(effective));
        Assert.True(FinancialEntryPrecedence.Rank(Entry("e", "CardProjected", "x", "EstimatedInstallment", "Pending")) > FinancialEntryPrecedence.Rank(Entry("k", "CardProjected", "x", "KnownInstallment", "Pending")));
    }

    [Theory]
    [InlineData("Transaction", "Manual", "Realized", FinancialEntryPrecedence.Effective)]
    [InlineData("Transaction", "CreditCardInvoicePayment", "Realized", FinancialEntryPrecedence.InvoicePayment)]
    [InlineData("Transaction", "DebtInstallmentPayment", "Realized", FinancialEntryPrecedence.DebtPayment)]
    [InlineData("CardPurchase", "CardInstallment", "Realized", FinancialEntryPrecedence.CardLaunched)]
    [InlineData("CardProjected", "KnownInstallment", "Pending", FinancialEntryPrecedence.CardForecast)]
    [InlineData("CardProjected", "EstimatedInstallment", "Pending", FinancialEntryPrecedence.CardEstimated)]
    [InlineData("CardProjected", "ManualRecurring", "Pending", FinancialEntryPrecedence.CardForecast)]
    [InlineData("Planned", "Manual", "Overdue", FinancialEntryPrecedence.Planned)]
    [InlineData("Transaction", "Manual", "Cancelled", FinancialEntryPrecedence.Cancelled)]
    public void TheStageSeparatesCardPurchaseForecastEstimateAndPaymentWithoutSayingTheInvoiceWasPaid(string source, string origin, string status, string stage) =>
        Assert.Equal(stage, FinancialEntryPrecedence.Stage(Entry("s", source, "x", origin, status)));
}

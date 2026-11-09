using Saldo360.Application.Entries;

namespace Saldo360.Tests.Entries;

/// <summary>T13.4: agrupamento por cartão/conta e totais por grupo (puro, sem banco). A soma é das linhas, em decimal, e nunca duplica nem trata transferência/pagamento como consumo.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Transactions")]
public sealed class FinancialEntryGroupingTests
{
    private static readonly Guid CardA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CardB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Pf = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Pj = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private static FinancialEntryDto Entry(string id, string type, string amount, Guid? card = null, Guid? account = null, string status = "Realized", string budget = "Consumes", string origin = "Manual",
        string source = "Transaction", string cardName = "Cartão", string accountName = "Conta", string? accountScope = "PF") =>
        new(id, source, "2026-11", new DateOnly(2026, 11, 5), "Invoice", "Compra", type, type == "Income" ? "In" : "Out", status, amount,
            account is { } a ? new EntryReference(a, accountName, accountScope) : null, null, card is { } c ? new EntryReference(c, cardName) : null, null, null, origin, budget, "None",
            null, null, null, null, null, null, [], []);

    // ---- identidade do grupo ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ByCardAMovementWithACardGoesToTheCardByIdAndABankMovementToItsAccountAndTheRestIsUnidentified()
    {
        var card = FinancialEntryGrouping.KeyOf(Entry("1", "Expense", "10.00", CardA, Pf, cardName: "Nubank"), FinancialEntryGrouping.ByCard)!;
        var bank = FinancialEntryGrouping.KeyOf(Entry("2", "Expense", "10.00", null, Pf, accountName: "Itaú PF"), FinancialEntryGrouping.ByCard)!;
        var none = FinancialEntryGrouping.KeyOf(Entry("3", "Expense", "10.00"), FinancialEntryGrouping.ByCard)!;

        Assert.Equal(($"card:{CardA}", "Card", "Nubank"), (card.Key, card.Kind, card.Name));
        Assert.Equal(($"account:{Pf}", "Account", "Itaú PF"), (bank.Key, bank.Kind, bank.Name));
        Assert.Equal(("none", "Sem conta identificada"), (none.Key, none.Name));
        Assert.Null(FinancialEntryGrouping.KeyOf(Entry("4", "Expense", "10.00", CardA, Pf), FinancialEntryGrouping.ByNone));
    }

    [Fact]
    public void ByAccountTheCardPurchasesGoToThePayerAccountAndTwoCardsWithTheSameNameStaySeparateByIdWhenGroupedByCard()
    {
        var byAccount = FinancialEntryGrouping.KeyOf(Entry("1", "Expense", "10.00", CardA, Pf, accountName: "Itaú PF"), FinancialEntryGrouping.ByAccount)!;
        var first = FinancialEntryGrouping.KeyOf(Entry("2", "Expense", "10.00", CardA, Pf, cardName: "Nubank"), FinancialEntryGrouping.ByCard)!;
        var second = FinancialEntryGrouping.KeyOf(Entry("3", "Expense", "10.00", CardB, Pj, cardName: "Nubank"), FinancialEntryGrouping.ByCard)!;

        Assert.Equal($"account:{Pf}", byAccount.Key);
        Assert.NotEqual(first.Key, second.Key);   // o agrupamento é pelo ID do cartão, nunca pelo nome
    }

    [Fact]
    public void GroupsAreOrderedCardsThenAccountsThenUnidentifiedByNameAndTheOrderIsStable()
    {
        var keys = new[]
        {
            FinancialEntryGrouping.KeyOf(Entry("1", "Expense", "1.00", null, Pf, accountName: "Itaú PF"), "card")!,
            FinancialEntryGrouping.KeyOf(Entry("2", "Expense", "1.00"), "card")!,
            FinancialEntryGrouping.KeyOf(Entry("3", "Expense", "1.00", CardB, Pj, cardName: "Nubank PJ"), "card")!,
            FinancialEntryGrouping.KeyOf(Entry("4", "Expense", "1.00", CardA, Pf, cardName: "Credicard"), "card")!,
        };

        var order = FinancialEntryGrouping.GroupOrder(keys);

        Assert.Equal([$"card:{CardA}", $"card:{CardB}", $"account:{Pf}", "none"], order.OrderBy(o => o.Value).Select(o => o.Key));
        Assert.Equal(order, FinancialEntryGrouping.GroupOrder(keys.Reverse()));
    }

    // ---- totais ----------------------------------------------------------------------------------------------------------------------

    private static EntryGroupSummary Sum(params FinancialEntryDto[] entries) =>
        FinancialEntryGrouping.Summarize(new("g", "Card", CardA, "Cartão A", "PF", 0), entries, 0, 25);

    [Fact]
    public void OutflowInflowAndTheNetBudgetImpactAreComputedInDecimalWithRefundsKeepingTheirSign()
    {
        var summary = Sum(
            Entry("1", "Expense", "100.10", CardA), Entry("2", "Expense", "50.05", CardA),
            Entry("3", "Income", "20.00", CardA, budget: "Reduces"),                    // estorno/crédito de cartão: entrada que reduz o consumo
            Entry("4", "Expense", "30.00", CardA, status: "Pending", budget: "Planned"));  // previsão: não consome ainda

        Assert.Equal(("180.15", "20.00", "150.15", "20.00", "130.15", "30.00"), (summary.Outflow, summary.Inflow, summary.BudgetConsumed, summary.BudgetReduced, summary.BudgetNet, summary.BudgetPlanned));
    }

    [Fact]
    public void TransfersAreCountedButNeverSummedAsIncomeOrExpense()
    {
        var summary = Sum(Entry("1", "Transfer", "500.00", account: Pf, budget: "None"), Entry("2", "Expense", "10.00", account: Pf));

        Assert.Equal((2, 1, "10.00", "0.00"), (summary.Count, summary.TransferCount, summary.Outflow, summary.Inflow));
    }

    [Fact]
    public void InvoicePaymentsAreCashOnlyAndNeverBudgetConsumptionOrOutflowOfTheCardGroup()
    {
        var summary = Sum(Entry("1", "Payment", "300.00", account: Pf, budget: "None", origin: "CreditCardInvoicePayment"), Entry("2", "Expense", "80.00", CardA));

        Assert.Equal(("300.00", "80.00", "80.00"), (summary.PaymentsOut, summary.Outflow, summary.BudgetConsumed));
    }

    [Fact]
    public void InvoiceFinancingIsHighlightedButItsInstallmentsAreOutflowAndBudgetConsumptionLikeAnyOtherInstallment()
    {
        var summary = Sum(Entry("1", "Expense", "1814.73", CardA, origin: "InvoiceFinancing"), Entry("2", "Expense", "50.00", CardA));

        Assert.Equal(("1864.73", "1814.73", "1864.73", "1864.73"), (summary.Outflow, summary.InvoiceFinancing, summary.BudgetConsumed, summary.BudgetNet));   // o destaque está dentro, nunca somado de novo
    }

    [Fact]
    public void CancelledEntriesAreCountedSeparatelyAndLeaveEverySumUntouched()
    {
        var summary = Sum(Entry("1", "Expense", "100.00", CardA), Entry("2", "Expense", "999.00", CardA, status: "Cancelled"));

        Assert.Equal((2, 1, 1, "100.00", "100.00"), (summary.Count, summary.ActiveCount, summary.CancelledCount, summary.Outflow, summary.BudgetConsumed));
    }

    [Fact]
    public void AnEmptyGroupHasZeroedTotals()
    {
        var summary = Sum();

        Assert.Equal((0, "0.00", "0.00", "0.00"), (summary.Count, summary.Outflow, summary.Inflow, summary.BudgetNet));
    }

    [Theory]
    [InlineData(0, 25, 1)]
    [InlineData(24, 25, 1)]
    [InlineData(25, 25, 2)]
    [InlineData(70, 25, 3)]
    public void TheStartingPageOfAGroupLocatesItsFirstItemInTheOrderedList(int firstIndex, int pageSize, int expectedPage) =>
        Assert.Equal(expectedPage, FinancialEntryGrouping.Summarize(new("g", "Card", CardA, "A", null, 0), [Entry("1", "Expense", "1.00", CardA)], firstIndex, pageSize).StartsOnPage);
}

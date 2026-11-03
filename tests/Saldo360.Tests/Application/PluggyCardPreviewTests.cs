using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
public sealed class PluggyCardPreviewTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);
    private static DateTimeOffset At(string date) => DateTimeOffset.Parse(date + "T00:00:00Z");

    private static PluggyAccount Account(decimal? limit = 1000m, decimal? available = 400m) =>
        new("acc", "item", "CREDIT", "CREDIT_CARD", "Cartao X", null, "5555444433332222", 600m, "BRL", null, null,
            new("GOLD", "VISA", null, At("2026-10-13"), available, limit, 0m, null, null, null));

    private static PluggyBill Bill(string due, decimal total, string? closing = null) =>
        new("b-" + due, At(due), total, "BRL", 10m, false, [], closing is null ? null : At(closing), []);

    private static PluggyTransaction Tx(string date, decimal amount, string status = "POSTED", int? installments = null) =>
        new("t-" + date + amount, "acc", At(date), "DESCRICAO SECRETA", null, amount, null, "BRL", "DEBIT", status, null, null, null, null, null, null,
            installments is null ? null : new(1, installments, null, null, null, null, null, null), null);

    [Fact]
    public void SummarizesLimitUsedAmountBillsAndUpcomingChargesByMonth()
    {
        var view = PluggyCardPreview.Build(Account(), [Bill("2026-09-10", 100m), Bill("2026-10-13", 250m)],
            [Tx("2026-10-20", 50m), Tx("2026-11-10", 100m, "PENDING", 4), Tx("2026-11-25", 20m, "PENDING"), Tx("2026-12-10", 100m, "PENDING", 4), Tx("2026-12-11", -30m, "PENDING")], false, Today);

        Assert.Equal(("1000.00", "400.00", "600.00", 13), (view.CreditLimit, view.AvailableLimit, view.UsedLimit, view.DueDay));
        Assert.Equal((2, "250.00"), (view.BillsRead, view.LastBillTotal));
        Assert.Equal([("2026-11", 2, 1, "120.00"), ("2026-12", 1, 1, "100.00")], view.Upcoming.Select(m => (m.Month, m.Entries, m.Installments, m.Amount)));
        Assert.Equal((5, 1, 4, 2), (view.TransactionsRead, view.Posted, view.Pending, view.WithInstallments));
    }

    [Fact]
    public void FlagsWhatIsMissingInsteadOfPresumingIt()
    {
        var view = PluggyCardPreview.Build(Account(), [Bill("2026-10-13", 785.3255m), Bill("2026-09-14", -2.42m)], [], true, Today);
        Assert.Equal(["AwaitingBankBill", "NoClosingDate", "FractionalBillCents", "CreditBill", "TransactionsTruncated"], view.Flags);

        var none = PluggyCardPreview.Build(Account(null, null), [], [], false, Today);
        Assert.Equal(["NoBillsReturned"], none.Flags);
        Assert.Null(none.CreditLimit);
        Assert.Null(none.UsedLimit);
        Assert.Empty(none.Upcoming);
    }

    [Fact]
    public void DoesNotExposeDescriptionsOrCardNumbers()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(PluggyCardPreview.Build(Account(), [Bill("2026-11-13", 10m, "2026-11-06")], [Tx("2026-11-10", 5m, "PENDING")], false, Today));
        Assert.DoesNotContain("SECRETA", json);
        Assert.DoesNotContain("5555444433332222", json);
    }

    private static PluggyTransaction Installment(string date, decimal amount, int number, int total, string description, string? purchase = null, string? billId = null) =>
        new("i-" + date + description, "acc", At(date), description, null, amount, null, "BRL", "DEBIT", At(date).Date > At("2026-11-03").Date ? "PENDING" : "POSTED", null, null, null, null, null, null,
            new(number, total, null, purchase is null ? null : At(purchase), billId, null, null, null), null);

    [Fact]
    public void TheCutoffReportsWhatStaysOutAndInstallmentsOfOlderPurchasesInsideTheWindow()
    {
        var cutoff = new DateOnly(2026, 10, 1);
        var transactions = new[]
        {
            Installment("2026-08-10", 100m, 1, 4, "SOFA 01/04", purchase: "2026-08-10"),   // antes do corte: fora
            Installment("2026-09-10", 100m, 2, 4, "SOFA 02/04", purchase: "2026-08-10"),   // antes do corte: fora
            Installment("2026-10-10", 100m, 3, 4, "SOFA 03/04", purchase: "2026-08-10"),   // dentro da janela
            Installment("2026-11-10", 100m, 4, 4, "SOFA 04/04", purchase: "2026-08-10"),   // dentro da janela
            Installment("2026-10-20", 50m, 1, 2, "TV 01/02", purchase: "2026-10-20"),      // compra nova: não conta como anterior
            Tx("2026-10-25", 30m),
        };

        var view = PluggyCardPreview.Build(Account(), [], transactions, false, Today, cutoff);

        var result = view.Cutoff!;
        Assert.Equal((cutoff, 2, 4), (result.ImportFrom, result.ExcludedTransactions, result.ImportedTransactions));
        Assert.Equal((1, 2), (result.InstallmentSeriesStartedBefore, result.InstallmentsInsideWindow));
        Assert.Contains("HistoryBeforeCutoffExcluded", view.Flags);
        Assert.Contains("InstallmentsFromBeforeCutoff", view.Flags);
    }

    [Fact]
    public void ABillThatMixesPurchasesBeforeAndAfterTheCutoffIsReportedWithBothParts()
    {
        var cutoff = new DateOnly(2026, 10, 1);
        var bill = new PluggyBill("bill-oct", At("2026-10-13"), 300m, "BRL", null, null, [], null, []);
        var transactions = new[]
        {
            Installment("2026-09-20", 120m, 1, 1, "COMPRA A", billId: "bill-oct"),
            Installment("2026-10-02", 180m, 1, 1, "COMPRA B", billId: "bill-oct"),
        };

        var view = PluggyCardPreview.Build(Account(), [bill], transactions, false, Today, cutoff);

        var spanning = Assert.Single(view.Cutoff!.BillsSpanningCutoff);
        Assert.Equal((new DateOnly(2026, 10, 13), "300.00", "180.00", "120.00"), (spanning.DueDate, spanning.BankTotal, spanning.FromCutoff, spanning.BeforeCutoff));
        Assert.Contains("BillsSpanCutoff", view.Flags);
    }

    [Fact]
    public void WithoutACutoffNothingIsReportedAboutIt()
    {
        var view = PluggyCardPreview.Build(Account(), [], [Tx("2026-09-01", 10m)], false, Today);
        Assert.Null(view.Cutoff);
        Assert.DoesNotContain("HistoryBeforeCutoffExcluded", view.Flags);
    }
}

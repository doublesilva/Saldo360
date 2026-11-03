using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.Application;

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
}

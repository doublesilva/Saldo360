using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
[Trait("Profile", "FAST")]
public sealed class FinancialCreditCardPurchaseTests
{
    [Theory]
    [InlineData("2026-10-04", "2026-10", "2026-10-10")]
    [InlineData("2026-10-05", "2026-10", "2026-10-10")]
    [InlineData("2026-10-08", "2026-11", "2026-11-10")]
    public void BlackClosingIsInclusiveAndMonthlyAmountIsNotOriginalTotal(string date, string competence, string due)
    {
        var purchaseDate = DateOnly.Parse(date);
        var cycle = CreditCardInvoiceCalendarCalculator.ForInstallment(purchaseDate, 1, 5, 10);
        Assert.Equal(competence, cycle.Competence); Assert.Equal(DateOnly.Parse(due), cycle.DueDate);
        Assert.Equal(DateOnly.Parse(competence + "-01").AddMonths(1).ToString("yyyy-MM"), CreditCardInvoiceCalendarCalculator.ForInstallment(purchaseDate, 2, 5, 10).Competence);
        var p = FinancialCreditCardPurchase.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Synthetic", purchaseDate, 999m, 3, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.All(p.Installments, i => Assert.Equal(333m, i.Amount));
        Assert.Equal(999m, p.TotalAmount);
    }
    [Theory]
    [InlineData("100.00", 3, "33.34", "33.33", "33.33")]
    [InlineData("0.10", 3, "0.04", "0.03", "0.03")]
    public void DistributesCentsDeterministically(string text, int count, string first, string second, string third)
    {
        var total = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var details = CreditCardPurchaseDetails.Validate("Compra", total, count, out var description);
        var purchase = FinancialCreditCardPurchase.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), description!, DateOnly.Parse("2026-10-15"), total, count, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Null(details);
        Assert.Equal([first, second, third], purchase.Installments.Select(item => item.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(total, purchase.Installments.Sum(item => item.Amount));
    }

    [Fact]
    public void Distributes999_99InTwelveInstallmentsExactly()
    {
        var total = 999.99m;
        var purchase = FinancialCreditCardPurchase.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Compra", new DateOnly(2026, 10, 15), total, 12, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(12, purchase.Installments.Count);
        Assert.Equal(total, purchase.Installments.Sum(item => item.Amount));
        Assert.Equal(83.34m, purchase.Installments.First().Amount);
        Assert.All(purchase.Installments.Take(3), installment => Assert.Equal(83.34m, installment.Amount));
        Assert.All(purchase.Installments.Skip(3), installment => Assert.Equal(83.33m, installment.Amount));
    }

    [Theory]
    [InlineData("0.00", 1)]
    [InlineData("-1.00", 1)]
    [InlineData("10.001", 1)]
    [InlineData("1.00", 0)]
    [InlineData("1.00", 121)]
    [InlineData("0.10", 11)]
    public void RejectsInvalidPurchaseValues(string text, int count)
    {
        var error = CreditCardPurchaseDetails.Validate("Compra", decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), count, out _);
        Assert.NotNull(error);
    }

    [Fact]
    public void CancellationCancelsEveryInstallmentAndPreservesHistory()
    {
        var purchase = FinancialCreditCardPurchase.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Compra", new DateOnly(2026, 10, 15), 100m, 3, Guid.NewGuid(), DateTimeOffset.UtcNow);
        purchase.Cancel(Guid.NewGuid(), DateTimeOffset.UtcNow);
        purchase.Cancel(Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(CreditCardPurchaseStatus.Cancelled, purchase.Status);
        Assert.All(purchase.Installments, item => Assert.Equal(CreditCardInstallmentStatus.Cancelled, item.Status));
        Assert.Equal(100m, purchase.TotalAmount);
    }

    [Fact]
    public void OngoingPurchaseKeepsOriginalRoundingAndRealNumbersWithoutRecreatingPaidRows()
    {
        var p = FinancialCreditCardPurchase.CreateOngoing(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Compra", new DateOnly(2026, 3, 26), 100m, 3, 1, new DateOnly(2026, 11, 1), Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(new[] { 2, 3 }, p.Installments.Select(i => i.InstallmentNumber));
        Assert.Equal(new[] { 33.33m, 33.33m }, p.Installments.Select(i => i.Amount));
        Assert.Equal(100m, p.TotalAmount);
        Assert.Equal(66.66m, p.Installments.Sum(i => i.Amount));
    }

    [Fact]
    public void CorrectingDateOrDescriptionDoesNotRewriteAmountsOfPaidHistory()
    {
        var user = Guid.NewGuid(); var category = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var p = FinancialCreditCardPurchase.CreateOngoing(Guid.NewGuid(), Guid.NewGuid(), category, "Compra", new DateOnly(2026, 3, 26), 3207.10m, 10, 6, new DateOnly(2026, 10, 1), user, now);
        var ids = p.Installments.Select(i => i.Id).ToArray();
        p.UpdateSchedule(category, "Rótulo corrigido", new DateOnly(2026, 3, 25), 3207.10m, 10, 6, new DateOnly(2026, 11, 1), user, now);
        Assert.Equal(ids, p.Installments.Select(i => i.Id));
        Assert.All(p.Installments, i => Assert.Equal(320.71m, i.Amount));
        Assert.Throws<InvalidOperationException>(() => p.UpdateSchedule(category, "Outro total", p.PurchaseDate, 4000m, 10, 6, p.FirstPendingCompetence, user, now));
        Assert.Throws<InvalidOperationException>(() => p.UpdateSchedule(category, "Outro histórico", p.PurchaseDate, 3207.10m, 10, 5, p.FirstPendingCompetence, user, now));
        Assert.Equal(3207.10m, p.TotalAmount);
    }

    [Theory]
    [InlineData(-1, "2026-11-01")]
    [InlineData(4, "2026-11-01")]
    [InlineData(2, null)]
    [InlineData(0, "2026-11-02")]
    public void RejectsInvalidHistoricalSchedule(int paid, string? first)
    {
        Assert.Throws<ArgumentException>(() => FinancialCreditCardPurchase.CreateOngoing(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Compra", new DateOnly(2026, 3, 26), 1000m, 4, paid, first is null ? null : DateOnly.Parse(first), Guid.NewGuid(), DateTimeOffset.UtcNow));
    }
}

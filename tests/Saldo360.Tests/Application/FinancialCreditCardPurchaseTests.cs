using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.Application;

public sealed class FinancialCreditCardPurchaseTests
{
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
}

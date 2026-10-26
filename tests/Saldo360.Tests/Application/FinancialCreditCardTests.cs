using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
[Trait("Profile", "FAST")]
public sealed class FinancialCreditCardTests
{
    [Fact]
    public void GmailQueryIsOptionalTrimmedEditableAndClearable()
    {
        var details = CreditCardDetails.TryCreate("Card", "Bank", "1234", null, 100m, 5, 10, null, out var errors, "  from:bank.example filename:pdf  ")!;
        Assert.Empty(errors); Assert.Equal("from:bank.example filename:pdf", details.GmailQuery);
        var card = FinancialCreditCard.Create(Guid.NewGuid(), details, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(details.GmailQuery, card.GmailQuery);
        var cleared = CreditCardDetails.TryCreate("Card", "Bank", "1234", null, 100m, 5, 10, null, out _, "  ")!;
        card.Update(cleared, Guid.NewGuid(), DateTimeOffset.UtcNow); Assert.Null(card.GmailQuery);
        Assert.Null(CreditCardDetails.TryCreate("Card", "Bank", "1234", null, 100m, 5, 10, null, out var invalid, new string('x', 2049)));
        Assert.Contains("gmailQuery", invalid.Keys);
    }
    [Fact]
    public void ValidatesZeroLimitAndBoundaryDays()
    {
        var details = CreditCardDetails.TryCreate("Principal", "Banco", null, 0m, 1, 31, null, out var errors);

        Assert.NotNull(details);
        Assert.Empty(errors);
        Assert.Equal(0m, details!.CreditLimit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void RejectsInvalidClosingDay(int day)
    {
        var details = CreditCardDetails.TryCreate("Principal", "Banco", null, 100m, day, 10, null, out var errors);

        Assert.Null(details);
        Assert.Contains("closingDay", errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void RejectsInvalidDueDay(int day)
    {
        var details = CreditCardDetails.TryCreate("Principal", "Banco", null, 100m, 10, day, null, out var errors);

        Assert.Null(details);
        Assert.Contains("dueDay", errors.Keys);
    }

    [Fact]
    public void RejectsNegativeAndOverPreciseLimit()
    {
        Assert.Null(CreditCardDetails.TryCreate("Principal", "Banco", null, -1m, 10, 20, null, out var negative));
        Assert.Contains("creditLimit", negative.Keys);
        Assert.Null(CreditCardDetails.TryCreate("Principal", "Banco", null, 10.001m, 10, 20, null, out var precise));
        Assert.Contains("creditLimit", precise.Keys);
    }

    [Fact]
    public void AllowsDaysThatMayNotExistInEveryMonth()
    {
        var details = CreditCardDetails.TryCreate("Principal", "Banco", null, 1234.56m, 29, 30, null, out var errors);

        Assert.NotNull(details);
        Assert.Empty(errors);
        Assert.Equal(29, details!.ClosingDay);
        Assert.Equal(30, details.DueDay);
    }

    [Fact]
    public void ArchivesWithoutChangingHistoricalData()
    {
        var details = CreditCardDetails.TryCreate("Principal", "Banco", null, 1234.56m, 10, 20, null, out _)!;
        var card = FinancialCreditCard.Create(Guid.NewGuid(), details, Guid.NewGuid(), DateTimeOffset.UtcNow);
        card.Archive(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.True(card.IsArchived);
        Assert.Equal(1234.56m, card.CreditLimit);
        Assert.Equal(10, card.ClosingDay);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("0007")]
    public void AcceptsExactLastFourDigits(string digits)
    {
        var details = CreditCardDetails.TryCreate("Principal", "Banco", digits, null, 100m, 10, 20, null, out var errors);
        Assert.NotNull(details);
        Assert.Empty(errors);
        Assert.Equal(digits, details!.LastFourDigits);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("12A4")]
    [InlineData("１２３４")]
    public void RejectsInvalidLastFourDigits(string digits)
    {
        Assert.Null(CreditCardDetails.TryCreate("Principal", "Banco", digits, null, 100m, 10, 20, null, out var errors));
        Assert.Contains("lastFourDigits", errors.Keys);
    }
}

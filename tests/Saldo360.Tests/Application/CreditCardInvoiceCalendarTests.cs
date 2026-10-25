using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
public sealed class CreditCardInvoiceCalendarTests
{
    [Fact]
    public void Purchase_on_closing_day_belongs_to_that_cycle()
    {
        var c = CreditCardInvoiceCalendarCalculator.ForInstallment(new DateOnly(2026, 10, 15), 1, 15, 10);
        Assert.Equal(new DateOnly(2026, 10, 15), c.ClosingDate);
        Assert.Equal(new DateOnly(2026, 9, 16), c.CycleStartDate);
        Assert.Equal(new DateOnly(2026, 11, 10), c.DueDate);
        Assert.Equal("2026-11", c.Competence);
    }

    [Fact]
    public void Purchase_after_closing_day_moves_to_next_cycle()
    {
        var c = CreditCardInvoiceCalendarCalculator.ForInstallment(new DateOnly(2026, 10, 16), 1, 15, 10);
        Assert.Equal(new DateOnly(2026, 11, 15), c.ClosingDate);
        Assert.Equal(new DateOnly(2026, 10, 16), c.CycleStartDate);
    }

    [Fact]
    public void Closing_and_due_days_are_clamped_and_due_is_strictly_after_close()
    {
        var c = CreditCardInvoiceCalendarCalculator.ForInstallment(new DateOnly(2026, 1, 31), 1, 31, 31);
        Assert.Equal(new DateOnly(2026, 1, 31), c.ClosingDate);
        Assert.Equal(new DateOnly(2026, 2, 28), c.DueDate);
        var leap = CreditCardInvoiceCalendarCalculator.ForInstallment(new DateOnly(2028, 2, 29), 1, 31, 31);
        Assert.Equal(new DateOnly(2028, 2, 29), leap.ClosingDate);
        Assert.Equal(new DateOnly(2028, 3, 31), leap.DueDate);
    }

    [Fact]
    public void Installments_cross_year_and_competence_follows_due_month()
    {
        var c = CreditCardInvoiceCalendarCalculator.ForInstallment(new DateOnly(2026, 12, 20), 2, 10, 5);
        Assert.Equal(new DateOnly(2027, 1, 11), c.CycleStartDate);
        Assert.Equal(new DateOnly(2027, 2, 10), c.ClosingDate);
        Assert.Equal("2027-03", c.Competence);
    }
}

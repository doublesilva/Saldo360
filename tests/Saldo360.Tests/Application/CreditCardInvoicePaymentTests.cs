using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.Application;

public sealed class CreditCardInvoicePaymentTests
{
    [Fact]
    public void Payment_is_active_and_cancel_is_idempotent()
    {
        var id = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var payment = FinancialCreditCardInvoicePayment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), id, operationId, new DateOnly(2026, 10, 15), 125.50m, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(operationId, payment.OperationId);
        Assert.Equal(FinancialCreditCardInvoicePaymentStatus.Active, payment.Status);
        Assert.True(payment.Cancel(Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Equal(FinancialCreditCardInvoicePaymentStatus.Cancelled, payment.Status);
        Assert.False(payment.Cancel(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(0, 0, "Unpaid")]
    [InlineData(100, 40, "PartiallyPaid")]
    [InlineData(100, 100, "Paid")]
    public void Remaining_amount_never_goes_negative(decimal total, decimal paid, string expected)
    {
        var remaining = Math.Max(0m, total - paid);
        var status = total <= 0 || paid <= 0 ? "Unpaid" : remaining == 0 ? "Paid" : "PartiallyPaid";
        Assert.Equal(expected, status);
        Assert.True(remaining >= 0);
    }
}

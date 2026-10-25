using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
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

}

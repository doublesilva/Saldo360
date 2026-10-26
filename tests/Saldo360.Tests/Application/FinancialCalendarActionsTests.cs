using Saldo360.Application.Calendar;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Transactions;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
[Trait("Area", "Planning")]
[Trait("Profile", "FAST")]
public sealed class FinancialCalendarActionsTests
{
    [Theory]
    [InlineData(FinancialCalendarSource.PlannedTransaction, FinancialCalendarStatus.Pending, FinancialTransactionOrigin.Manual, false, "Realize,Edit,Cancel")]
    [InlineData(FinancialCalendarSource.PlannedTransaction, FinancialCalendarStatus.Overdue, FinancialTransactionOrigin.Manual, false, "Realize,Edit,Cancel")]
    [InlineData(FinancialCalendarSource.PlannedTransaction, FinancialCalendarStatus.Realized, FinancialTransactionOrigin.Manual, false, "")]
    [InlineData(FinancialCalendarSource.Transaction, FinancialCalendarStatus.Realized, FinancialTransactionOrigin.PlannedRealization, true, "UndoRealization,ViewOrigin")]
    [InlineData(FinancialCalendarSource.Transaction, FinancialCalendarStatus.Realized, FinancialTransactionOrigin.Manual, false, "Edit,Cancel")]
    [InlineData(FinancialCalendarSource.Transaction, FinancialCalendarStatus.Realized, FinancialTransactionOrigin.CreditCardInvoicePayment, false, "")]
    [InlineData(FinancialCalendarSource.Transaction, FinancialCalendarStatus.Realized, FinancialTransactionOrigin.DebtInstallmentPayment, false, "")]
    [InlineData(FinancialCalendarSource.DebtInstallment, FinancialCalendarStatus.Pending, FinancialTransactionOrigin.DebtInstallmentPayment, false, "")]
    public void ActionsRespectSourceAndOrigin(FinancialCalendarSource source, FinancialCalendarStatus status,
        FinancialTransactionOrigin origin, bool linked, string expected)
    {
        var item = new FinancialCalendarItemDto(Guid.NewGuid(), source, status, CategoryType.Expense,
            10.05m, new DateOnly(2026, 10, 20), "Synthetic", null, null, "BRL",
            linked ? Guid.NewGuid() : null, origin);
        Assert.Equal(expected, string.Join(',', item.AllowedActions));
    }
}

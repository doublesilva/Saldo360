using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
public sealed class InvoiceAdjustmentValidationTests
{
    [Theory]
    [InlineData("PreviousBalance", "Debit", "type")]
    [InlineData("FinancedBalance", "Credit", "type")]
    [InlineData("999", "Debit", "type")]
    [InlineData("Credit", "999", "nature")]
    public async Task ManualContractDoesNotAcceptInternalBalanceTypesOrUndefinedEnums(string type, string nature, string field)
    {
        var service = new FinancialCreditCardInvoiceAdjustmentService(new ForbiddenRepository(), TimeProvider.System);
        var result = await service.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new(type, nature, "2026-09-02", "Synthetic adjustment", "1.00", "invalid-contract", null), default);
        Assert.Equal(InvoiceAdjustmentAccess.Invalid, result.Access);
        Assert.Equal(new[] { field }, result.Errors!.Keys);
    }

    // A rejected service input must not even reach persistence. Relational effects remain
    // covered by FinancialCreditCardInvoiceAdjustmentIntegrationTests on real PostgreSQL.
    private sealed class ForbiddenRepository : IFinancialCreditCardInvoiceAdjustmentRepository
    {
        public Task<InvoiceAdjustmentOperationResult> CreateAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid userId,
            FinancialCreditCardInvoiceAdjustmentInput input, DateTimeOffset now, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Invalid input reached persistence.");
        public Task<InvoiceAdjustmentOperationResult> CancelAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid adjustmentId,
            Guid userId, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FinancialCreditCardInvoiceAdjustmentDto>?> ListAsync(Guid workspaceId, Guid cardId, Guid invoiceId,
            CancellationToken ct) => throw new NotSupportedException();
    }
}

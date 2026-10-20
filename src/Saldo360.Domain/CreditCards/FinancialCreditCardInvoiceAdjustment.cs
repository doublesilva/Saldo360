using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public enum FinancialCreditCardInvoiceAdjustmentType { Fee, Credit, Refund }
public enum FinancialCreditCardInvoiceAdjustmentNature { Debit, Credit }
public enum FinancialCreditCardInvoiceAdjustmentStatus { Active, Cancelled }

public sealed class FinancialCreditCardInvoiceAdjustment : Entity
{
    public const int DescriptionMaxLength = 200;
    public const int ExternalIdMaxLength = 200;

    private FinancialCreditCardInvoiceAdjustment()
    {
        Description = string.Empty;
        ExternalId = string.Empty;
    }

    public Guid WorkspaceId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public Guid InvoiceId { get; private init; }
    public FinancialCreditCardInvoiceAdjustmentType Type { get; private init; }
    public FinancialCreditCardInvoiceAdjustmentNature Nature { get; private init; }
    public DateOnly TransactionDate { get; private init; }
    public string Description { get; private init; }
    public decimal Amount { get; private init; }
    public string ExternalId { get; private init; }
    public Guid? CategoryId { get; private init; }
    public FinancialCreditCardInvoiceAdjustmentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }

    public decimal SignedAmount => Nature == FinancialCreditCardInvoiceAdjustmentNature.Debit ? Amount : -Amount;

    public static FinancialCreditCardInvoiceAdjustment Create(
        Guid workspaceId, Guid creditCardId, Guid invoiceId,
        FinancialCreditCardInvoiceAdjustmentType type, FinancialCreditCardInvoiceAdjustmentNature nature,
        DateOnly transactionDate, string description, decimal amount, string externalId,
        Guid? categoryId, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreditCardId = creditCardId, InvoiceId = invoiceId,
        Type = type, Nature = nature, TransactionDate = transactionDate, Description = description,
        Amount = amount, ExternalId = externalId, CategoryId = categoryId,
        Status = FinancialCreditCardInvoiceAdjustmentStatus.Active,
        CreatedAt = now, CreatedByUserId = userId,
    };

    public void Cancel(Guid userId, DateTimeOffset now)
    {
        if (Status == FinancialCreditCardInvoiceAdjustmentStatus.Cancelled) return;
        Status = FinancialCreditCardInvoiceAdjustmentStatus.Cancelled;
        CancelledAt = now;
        CancelledByUserId = userId;
    }
}

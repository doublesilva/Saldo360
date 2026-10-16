using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public enum FinancialCreditCardImportedInstallmentStatus { Active, Cancelled }

public sealed class FinancialCreditCardImportedInstallment : Entity
{
    private FinancialCreditCardImportedInstallment() { Description = string.Empty; }
    public Guid FamilyId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public Guid ImportId { get; private init; }
    public int RowNumber { get; private init; }
    public string? ExternalId { get; private init; }
    public Guid? LinkedInstallmentId { get; private init; }
    public Guid InvoiceId { get; private init; }
    public DateOnly TransactionDate { get; private init; }
    public string Description { get; private init; }
    public decimal Amount { get; private init; }
    public int InstallmentNumber { get; private init; }
    public int InstallmentCount { get; private init; }
    public Guid? CategoryId { get; private init; }
    public bool IsHistorical { get; private init; }
    public FinancialCreditCardImportedInstallmentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }

    public static FinancialCreditCardImportedInstallment Create(
        Guid familyId, Guid cardId, Guid importId, int rowNumber, string? externalId, Guid? linkedInstallmentId,
        Guid invoiceId, DateOnly transactionDate, string description, decimal amount, int installmentNumber,
        int installmentCount, Guid? categoryId, bool historical, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), FamilyId = familyId, CreditCardId = cardId, ImportId = importId, RowNumber = rowNumber,
        ExternalId = externalId, LinkedInstallmentId = linkedInstallmentId, InvoiceId = invoiceId,
        TransactionDate = transactionDate, Description = description, Amount = amount,
        InstallmentNumber = installmentNumber, InstallmentCount = installmentCount, CategoryId = categoryId,
        IsHistorical = historical, Status = FinancialCreditCardImportedInstallmentStatus.Active,
        CreatedAt = now, CreatedByUserId = userId,
    };

    public void Cancel(Guid userId, DateTimeOffset now)
    {
        if (Status == FinancialCreditCardImportedInstallmentStatus.Cancelled) return;
        Status = FinancialCreditCardImportedInstallmentStatus.Cancelled;
        CancelledAt = now; CancelledByUserId = userId;
    }
}

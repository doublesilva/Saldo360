using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public enum FinancialCreditCardImportedInstallmentStatus { Active, Cancelled }

public sealed class FinancialCreditCardImportedInstallment : Entity
{
    private FinancialCreditCardImportedInstallment() { Description = string.Empty; }
    public Guid WorkspaceId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public Guid? ImportId { get; private init; }
    public int? RowNumber { get; private init; }
    public string? ExternalId { get; private init; }
    public Guid? LinkedInstallmentId { get; private init; }
    public Guid? InvoiceId { get; private set; }
    public DateOnly? TransactionDate { get; private init; }
    public DateOnly? Competence { get; private init; }
    public DateOnly? DueDate { get; private init; }
    public string? SourceSystem { get; private init; }
    public string? ExternalReference { get; private init; }
    public string Description { get; private init; }
    public decimal Amount { get; private set; }
    public int? InstallmentNumber { get; private init; }
    public int? InstallmentCount { get; private init; }
    public Guid? CategoryId { get; private set; }
    public bool IsHistorical { get; private init; }
    public FinancialCreditCardImportedInstallmentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }

    public static FinancialCreditCardImportedInstallment Create(
        Guid workspaceId, Guid cardId, Guid importId, int rowNumber, string? externalId, Guid? linkedInstallmentId,
        Guid invoiceId, DateOnly transactionDate, string description, decimal amount, int installmentNumber,
        int installmentCount, Guid? categoryId, bool historical, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreditCardId = cardId, ImportId = importId, RowNumber = rowNumber,
        ExternalId = externalId, LinkedInstallmentId = linkedInstallmentId, InvoiceId = invoiceId,
        TransactionDate = transactionDate, Description = description, Amount = amount,
        InstallmentNumber = installmentNumber, InstallmentCount = installmentCount, CategoryId = categoryId,
        IsHistorical = historical, Status = FinancialCreditCardImportedInstallmentStatus.Active,
        CreatedAt = now, CreatedByUserId = userId,
    };

    /// <summary>Only explicitly known obligations; no invented purchase date, original total or invoice.</summary>
    public static FinancialCreditCardImportedInstallment CreateKnown(Guid workspaceId, Guid cardId, Guid? categoryId,
        string description, decimal amount, int? number, int? count, DateOnly competence, DateOnly? dueDate,
        string sourceSystem, string externalReference, Guid userId, DateTimeOffset now, DateOnly? transactionDate = null)
    {
        if (workspaceId == Guid.Empty || cardId == Guid.Empty || categoryId == Guid.Empty || string.IsNullOrWhiteSpace(description)
            || description.Trim().Length > 200 || amount <= 0 || Saldo360.Domain.Common.Money.Validate(amount, "Parcela") is not null
            || number is <= 0 || count is <= 0 || number > count || competence.Day != 1
            || string.IsNullOrWhiteSpace(sourceSystem) || sourceSystem.Length > 80 || string.IsNullOrWhiteSpace(externalReference) || externalReference.Length > 200)
            throw new ArgumentException("Parcela conhecida inválida.");
        return new() { WorkspaceId = workspaceId, CreditCardId = cardId, CategoryId = categoryId, Description = description.Trim(),
            Amount = amount, InstallmentNumber = number, InstallmentCount = count, Competence = competence, DueDate = dueDate, TransactionDate = transactionDate,
            SourceSystem = sourceSystem, ExternalReference = externalReference, IsHistorical = true,
            Status = FinancialCreditCardImportedInstallmentStatus.Active, CreatedByUserId = userId, CreatedAt = now };
    }

    public void Cancel(Guid userId, DateTimeOffset now)
    {
        if (Status == FinancialCreditCardImportedInstallmentStatus.Cancelled) return;
        Status = FinancialCreditCardImportedInstallmentStatus.Cancelled;
        CancelledAt = now; CancelledByUserId = userId;
    }

    public void AttachKnownInvoice(FinancialCreditCardInvoice invoice)
    {
        if (SourceSystem is null || Status != FinancialCreditCardImportedInstallmentStatus.Active || invoice.WorkspaceId != WorkspaceId
            || invoice.CreditCardId != CreditCardId || invoice.Competence != Competence?.ToString("yyyy-MM") || invoice.DueDate != DueDate
            || InvoiceId.HasValue && InvoiceId != invoice.Id) throw new ArgumentException("Fatura incompatível com obrigação conhecida.");
        InvoiceId = invoice.Id;
    }
    public void ApplyOfficial(decimal amount, Guid? categoryId, FinancialCreditCardInvoice? invoice = null)
    {
        if (Status != FinancialCreditCardImportedInstallmentStatus.Active || amount <= 0 || Money.Validate(amount, "Parcela") is not null) throw new ArgumentException("Parcela inválida.");
        if (invoice is not null) AttachKnownInvoice(invoice);
        Amount = amount; CategoryId = categoryId;
    }
}

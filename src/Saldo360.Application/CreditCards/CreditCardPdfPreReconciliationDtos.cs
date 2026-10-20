namespace Saldo360.Application.CreditCards;

public enum CreditCardPdfComponentClassification
{
    Create,
    AlreadyExists,
    NeedsReview,
    Conflict,
    Unsupported,
    Informational,
}

public sealed record CreditCardPdfDestinationValidation(
    Guid WorkspaceId,
    Guid CreditCardId,
    string Family,
    string CreditCard,
    string Holder,
    string MaskedCard,
    string Invoice,
    Guid? InvoiceId,
    IReadOnlyList<string> Notes);

public sealed record CreditCardPdfPreReconciliationItem(
    string ComponentId,
    int RowNumber,
    int Page,
    string Type,
    string Nature,
    string? TransactionDate,
    string Description,
    string? Amount,
    int? InstallmentNumber,
    int? InstallmentCount,
    bool IncludedInCurrentTotal,
    string Classification,
    string? MatchedEntityType,
    Guid? MatchedEntityId,
    IReadOnlyList<string> Reasons);

public sealed record CreditCardPdfPreReconciliationCounts(
    int Create,
    int AlreadyExists,
    int NeedsReview,
    int Conflict,
    int Unsupported,
    int Informational);

public sealed record CreditCardPdfEstimatedImpact(
    string Category,
    string Debit,
    string Credit,
    string Net,
    int Components);

public sealed record CreditCardPdfStatementIdentification(
    string? Issuer,
    string? CardLastDigits,
    string? Competence,
    string? ClosingDate,
    string? DueDate,
    string? TotalAmount);

public sealed record CreditCardPdfPreReconciliationReport(
    string DocumentKey,
    string Parser,
    CreditCardPdfStatementIdentification Statement,
    CreditCardPdfReconciliation Reconciliation,
    CreditCardPdfDestinationValidation Destination,
    CreditCardPdfPreReconciliationCounts Counts,
    IReadOnlyList<CreditCardPdfPreReconciliationItem> Items,
    IReadOnlyList<CreditCardPdfEstimatedImpact> EstimatedImpact,
    IReadOnlyList<string> Limitations);

public sealed record CreditCardPdfPreReconciliationResult(
    CreditCardPdfPreviewAccess Access,
    CreditCardPdfPreReconciliationReport? Report,
    IDictionary<string, string[]>? Errors);

public sealed record CreditCardPdfPreReconciliationSnapshot(
    string CardName,
    string Issuer,
    string? LastFourDigits,
    Guid? HolderUserId,
    CreditCardPdfInvoiceSnapshot? Invoice,
    IReadOnlyList<CreditCardPdfExistingOperation> Operations);

public sealed record CreditCardPdfInvoiceSnapshot(Guid Id, string Competence, DateOnly ClosingDate, DateOnly DueDate);

public sealed record CreditCardPdfExistingOperation(
    string EntityType,
    Guid EntityId,
    string? ExternalId,
    DateOnly Date,
    string Description,
    decimal Amount,
    int? InstallmentNumber,
    int? InstallmentCount,
    string? Competence);

public interface ICreditCardPdfPreReconciliationRepository
{
    Task<CreditCardPdfPreReconciliationSnapshot?> GetSnapshotAsync(
        Guid workspaceId,
        Guid cardId,
        Guid userId,
        string? competence,
        CancellationToken cancellationToken);
}

public interface ICreditCardPdfPreReconciliationService
{
    Task<CreditCardPdfPreReconciliationResult> PreReconcileAsync(
        Guid workspaceId,
        Guid cardId,
        Guid userId,
        Stream pdf,
        long length,
        string fileName,
        string? issuerProfile,
        string? competence,
        string? dateFormat,
        string? password,
        CancellationToken cancellationToken);
}

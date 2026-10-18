using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public enum FinancialDocumentSource { Manual, Email }
public enum FinancialDocumentInboxStatus { Received, Analyzing, ReadyForReview, ReadyToConfirm, NeedsReview, Conflict, Imported, Failed, Ignored }

public sealed class FinancialDocumentInboxItem : Entity
{
    private FinancialDocumentInboxItem() { OriginalFileName = ContentHash = ContentType = StorageReference = string.Empty; }
    public Guid FamilyId { get; private init; }
    public FinancialDocumentSource Source { get; private init; }
    public string? ExternalSourceId { get; private init; }
    public string OriginalFileName { get; private init; }
    public string ContentHash { get; private init; }
    public string ContentType { get; private init; }
    public string StorageReference { get; private init; }
    public DateTimeOffset ReceivedAt { get; private init; }
    public FinancialDocumentInboxStatus Status { get; private set; }
    public string? Parser { get; private set; }
    public string? Competence { get; private set; }
    public Guid? CreditCardId { get; private set; }
    public string? FailureCode { get; private set; }
    public string? DocumentType { get; private set; }
    public string? ClassificationConfidence { get; private set; }
    public string? ClassificationSource { get; private set; }
    public string? ClassificationFailureCode { get; private set; }
    public DateTimeOffset? ClassifiedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static FinancialDocumentInboxItem Create(Guid id, Guid familyId, FinancialDocumentSource source,
        string? externalSourceId, string fileName, string contentHash, string contentType, string storageReference, DateTimeOffset receivedAt, DateTimeOffset now) =>
        new() { Id = id, FamilyId = familyId, Source = source, ExternalSourceId = NullIfBlank(externalSourceId),
            OriginalFileName = fileName, ContentHash = contentHash, ContentType = contentType,
            StorageReference = storageReference, ReceivedAt = receivedAt, Status = FinancialDocumentInboxStatus.Received,
            CreatedAt = now, UpdatedAt = now };

    public void StartAnalysis(DateTimeOffset now) { EnsureMutable(); Status = FinancialDocumentInboxStatus.Analyzing; FailureCode = null; UpdatedAt = now; }
    public void CompleteAnalysis(string parser, string? competence, Guid? cardId, FinancialDocumentInboxStatus status, DateTimeOffset now)
    {
        if (status is not (FinancialDocumentInboxStatus.ReadyToConfirm or FinancialDocumentInboxStatus.NeedsReview or FinancialDocumentInboxStatus.ReadyForReview))
            throw new ArgumentOutOfRangeException(nameof(status));
        Parser = parser; Competence = competence; CreditCardId = cardId; Status = status; FailureCode = null; UpdatedAt = now;
    }
    public void Fail(string code, DateTimeOffset now) { Status = FinancialDocumentInboxStatus.Failed; FailureCode = code; UpdatedAt = now; }
    public void RequireReview(string code, DateTimeOffset now) { Status = FinancialDocumentInboxStatus.NeedsReview; FailureCode = code; UpdatedAt = now; }
    public void ApplyClassification(string documentType, string confidence, string source, string? failureCode, DateTimeOffset now)
    {
        DocumentType = documentType; ClassificationConfidence = confidence; ClassificationSource = source;
        ClassificationFailureCode = failureCode; ClassifiedAt = now; UpdatedAt = now;
        if (documentType != "CreditCardStatement") { Parser = null; Competence = null; CreditCardId = null; }
    }
    public void MarkImported(Guid cardId, DateTimeOffset now) { CreditCardId = cardId; Status = FinancialDocumentInboxStatus.Imported; FailureCode = null; UpdatedAt = now; }
    public void Ignore(DateTimeOffset now) { EnsureMutable(); Status = FinancialDocumentInboxStatus.Ignored; UpdatedAt = now; }
    private void EnsureMutable() { if (Status == FinancialDocumentInboxStatus.Imported) throw new InvalidOperationException("Documento já importado."); }
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

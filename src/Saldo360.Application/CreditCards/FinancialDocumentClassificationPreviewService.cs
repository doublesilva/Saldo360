namespace Saldo360.Application.CreditCards;

public enum FinancialDocumentClassificationAccess { Allowed, NotFound, Invalid }
public sealed record FinancialDocumentClassificationPreviewResult(
    FinancialDocumentClassificationAccess Access,
    FinancialDocumentClassificationResult? Classification = null,
    IDictionary<string, string[]>? Errors = null);

public sealed class FinancialDocumentClassificationPreviewService(
    IFinancialDocumentInboxRepository repository,
    IFinancialDocumentStorage storage,
    ICreditCardPdfTextExtractor extractor,
    IFinancialDocumentClassifier classifier)
{
    public async Task<FinancialDocumentClassificationPreviewResult> PreviewAsync(Guid workspaceId, Guid id, Guid userId, string? password, CancellationToken cancellationToken)
    {
        if (password?.Length > 128) return Invalid("password", "A senha excede o limite permitido.");
        var item = await repository.GetAsync(workspaceId, id, userId, cancellationToken);
        if (item is null) return new(FinancialDocumentClassificationAccess.NotFound);
        await using var content = await storage.OpenReadAsync(item.StorageReference, cancellationToken);
        if (content is null) return Invalid("document", "O documento armazenado não está disponível.");
        try
        {
            var passwords = string.IsNullOrEmpty(password) ? Array.Empty<string>() : [password];
            var document = await extractor.ExtractAsync(content, CreditCardPdfPreviewService.MaxPages, passwords, cancellationToken);
            if (document.Pages.Count == 0 || document.Pages.All(page => string.IsNullOrWhiteSpace(page.Text)))
                return new(FinancialDocumentClassificationAccess.Allowed,
                    new("Unknown", "Low", [], "Unknown", "InsufficientEvidence"));
            return new(FinancialDocumentClassificationAccess.Allowed,
                await classifier.ClassifyAsync(new(item.OriginalFileName, document), cancellationToken));
        }
        catch (CreditCardPdfReadException exception) when (exception.Failure is CreditCardPdfReadFailure.PasswordRequired or CreditCardPdfReadFailure.PasswordRejected)
        {
            return new(FinancialDocumentClassificationAccess.Allowed,
                new("Unknown", "Low", [], "Unknown", exception.Failure.ToString()));
        }
        catch (CreditCardPdfReadException exception)
        {
            return new(FinancialDocumentClassificationAccess.Allowed,
                new("Unknown", "Low", [], "Unknown", exception.Failure.ToString()));
        }
    }

    private static FinancialDocumentClassificationPreviewResult Invalid(string field, string message) =>
        new(FinancialDocumentClassificationAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

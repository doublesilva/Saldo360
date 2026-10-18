namespace Saldo360.Application.CreditCards;

public sealed record ServiceBillPreviewResult(FinancialDocumentClassificationAccess Access, ServiceBillExtractionOutput? Preview = null, IDictionary<string, string[]>? Errors = null);
public sealed class ServiceBillPreviewService(IFinancialDocumentInboxRepository repository, IFinancialDocumentStorage storage,
    ICreditCardPdfTextExtractor pdfExtractor, IServiceBillExtractor extractor, ServiceBillExtractionOptions options)
{
    public async Task<ServiceBillPreviewResult> PreviewAsync(Guid familyId, Guid id, Guid userId, string? password, CancellationToken cancellationToken)
    {
        var item = await repository.GetAsync(familyId, id, userId, cancellationToken);
        if (item is null) return new(FinancialDocumentClassificationAccess.NotFound);
        if (!options.Enabled) return Invalid("feature", "A extração experimental de contas está desabilitada.");
        if (item.DocumentType is not ("ElectricityBill" or "WaterBill" or "TelecomBill")) return Invalid("documentType", "O tipo documental não é uma conta de serviço suportada.");
        if (password?.Length > 128) return Invalid("password", "A senha excede o limite permitido.");
        await using var content = await storage.OpenReadAsync(item.StorageReference, cancellationToken);
        if (content is null) return Invalid("document", "O documento armazenado não está disponível.");
        try
        {
            var passwords = string.IsNullOrEmpty(password) ? Array.Empty<string>() : [password];
            var document = await pdfExtractor.ExtractAsync(content, CreditCardPdfPreviewService.MaxPages, passwords, cancellationToken);
            if (document.Pages.Count == 0 || document.Pages.All(page => string.IsNullOrWhiteSpace(page.Text)))
                return new(FinancialDocumentClassificationAccess.Allowed, new(new(item.DocumentType, null, null, null, null, null, null, "Low", "Unknown", "InsufficientEvidence"), "Rejected"));
            return new(FinancialDocumentClassificationAccess.Allowed, await extractor.ExtractAsync(new(item.OriginalFileName, document, item.DocumentType), cancellationToken));
        }
        catch (CreditCardPdfReadException exception)
        {
            return new(FinancialDocumentClassificationAccess.Allowed, new(new(item.DocumentType, null, null, null, null, null, null, "Low", "Unknown", exception.Failure.ToString()), "Rejected"));
        }
    }
    private static ServiceBillPreviewResult Invalid(string field, string message) => new(FinancialDocumentClassificationAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

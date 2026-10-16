using Saldo360.Application.Families;

namespace Saldo360.Application.CreditCards;

public sealed class CreditCardPdfPreviewService(
    IFamilyRepository families,
    IFinancialCreditCardPurchaseRepository cards,
    ICreditCardPdfTextExtractor extractor) : ICreditCardPdfPreviewService
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxPages = 50;
    public async Task<CreditCardPdfPreviewResult> PreviewAsync(Guid familyId, Guid cardId, Guid userId, Stream pdf, long length, string fileName, string? issuerProfile, string? competence, string? dateFormat, CancellationToken cancellationToken)
    {
        if (await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is null || !await cards.IsActiveCardAsync(familyId, cardId, cancellationToken)) return new(CreditCardPdfPreviewAccess.NotFound, null, null);
        if (length <= 0) return Invalid("file", "O arquivo PDF está vazio.");
        if (length > MaxFileBytes) return Invalid("file", "O arquivo excede o limite de 10 MB.");
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return Invalid("file", "O arquivo deve ter extensão .pdf.");
        try
        {
            var extracted = await extractor.ExtractAsync(pdf, MaxPages, cancellationToken);
            if (extracted.Pages.Count == 0 || extracted.Pages.All(page => string.IsNullOrWhiteSpace(page.Text))) return Invalid("file", "Não foi possível extrair texto do PDF; OCR não é suportado.");
            var preview = extractor.Parse(extracted, fileName, competence, dateFormat);
            return new(CreditCardPdfPreviewAccess.Allowed, preview, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid("file", "O PDF está corrompido, protegido ou não possui camada textual suficiente.");
        }
    }

    private static CreditCardPdfPreviewResult Invalid(string field, string message) => new(CreditCardPdfPreviewAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

public sealed record CreditCardPdfExtractedPage(int Number, string Text);
public sealed record CreditCardPdfExtractedDocument(IReadOnlyList<CreditCardPdfExtractedPage> Pages);

public interface ICreditCardPdfTextExtractor
{
    Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, CancellationToken cancellationToken);
    CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat);
}

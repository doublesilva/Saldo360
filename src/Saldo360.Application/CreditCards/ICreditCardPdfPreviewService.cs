namespace Saldo360.Application.CreditCards;

public interface ICreditCardPdfPreviewService
{
    Task<CreditCardPdfPreviewResult> PreviewAsync(Guid familyId, Guid cardId, Guid userId, Stream pdf, long length, string fileName, string? issuerProfile, string? competence, string? dateFormat, CancellationToken cancellationToken);
}

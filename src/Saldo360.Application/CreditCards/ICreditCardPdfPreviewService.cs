namespace Saldo360.Application.CreditCards;

public interface ICreditCardPdfPreviewService
{
    Task<CreditCardPdfPreviewResult> PreviewAsync(Guid workspaceId, Guid cardId, Guid userId, Stream pdf, long length, string fileName, string? issuerProfile, string? competence, string? dateFormat, string? password, CancellationToken cancellationToken);
}

namespace Saldo360.Application.CreditCards;

public interface ICreditCardCsvPreviewService
{
    Task<CreditCardCsvPreviewResult> PreviewAsync(
        Guid workspaceId,
        Guid cardId,
        Guid userId,
        Stream csv,
        long length,
        CreditCardCsvPreviewRequest request,
        CancellationToken cancellationToken);
}

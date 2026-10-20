using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialCreditCardImportRepository
{
    Task<FinancialCreditCardImportBatch?> FindBatchAsync(Guid importId, CancellationToken cancellationToken);
    Task<bool> IsExpenseCategoryAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken);
    Task<CreditCardImportRepositoryResult> ConfirmAsync(Guid workspaceId, Guid cardId, Guid userId, Guid importId, string payloadHash, IReadOnlyList<CreditCardImportItemInput> items, DateTimeOffset now, CancellationToken cancellationToken);
}

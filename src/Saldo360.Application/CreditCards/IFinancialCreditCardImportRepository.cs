using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialCreditCardImportRepository
{
    Task<FinancialCreditCardImportBatch?> FindBatchAsync(Guid importId, CancellationToken cancellationToken);
    Task<bool> IsExpenseCategoryAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken);
    Task<CreditCardImportRepositoryResult> ConfirmAsync(Guid familyId, Guid cardId, Guid userId, Guid importId, string payloadHash, IReadOnlyList<CreditCardImportItemInput> items, DateTimeOffset now, CancellationToken cancellationToken);
}

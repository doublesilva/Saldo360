using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialCreditCardImportedInstallmentRepository
{
    Task<IReadOnlyList<ImportedInstallmentMatchDto>?> FindMatchesAsync(Guid familyId, Guid cardId, DateOnly date, decimal amount, int number, int count, string? competence, string description, CancellationToken cancellationToken);
    Task<ImportedInstallmentOperationResult> LinkAsync(Guid familyId, Guid cardId, Guid userId, ImportedInstallmentInput input, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ImportedInstallmentOperationResult> CreateHistoricalAsync(Guid familyId, Guid cardId, Guid userId, ImportedInstallmentInput input, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<ImportedInstallmentRecordDto>?> ListAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken);
    Task<ImportedInstallmentRecordDto?> GetAsync(Guid familyId, Guid cardId, Guid id, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid familyId, Guid cardId, Guid id, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> IsExpenseCategoryAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken);
}

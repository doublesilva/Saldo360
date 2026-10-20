using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialCreditCardImportedInstallmentRepository
{
    Task<IReadOnlyList<ImportedInstallmentMatchDto>?> FindMatchesAsync(Guid workspaceId, Guid cardId, DateOnly date, decimal amount, int number, int count, string? competence, string description, CancellationToken cancellationToken);
    Task<ImportedInstallmentOperationResult> LinkAsync(Guid workspaceId, Guid cardId, Guid userId, ImportedInstallmentInput input, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ImportedInstallmentOperationResult> CreateHistoricalAsync(Guid workspaceId, Guid cardId, Guid userId, ImportedInstallmentInput input, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<ImportedInstallmentRecordDto>?> ListAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken);
    Task<ImportedInstallmentRecordDto?> GetAsync(Guid workspaceId, Guid cardId, Guid id, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid workspaceId, Guid cardId, Guid id, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> IsExpenseCategoryAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken);
}

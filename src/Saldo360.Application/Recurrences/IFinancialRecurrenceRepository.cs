using Saldo360.Domain.Recurrences;

namespace Saldo360.Application.Recurrences;

public interface IFinancialRecurrenceRepository
{
    Task AddAsync(FinancialRecurrence recurrence, CancellationToken ct);
    Task<RecurrenceDto?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<RecurrenceDto>> ListAsync(Guid workspaceId, bool includeArchived, CancellationToken ct);
    Task<FinancialRecurrence?> FindEntityAsync(Guid workspaceId, Guid id, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
    Task<bool> HasMaterializationsAsync(Guid workspaceId, Guid recurrenceId, CancellationToken ct);
}

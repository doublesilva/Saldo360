using Saldo360.Domain.Recurrences;

namespace Saldo360.Application.Recurrences;

public interface IFinancialRecurrenceRepository
{
    Task AddAsync(FinancialRecurrence recurrence, CancellationToken ct);
    Task<RecurrenceDto?> GetAsync(Guid familyId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<RecurrenceDto>> ListAsync(Guid familyId, bool includeArchived, CancellationToken ct);
    Task<FinancialRecurrence?> FindEntityAsync(Guid familyId, Guid id, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

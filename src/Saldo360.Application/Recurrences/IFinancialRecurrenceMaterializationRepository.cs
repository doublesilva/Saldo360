using Saldo360.Domain.Recurrences;

namespace Saldo360.Application.Recurrences;

public interface IFinancialRecurrenceMaterializationRepository
{
    Task<bool> HasAnyAsync(Guid familyId, Guid recurrenceId, CancellationToken ct);
    Task<MaterializationResultDto> MaterializeAsync(FinancialRecurrence recurrence, IReadOnlyList<RecurrenceOccurrenceDto> occurrences, Guid userId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<MaterializationQueryItem>> ListAsync(Guid familyId, Guid recurrenceId, DateOnly from, DateOnly through, CancellationToken ct);
}

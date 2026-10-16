using Saldo360.Application.Planning;

namespace Saldo360.Application.Recurrences;

public sealed record MaterializeRecurrenceInput(DateOnly? From, DateOnly? Through);
public sealed record MaterializationItemDto(int OccurrenceNumber, DateOnly ScheduledDate, Guid? PlannedTransactionId, DateOnly? PlannedDate, decimal? Amount, PlannedTransactionStatus? Status, string Result);
public sealed record MaterializationResultDto(Guid RecurrenceId, int CreatedCount, int ExistingCount, IReadOnlyList<MaterializationItemDto> Items);
public sealed record MaterializationQueryItem(int OccurrenceNumber, DateOnly ScheduledDate, Guid? PlannedTransactionId, DateOnly? PlannedDate, decimal? Amount, PlannedTransactionStatus? Status);

using Saldo360.Application.Planning;

namespace Saldo360.Application.Recurrences;

public sealed record MaterializeRecurrenceInput(DateOnly? From, DateOnly? Through);
public sealed record MaterializationItemDto(int OccurrenceNumber, DateOnly ScheduledDate, Guid? PlannedTransactionId, DateOnly? PlannedDate, decimal? Amount, PlannedTransactionStatus? Status, string Result, Guid? CreditCardPurchaseId = null);
public sealed record MaterializationResultDto(Guid RecurrenceId, int CreatedCount, int ExistingCount, IReadOnlyList<MaterializationItemDto> Items);
public sealed record MaterializationQueryItem(int OccurrenceNumber, DateOnly ScheduledDate, Guid? PlannedTransactionId, DateOnly? PlannedDate, decimal? Amount, PlannedTransactionStatus? Status, Guid? CreditCardPurchaseId = null);

public enum CompetenceAmountStatus { Ok, InvalidCompetence, Conflict }
/// <summary>Valor real confirmado de uma competência (PROJ-04). Base = valor da recorrência; Adjusted = ajuste da competência; Confirmed = valor real. Confirmar não é pagar.</summary>
public sealed record CompetenceAmountResult(Guid RecurrenceId, DateOnly Competence, Guid PlannedTransactionId, DateOnly DueDate, decimal BaseAmount, decimal AdjustedAmount, decimal ConfirmedAmount, DateTimeOffset ConfirmedAt, bool Created);
public sealed record CompetenceAmountOutcome(CompetenceAmountStatus Status, string? Message, CompetenceAmountResult? Result);

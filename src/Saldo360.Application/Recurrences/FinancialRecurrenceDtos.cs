using Saldo360.Domain.Categories;
using Saldo360.Domain.Recurrences;

namespace Saldo360.Application.Recurrences;

public sealed record RecurrenceInput(Guid? AccountId, Guid? CategoryId, string? Type, string? Description, decimal? Amount,
    bool IsEstimated, string? Frequency, int? Interval, DateOnly? StartDate, DateOnly? EndDate, int? OccurrenceCount, int? DayOfMonth, Guid? CreditCardId = null, string? ExpenseNature = null);
public sealed record RecurrenceDto(Guid Id, Guid WorkspaceId, Guid? AccountId, Guid CategoryId, CategoryType Type, string Description,
    decimal Amount, bool IsEstimated, RecurrenceFrequency Frequency, int Interval, DateOnly StartDate, DateOnly? EndDate,
    int? OccurrenceCount, int DayOfMonth, RecurrenceStatus Status, string Currency, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt, Guid? CreditCardId = null, ExpenseNature Nature = ExpenseNature.Undefined);
public sealed record RecurrenceOccurrenceDto(Guid RecurrenceId, int Sequence, DateOnly Date, string Description, CategoryType Type, decimal Amount, bool IsEstimated, Guid? AccountId, Guid CategoryId, Guid? CreditCardId = null);
public sealed record RecurrenceDetailsResult(RecurrenceDto? Recurrence, IDictionary<string,string[]>? Errors, bool NotFound, bool Conflict)
{
    public static RecurrenceDetailsResult NotFoundResult => new(null,null,true,false);
}
public sealed record RecurrenceListResult(IReadOnlyList<RecurrenceDto>? Items, IDictionary<string,string[]>? Errors, bool NotFound);
public enum RecurrenceAccess { Allowed, NotFound, Invalid, Conflict }
public sealed record RecurrenceOperationResult(RecurrenceAccess Access, RecurrenceDto? Recurrence, IDictionary<string,string[]>? Errors, string? Message = null);

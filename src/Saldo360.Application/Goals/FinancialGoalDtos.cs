using Saldo360.Domain.Goals;
namespace Saldo360.Application.Goals;
public sealed record FinancialGoalDto(Guid Id, Guid WorkspaceId, string Name, string? Description, FinancialGoalType GoalType, decimal TargetAmount, DateOnly? TargetDate, FinancialGoalPriority Priority, FinancialGoalStatus Status, string Currency, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ArchivedAt);
public sealed record FinancialGoalInput(string? Name, string? Description, string? GoalType, string? TargetAmount, DateOnly? TargetDate, string? Priority);
public enum FinancialGoalAccess { Allowed, NotFound, Invalid, Conflict }
public sealed record FinancialGoalResult(FinancialGoalAccess Access, FinancialGoalDto? Goal, IDictionary<string,string[]>? Errors, string? Message = null);

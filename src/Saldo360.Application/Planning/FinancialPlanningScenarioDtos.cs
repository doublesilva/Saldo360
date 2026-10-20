using Saldo360.Domain.Planning;

namespace Saldo360.Application.Planning;

public sealed record PlanningScenarioDto(Guid Id, Guid WorkspaceId, Guid? RecoveryAccountId, string Name, string? Description, DateOnly StartMonth, int Months, PlanningScenarioStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt);
public sealed record PlanningMonthlyParameterDto(Guid Id, Guid WorkspaceId, Guid ScenarioId, DateOnly Competence, decimal ExpectedAdditionalIncome, decimal ExpectedAdditionalExpense, decimal ContingencyReserve, decimal RecoveryTargetPercentage, string? Notes);
public sealed record PlanningScenarioInput(string? Name, string? Description, string? StartMonth, int? Months, Guid? RecoveryAccountId = null);
public sealed record PlanningMonthlyParameterInput(string? ExpectedAdditionalIncome, string? ExpectedAdditionalExpense, string? ContingencyReserve, string? RecoveryTargetPercentage, string? Notes);
public enum PlanningScenarioAccess { Allowed, NotFound, Invalid, Conflict }
public sealed record PlanningScenarioResult(PlanningScenarioAccess Access, PlanningScenarioDto? Scenario, IDictionary<string, string[]>? Errors, string? Message = null);
public sealed record PlanningParameterResult(PlanningScenarioAccess Access, PlanningMonthlyParameterDto? Parameter, IDictionary<string, string[]>? Errors, string? Message = null);

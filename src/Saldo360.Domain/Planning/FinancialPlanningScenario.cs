using Saldo360.Domain.Common;

namespace Saldo360.Domain.Planning;

public enum PlanningScenarioStatus { Active, Archived }

public sealed class FinancialPlanningScenario : Entity
{
    public const int NameMaxLength = 120;
    public const int DescriptionMaxLength = 500;

    private FinancialPlanningScenario() { Name = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public Guid? RecoveryAccountId { get; private set; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public DateOnly StartMonth { get; private set; }
    public int Months { get; private set; }
    public PlanningScenarioStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    public static FinancialPlanningScenario Create(Guid workspaceId, string name, string? description, DateOnly startMonth, int months, DateTimeOffset now, Guid? recoveryAccountId = null) => new()
    {
        WorkspaceId = workspaceId, RecoveryAccountId = recoveryAccountId, Name = name, Description = description, StartMonth = startMonth, Months = months,
        Status = PlanningScenarioStatus.Active, CreatedAt = now, UpdatedAt = now,
    };

    public void Update(string name, string? description, DateOnly startMonth, int months, DateTimeOffset now, Guid? recoveryAccountId = null)
    {
        if (Status == PlanningScenarioStatus.Archived) throw new InvalidOperationException("Cenário arquivado não pode ser editado.");
        Name = name; Description = description; StartMonth = startMonth; Months = months; RecoveryAccountId = recoveryAccountId; UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        if (Status == PlanningScenarioStatus.Archived) return;
        Status = PlanningScenarioStatus.Archived; ArchivedAt = now; UpdatedAt = now;
    }
}

public sealed class FinancialPlanningMonthlyParameter : Entity
{
    public const int NotesMaxLength = 500;
    private FinancialPlanningMonthlyParameter() { }

    public Guid WorkspaceId { get; private init; }
    public Guid ScenarioId { get; private init; }
    public DateOnly Competence { get; private init; }
    public decimal ExpectedAdditionalIncome { get; private set; }
    public decimal ExpectedAdditionalExpense { get; private set; }
    public decimal ContingencyReserve { get; private set; }
    public decimal RecoveryTargetPercentage { get; private set; }
    public string? Notes { get; private set; }

    public static FinancialPlanningMonthlyParameter Create(Guid workspaceId, Guid scenarioId, DateOnly competence, decimal income, decimal expense, decimal reserve, decimal recovery, string? notes) => new()
    {
        WorkspaceId = workspaceId, ScenarioId = scenarioId, Competence = competence, ExpectedAdditionalIncome = income,
        ExpectedAdditionalExpense = expense, ContingencyReserve = reserve, RecoveryTargetPercentage = recovery, Notes = notes,
    };

    public void Update(decimal income, decimal expense, decimal reserve, decimal recovery, string? notes)
    {
        ExpectedAdditionalIncome = income; ExpectedAdditionalExpense = expense; ContingencyReserve = reserve;
        RecoveryTargetPercentage = recovery; Notes = notes;
    }
}

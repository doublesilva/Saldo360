using Saldo360.Domain.Common;

namespace Saldo360.Domain.Goals;

public enum FinancialGoalType { EmergencyFund, Purchase, Travel, Education, DebtRepayment, Other }
public enum FinancialGoalPriority { Low, Medium, High }
public enum FinancialGoalStatus { Active, Paused, Completed, Archived }

public sealed class FinancialGoal : Entity
{
    public const int NameMaxLength = 160;
    public const int DescriptionMaxLength = 1000;
    private FinancialGoal() { Name = string.Empty; Currency = "BRL"; }
    public Guid FamilyId { get; private init; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public FinancialGoalType GoalType { get; private set; }
    public decimal TargetAmount { get; private set; }
    public DateOnly? TargetDate { get; private set; }
    public FinancialGoalPriority Priority { get; private set; }
    public FinancialGoalStatus Status { get; private set; }
    public string Currency { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    public static FinancialGoal Create(Guid familyId, FinancialGoalDetails details, DateTimeOffset now) =>
        new FinancialGoal() { FamilyId = familyId, Status = FinancialGoalStatus.Active, CreatedAt = now, UpdatedAt = now }.Apply(details);

    public void Update(FinancialGoalDetails details, DateTimeOffset now)
    {
        if (Status == FinancialGoalStatus.Archived) throw new InvalidOperationException("Objetivo arquivado não pode ser editado.");
        Apply(details); UpdatedAt = now;
    }
    public void Pause(DateTimeOffset now) { if (Status != FinancialGoalStatus.Active) throw new InvalidOperationException("Somente objetivos ativos podem ser pausados."); Status = FinancialGoalStatus.Paused; UpdatedAt = now; }
    public void Resume(DateTimeOffset now) { if (Status != FinancialGoalStatus.Paused) throw new InvalidOperationException("Somente objetivos pausados podem ser retomados."); Status = FinancialGoalStatus.Active; UpdatedAt = now; }
    public void Complete(DateTimeOffset now) { if (Status is not (FinancialGoalStatus.Active or FinancialGoalStatus.Paused)) throw new InvalidOperationException("Somente objetivos ativos ou pausados podem ser concluídos."); Status = FinancialGoalStatus.Completed; CompletedAt = now; UpdatedAt = now; }
    public void Archive(DateTimeOffset now) { if (Status == FinancialGoalStatus.Archived) return; Status = FinancialGoalStatus.Archived; ArchivedAt = now; UpdatedAt = now; }
    private FinancialGoal Apply(FinancialGoalDetails d) { Name=d.Name; Description=d.Description; GoalType=d.GoalType; TargetAmount=d.TargetAmount; TargetDate=d.TargetDate; Priority=d.Priority; return this; }
}

public sealed record FinancialGoalDetails(string Name, string? Description, FinancialGoalType GoalType, decimal TargetAmount, DateOnly? TargetDate, FinancialGoalPriority Priority);

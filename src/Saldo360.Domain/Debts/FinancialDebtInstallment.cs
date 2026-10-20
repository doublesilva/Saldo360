using Saldo360.Domain.Common;

namespace Saldo360.Domain.Debts;

public sealed class FinancialDebtInstallment : Entity
{
    private FinancialDebtInstallment() { }
    public Guid WorkspaceId { get; private init; }
    public Guid DebtId { get; private init; }
    public Guid? CategoryId { get; private init; }
    public int InstallmentNumber { get; private init; }
    public DateOnly DueDate { get; private init; }
    public decimal ScheduledAmount { get; private init; }
    public decimal? PrincipalComponent { get; private init; }
    public decimal? InterestComponent { get; private init; }
    public decimal? InsuranceComponent { get; private init; }
    public decimal? OtherChargesComponent { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public static FinancialDebtInstallment Create(Guid workspaceId, Guid debtId, InstallmentDetails d, DateTimeOffset now) => new()
    { WorkspaceId=workspaceId, DebtId=debtId, InstallmentNumber=d.InstallmentNumber, DueDate=d.DueDate, ScheduledAmount=d.ScheduledAmount,
      CategoryId=d.CategoryId,
      PrincipalComponent=d.PrincipalComponent, InterestComponent=d.InterestComponent, InsuranceComponent=d.InsuranceComponent,
      OtherChargesComponent=d.OtherChargesComponent, CreatedAt=now };
    public void Cancel(DateTimeOffset now) => CancelledAt ??= now;
}
public sealed record InstallmentDetails(int InstallmentNumber, DateOnly DueDate, decimal ScheduledAmount, decimal? PrincipalComponent, decimal? InterestComponent, decimal? InsuranceComponent, decimal? OtherChargesComponent, Guid? CategoryId = null);

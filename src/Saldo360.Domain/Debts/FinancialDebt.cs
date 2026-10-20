using Saldo360.Domain.Common;

namespace Saldo360.Domain.Debts;

public enum DebtType { PersonalLoan, VehicleFinancing, Mortgage, BusinessLoan, RenegotiatedDebt, Other }
public enum DebtStatus { Active, Suspended, Settled, Archived }
public enum InterestRatePeriod { Monthly, Annual }
public enum AmortizationMethod { Price, Sac, Other, Unknown }

public sealed class FinancialDebt : Entity
{
    public const int NameMaxLength = 160;
    public const int DescriptionMaxLength = 1000;
    public const int LenderNameMaxLength = 160;
    private FinancialDebt() { Name = string.Empty; Currency = "BRL"; }
    public Guid WorkspaceId { get; private init; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public DebtType DebtType { get; private set; }
    public string? LenderName { get; private set; }
    public Guid? ResponsibleAccountId { get; private set; }
    public decimal OriginalPrincipal { get; private set; }
    public decimal? OutstandingPrincipal { get; private set; }
    public decimal? OutstandingBalance { get; private set; }
    public DateOnly? BalanceReferenceDate { get; private set; }
    public DateOnly ContractDate { get; private set; }
    public DateOnly? FirstDueDate { get; private set; }
    public int? InstallmentCount { get; private set; }
    public int? PaidInstallmentCount { get; private set; }
    public decimal? InstallmentAmount { get; private set; }
    public decimal? InterestRate { get; private set; }
    public InterestRatePeriod? InterestRatePeriod { get; private set; }
    public AmortizationMethod? AmortizationMethod { get; private set; }
    public DebtStatus Status { get; private set; }
    public string Currency { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    public static FinancialDebt Create(Guid workspaceId, DebtDetails details, DateTimeOffset now) => new FinancialDebt { WorkspaceId = workspaceId, Status = DebtStatus.Active, CreatedAt = now, UpdatedAt = now }.Apply(details);
    public void Update(DebtDetails details, DateTimeOffset now) { if (Status == DebtStatus.Archived) throw new InvalidOperationException("Dívida arquivada não pode ser editada."); Apply(details); UpdatedAt = now; }
    public void Suspend(DateTimeOffset now) { if (Status == DebtStatus.Active) { Status = DebtStatus.Suspended; UpdatedAt = now; } }
    public void Resume(DateTimeOffset now) { if (Status == DebtStatus.Suspended) { Status = DebtStatus.Active; UpdatedAt = now; } }
    public void Settle(DateTimeOffset now) { if (Status is not DebtStatus.Archived) { Status = DebtStatus.Settled; UpdatedAt = now; } }
    public void Archive(DateTimeOffset now) { if (Status != DebtStatus.Archived) { Status = DebtStatus.Archived; ArchivedAt = now; UpdatedAt = now; } }
    private FinancialDebt Apply(DebtDetails d) { Name=d.Name; Description=d.Description; DebtType=d.DebtType; LenderName=d.LenderName; ResponsibleAccountId=d.ResponsibleAccountId; OriginalPrincipal=d.OriginalPrincipal; OutstandingPrincipal=d.OutstandingPrincipal; OutstandingBalance=d.OutstandingBalance; BalanceReferenceDate=d.BalanceReferenceDate; ContractDate=d.ContractDate; FirstDueDate=d.FirstDueDate; InstallmentCount=d.InstallmentCount; PaidInstallmentCount=d.PaidInstallmentCount; InstallmentAmount=d.InstallmentAmount; InterestRate=d.InterestRate; InterestRatePeriod=d.InterestRatePeriod; AmortizationMethod=d.AmortizationMethod; return this; }
}

public sealed record DebtDetails(Guid? ResponsibleAccountId, string Name, string? Description, DebtType DebtType, string? LenderName, decimal OriginalPrincipal, decimal? OutstandingPrincipal, decimal? OutstandingBalance, DateOnly? BalanceReferenceDate, DateOnly ContractDate, DateOnly? FirstDueDate, int? InstallmentCount, int? PaidInstallmentCount, decimal? InstallmentAmount, decimal? InterestRate, InterestRatePeriod? InterestRatePeriod, AmortizationMethod? AmortizationMethod);

namespace Saldo360.Application.Debts;
public interface IFinancialDebtInstallmentReconciliationRepository
{
 Task<IReadOnlyList<DebtInstallmentReconciliationDto>> ListAsync(Guid familyId,CancellationToken ct);
 Task<DebtInstallmentReconciliationDto?> CreateAsync(Guid familyId,Guid installmentId,Guid plannedId,Guid userId,DateTimeOffset now,CancellationToken ct);
 Task<bool> RemoveAsync(Guid familyId,Guid id,Guid userId,DateTimeOffset now,CancellationToken ct);
 Task<(bool InstallmentExists,bool PlannedExists,bool PlannedEligible,bool InstallmentEligible,bool AccountCompatible,bool AlreadyLinked)> EligibilityAsync(Guid familyId,Guid installmentId,Guid plannedId,CancellationToken ct);
}

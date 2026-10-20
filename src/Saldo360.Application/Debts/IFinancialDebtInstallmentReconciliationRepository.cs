namespace Saldo360.Application.Debts;
public interface IFinancialDebtInstallmentReconciliationRepository
{
 Task<IReadOnlyList<DebtInstallmentReconciliationDto>> ListAsync(Guid workspaceId,CancellationToken ct);
 Task<DebtInstallmentReconciliationDto?> FindActiveAsync(Guid workspaceId,Guid installmentId,Guid plannedId,CancellationToken ct);
 Task<DebtInstallmentReconciliationDto?> CreateAsync(Guid workspaceId,Guid installmentId,Guid plannedId,Guid userId,DateTimeOffset now,CancellationToken ct);
 Task<bool> RemoveAsync(Guid workspaceId,Guid id,Guid userId,DateTimeOffset now,CancellationToken ct);
 Task<(bool InstallmentExists,bool PlannedExists,bool PlannedEligible,bool InstallmentEligible,bool AccountCompatible,bool AlreadyLinked)> EligibilityAsync(Guid workspaceId,Guid installmentId,Guid plannedId,CancellationToken ct);
}

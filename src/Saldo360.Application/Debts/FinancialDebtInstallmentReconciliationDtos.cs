namespace Saldo360.Application.Debts;
public sealed record DebtInstallmentReconciliationInput(Guid? DebtInstallmentId, Guid? PlannedTransactionId);
public sealed record DebtInstallmentReconciliationDto(Guid Id,Guid FamilyId,Guid DebtInstallmentId,Guid PlannedTransactionId,DateTimeOffset CreatedAt,DateTimeOffset? RemovedAt);
public enum DebtReconciliationAccess{Allowed,NotFound,Invalid,Conflict}
public sealed record DebtReconciliationResult(DebtReconciliationAccess Access,DebtInstallmentReconciliationDto? Reconciliation,IDictionary<string,string[]>? Errors,string? Message=null);

using Saldo360.Domain.Common;
namespace Saldo360.Domain.Debts;
public sealed class FinancialDebtInstallmentReconciliation : Entity
{
 private FinancialDebtInstallmentReconciliation() { }
 public Guid WorkspaceId { get; private init; }
 public Guid DebtInstallmentId { get; private init; }
 public Guid PlannedTransactionId { get; private init; }
 public DateTimeOffset CreatedAt { get; private init; }
 public Guid CreatedByUserId { get; private init; }
 public DateTimeOffset? RemovedAt { get; private set; }
 public Guid? RemovedByUserId { get; private set; }
 public static FinancialDebtInstallmentReconciliation Create(Guid workspaceId,Guid installmentId,Guid plannedId,Guid userId,DateTimeOffset now)=>new(){WorkspaceId=workspaceId,DebtInstallmentId=installmentId,PlannedTransactionId=plannedId,CreatedByUserId=userId,CreatedAt=now};
 public bool Remove(Guid userId,DateTimeOffset now){if(RemovedAt is not null)return false;RemovedAt=now;RemovedByUserId=userId;return true;}
}

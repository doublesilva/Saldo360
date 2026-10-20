using Saldo360.Domain.Common;

namespace Saldo360.Domain.Debts;

public sealed class FinancialDebtPayment : Entity
{
    private FinancialDebtPayment() { }
    public Guid WorkspaceId { get; private init; }
    public Guid DebtId { get; private init; }
    public Guid InstallmentId { get; private init; }
    public Guid FinancialAccountId { get; private init; }
    public DateOnly PaymentDate { get; private init; }
    public decimal Amount { get; private init; }
    public decimal? PrincipalPaid { get; private init; }
    public decimal? InterestPaid { get; private init; }
    public decimal? InsurancePaid { get; private init; }
    public decimal? OtherChargesPaid { get; private init; }
    public Guid BankTransactionId { get; private init; }
    public string OperationId { get; private init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public static FinancialDebtPayment Create(Guid workspaceId, Guid debtId, Guid installmentId, Guid accountId, PaymentDetails d, Guid transactionId, DateTimeOffset now) => new()
    { WorkspaceId=workspaceId, DebtId=debtId, InstallmentId=installmentId, FinancialAccountId=accountId, PaymentDate=d.PaymentDate, Amount=d.Amount,
      PrincipalPaid=d.PrincipalPaid, InterestPaid=d.InterestPaid, InsurancePaid=d.InsurancePaid, OtherChargesPaid=d.OtherChargesPaid,
      BankTransactionId=transactionId, OperationId=d.OperationId, CreatedAt=now };
    public bool Cancel(DateTimeOffset now) { if (CancelledAt is not null) return false; CancelledAt=now; return true; }
}
public sealed record PaymentDetails(string OperationId, DateOnly PaymentDate, decimal Amount, decimal? PrincipalPaid, decimal? InterestPaid, decimal? InsurancePaid, decimal? OtherChargesPaid);

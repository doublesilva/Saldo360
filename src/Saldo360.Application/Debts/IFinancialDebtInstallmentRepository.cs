using Saldo360.Domain.Debts;
using Saldo360.Domain.Transactions;
namespace Saldo360.Application.Debts;
public interface IFinancialDebtInstallmentRepository
{
 Task<FinancialDebt?> GetDebtAsync(Guid workspaceId, Guid debtId, CancellationToken ct);
 Task<FinancialDebtInstallment?> GetInstallmentAsync(Guid workspaceId, Guid debtId, Guid installmentId, CancellationToken ct);
 Task<IReadOnlyList<InstallmentDto>> ListInstallmentsAsync(Guid workspaceId, Guid debtId, CancellationToken ct);
 Task<IReadOnlyList<PaymentDto>> ListPaymentsAsync(Guid workspaceId, Guid installmentId, CancellationToken ct);
 Task<FinancialDebtPayment?> FindPaymentByOperationAsync(Guid workspaceId, string operationId, CancellationToken ct);
 Task<bool> PaymentPayloadMatchesAsync(Guid workspaceId, string operationId, Guid debtId, Guid installmentId, Guid accountId, DateOnly paymentDate, decimal amount, decimal? principalPaid, decimal? interestPaid, decimal? insurancePaid, decimal? otherChargesPaid, CancellationToken ct);
 Task<PaymentDto?> GetPaymentAsync(Guid workspaceId, Guid paymentId, CancellationToken ct);
 Task AddInstallmentAsync(FinancialDebtInstallment item, CancellationToken ct);
 Task<(FinancialDebtPayment Payment, FinancialTransaction Transaction)?> AddPaymentAsync(FinancialDebtPayment payment, FinancialTransaction transaction, CancellationToken ct);
 Task SaveAsync(CancellationToken ct);
 Task<bool> CancelPaymentAsync(Guid workspaceId, Guid paymentId, Guid userId, DateTimeOffset now, CancellationToken ct);
}

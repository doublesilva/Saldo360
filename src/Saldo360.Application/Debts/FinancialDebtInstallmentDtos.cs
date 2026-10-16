namespace Saldo360.Application.Debts;
public sealed record InstallmentInput(int? InstallmentNumber, DateOnly? DueDate, string? ScheduledAmount, string? PrincipalComponent, string? InterestComponent, string? InsuranceComponent, string? OtherChargesComponent, Guid? CategoryId = null);
public sealed record PaymentInput(string? OperationId, Guid? FinancialAccountId, DateOnly? PaymentDate, string? Amount, string? PrincipalPaid, string? InterestPaid, string? InsurancePaid, string? OtherChargesPaid);
public sealed record InstallmentDto(Guid Id, Guid FamilyId, Guid DebtId, int InstallmentNumber, DateOnly DueDate, decimal ScheduledAmount, decimal? PrincipalComponent, decimal? InterestComponent, decimal? InsuranceComponent, decimal? OtherChargesComponent, decimal PaidAmount, decimal RemainingAmount, string Status, DateTimeOffset CreatedAt);
public sealed record PaymentDto(Guid Id, Guid InstallmentId, Guid FinancialAccountId, DateOnly PaymentDate, decimal Amount, decimal? PrincipalPaid, decimal? InterestPaid, Guid BankTransactionId, string OperationId, bool IsCancelled, DateTimeOffset CreatedAt);
public enum DebtInstallmentAccess { Allowed, NotFound, Invalid, Conflict }
public sealed record InstallmentResult(DebtInstallmentAccess Access, InstallmentDto? Installment, IDictionary<string,string[]>? Errors, string? Message=null);
public sealed record PaymentResult(DebtInstallmentAccess Access, PaymentDto? Payment, IDictionary<string,string[]>? Errors, string? Message=null);

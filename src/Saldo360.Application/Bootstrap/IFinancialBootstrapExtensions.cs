using Saldo360.Domain.Budgets;

namespace Saldo360.Application.Bootstrap;

public sealed record AggregateTargetInput(DateOnly Competence, MonthlyAggregateTargetType TargetType, decimal Amount, BootstrapSource Source);
public sealed record KnownCardInstallmentInput(Guid CreditCardId, Guid? CategoryId, string Description, int? InstallmentNumber,
    int? TotalInstallments, DateOnly Competence, DateOnly? DueDate, decimal Amount, BootstrapSource Source);
public sealed record BootstrapWriteResult(BootstrapDisposition Disposition, Guid? EntityId, string? Code = null);

/// <summary>Application port for the future one-shot importer; no public import endpoint or automatic execution.</summary>
public interface IFinancialBootstrapExtensions
{
    Task<BootstrapWriteResult> SaveTargetAsync(Guid workspaceId, Guid actorId, AggregateTargetInput input, CancellationToken ct);
    Task<BootstrapWriteResult> SaveKnownInstallmentAsync(Guid workspaceId, Guid actorId, KnownCardInstallmentInput input, CancellationToken ct);
    Task<BootstrapWriteResult> AttachKnownInvoiceAsync(Guid workspaceId, Guid actorId, Guid installmentId, Guid invoiceId, CancellationToken ct);
    Task<IReadOnlyList<FinancialMonthlyAggregateTarget>?> ListTargetsAsync(Guid workspaceId, Guid actorId, CancellationToken ct);
    Task<BootstrapWriteResult> InspectProvenanceAsync(Guid workspaceId, Guid actorId, BootstrapSource source, string contentHash, CancellationToken ct);
}

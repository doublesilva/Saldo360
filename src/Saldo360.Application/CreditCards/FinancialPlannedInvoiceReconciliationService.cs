using Saldo360.Application.Workspaces;
using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialPlannedInvoiceReconciliationService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialPlannedInvoiceReconciliationRepository reconciliations,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<FinancialPlannedInvoiceReconciliationDto>?> ListAsync(
        Guid workspaceId, Guid userId, Guid? invoiceId, Guid? plannedTransactionId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken)
            ? await reconciliations.ListAsync(workspaceId, invoiceId, plannedTransactionId, cancellationToken)
            : null;

    public async Task<FinancialPlannedInvoiceReconciliationResult> CreateAsync(
        Guid workspaceId, Guid userId, FinancialPlannedInvoiceReconciliationInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken))
            return new(FinancialPlannedInvoiceReconciliationAccess.NotFound, null, null);

        var errors = new Dictionary<string, string[]>();
        if (input.PlannedTransactionId is null || input.PlannedTransactionId == Guid.Empty)
            errors["plannedTransactionId"] = ["Informe a previsão."];
        if (input.CreditCardInvoiceId is null || input.CreditCardInvoiceId == Guid.Empty)
            errors["creditCardInvoiceId"] = ["Informe a fatura."];
        if (errors.Count > 0)
            return new(FinancialPlannedInvoiceReconciliationAccess.Invalid, null, errors);

        if (await reconciliations.FindActiveAsync(workspaceId, input.PlannedTransactionId!.Value, input.CreditCardInvoiceId!.Value, cancellationToken) is { } existing)
            return new(FinancialPlannedInvoiceReconciliationAccess.Allowed, existing, null);

        var eligibility = await reconciliations.GetEligibilityAsync(workspaceId, input.PlannedTransactionId!.Value, input.CreditCardInvoiceId!.Value, cancellationToken);
        if (eligibility is null || !eligibility.PlannedExists || !eligibility.InvoiceExists)
            return new(FinancialPlannedInvoiceReconciliationAccess.NotFound, null, null);
        if (!eligibility.PlannedEligible)
            return Invalid("plannedTransactionId", "A previsão precisa ser uma despesa pendente, não cancelada e sem lançamento efetivo.");
        if (!eligibility.InvoiceEligible)
            return Invalid("creditCardInvoiceId", "A fatura precisa possuir saldo devido e não estar totalmente quitada.");
        if (!eligibility.AccountCompatible)
            return Invalid("plannedTransactionId", "A conta da previsão deve ser a conta preferencial do cartão.");
        if (eligibility.PlannedAlreadyLinked)
            return Conflict("A previsão já possui uma conciliação ativa.");
        if (eligibility.InvoiceAlreadyLinked)
            return Conflict("A fatura já possui uma conciliação ativa.");

        var created = await reconciliations.CreateAsync(workspaceId, input.PlannedTransactionId.Value, input.CreditCardInvoiceId.Value, userId, timeProvider.GetUtcNow(), cancellationToken);
        if (created is not null) return new(FinancialPlannedInvoiceReconciliationAccess.Allowed, created, null);
        return await reconciliations.FindActiveAsync(workspaceId, input.PlannedTransactionId.Value, input.CreditCardInvoiceId.Value, cancellationToken) is { } concurrent
            ? new(FinancialPlannedInvoiceReconciliationAccess.Allowed, concurrent, null)
            : Conflict("A previsão ou a fatura foi conciliada por outra operação.");
    }

    public async Task<bool> RemoveAsync(Guid workspaceId, Guid id, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken)
        && await reconciliations.RemoveAsync(workspaceId, id, userId, timeProvider.GetUtcNow(), cancellationToken);

    private async Task<bool> IsParticipantAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is not null;

    private static FinancialPlannedInvoiceReconciliationResult Invalid(string field, string message) =>
        new(FinancialPlannedInvoiceReconciliationAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });

    private static FinancialPlannedInvoiceReconciliationResult Conflict(string message) =>
        new(FinancialPlannedInvoiceReconciliationAccess.Conflict, null, null, message);
}

using Saldo360.Application.Workspaces;
using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialCreditCardInvoicePaymentService(IFinancialWorkspaceRepository workspaces, IFinancialCreditCardInvoicePaymentRepository payments, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<FinancialCreditCardInvoicePaymentDto>?> ListAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid userId, CancellationToken ct) =>
        await IsParticipant(workspaceId, userId, ct) ? await payments.ListAsync(workspaceId, cardId, invoiceId, ct) : null;

    public async Task<FinancialCreditCardInvoicePaymentResult> CreateAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid userId, FinancialCreditCardInvoicePaymentInput input, CancellationToken ct)
    {
        if (!await IsParticipant(workspaceId, userId, ct)) return new(FinancialCreditCardInvoicePaymentAccess.NotFound, null, null);
        var errors = new Dictionary<string, string[]>();
        if (input.OperationId is null || input.OperationId == Guid.Empty) errors["operationId"] = ["Informe um OperationId válido."];
        if (input.FinancialAccountId is null || input.FinancialAccountId == Guid.Empty) errors["financialAccountId"] = ["Informe a conta de pagamento."];
        if (input.PaymentDate is null) errors["paymentDate"] = ["Informe a data do pagamento."];
        else if (input.PaymentDate > BusinessCalendar.Today(timeProvider.GetUtcNow())) errors["paymentDate"] = ["A data não pode ser futura."];
        if (input.Amount is null || input.Amount <= 0) errors["amount"] = ["O valor deve ser maior que zero."];
        else if (Money.Validate(input.Amount.Value, "O valor") is { } error) errors["amount"] = [error];
        if (errors.Count > 0) return new(FinancialCreditCardInvoicePaymentAccess.Invalid, null, errors);
        var operationId = input.OperationId!.Value;
        if (await payments.GetByOperationAsync(workspaceId, operationId, ct) is { } existing)
            return await payments.PayloadMatchesAsync(workspaceId, operationId, invoiceId, input.FinancialAccountId!.Value, input.PaymentDate!.Value, input.Amount!.Value, ct)
                ? new(FinancialCreditCardInvoicePaymentAccess.Allowed, existing, null)
                : new(FinancialCreditCardInvoicePaymentAccess.Conflict, null, null, "OperationId já utilizado com conteúdo diferente.");
        var now = timeProvider.GetUtcNow();
        var result = await payments.CreateAsync(workspaceId, cardId, invoiceId, operationId, input.FinancialAccountId!.Value, input.PaymentDate!.Value, input.Amount!.Value, userId, BusinessCalendar.Today(now), now, ct);
        if (result.Result == PaymentOperationResult.DuplicateOperation && await payments.GetByOperationAsync(workspaceId, operationId, ct) is { } concurrent)
            return await payments.PayloadMatchesAsync(workspaceId, operationId, invoiceId, input.FinancialAccountId.Value, input.PaymentDate.Value, input.Amount.Value, ct)
                ? new(FinancialCreditCardInvoicePaymentAccess.Allowed, concurrent, null)
                : new(FinancialCreditCardInvoicePaymentAccess.Conflict, null, null, "OperationId já utilizado com conteúdo diferente.");
        return result.Result switch
        {
            PaymentOperationResult.Success => new(FinancialCreditCardInvoicePaymentAccess.Allowed, await payments.GetAsync(workspaceId, cardId, invoiceId, result.PaymentId!.Value, ct), null),
            PaymentOperationResult.OpenInvoice => new(FinancialCreditCardInvoicePaymentAccess.Conflict, null, null, "A fatura precisa estar fechada para receber pagamentos."),
            PaymentOperationResult.InvalidAccount => new(FinancialCreditCardInvoicePaymentAccess.Invalid, null, new Dictionary<string, string[]> { ["financialAccountId"] = ["A conta não existe ou está inativa."] }),
            PaymentOperationResult.ExceedsDue => new(FinancialCreditCardInvoicePaymentAccess.Invalid, null, new Dictionary<string, string[]> { ["amount"] = ["O valor não pode exceder o saldo devido da fatura."] }),
            PaymentOperationResult.DuplicateOperation => new(FinancialCreditCardInvoicePaymentAccess.Conflict, null, null, "OperationId concorrente não pôde ser reconciliado."),
            _ => new(FinancialCreditCardInvoicePaymentAccess.NotFound, null, null),
        };
    }

    public async Task<FinancialCreditCardInvoicePaymentResult> CancelAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid paymentId, Guid userId, CancellationToken ct)
    {
        if (!await IsParticipant(workspaceId, userId, ct)) return new(FinancialCreditCardInvoicePaymentAccess.NotFound, null, null);
        return await payments.CancelAsync(workspaceId, cardId, invoiceId, paymentId, userId, timeProvider.GetUtcNow(), ct)
            ? new(FinancialCreditCardInvoicePaymentAccess.Allowed, await payments.GetAsync(workspaceId, cardId, invoiceId, paymentId, ct), null)
            : new(FinancialCreditCardInvoicePaymentAccess.NotFound, null, null);
    }

    private async Task<bool> IsParticipant(Guid workspaceId, Guid userId, CancellationToken ct) => await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is not null;
}

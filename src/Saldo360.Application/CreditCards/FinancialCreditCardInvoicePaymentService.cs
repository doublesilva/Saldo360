using Saldo360.Application.Families;
using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialCreditCardInvoicePaymentService(IFamilyRepository families, IFinancialCreditCardInvoicePaymentRepository payments, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<FinancialCreditCardInvoicePaymentDto>?> ListAsync(Guid familyId, Guid cardId, Guid invoiceId, Guid userId, CancellationToken ct) =>
        await IsParticipant(familyId, userId, ct) ? await payments.ListAsync(familyId, cardId, invoiceId, ct) : null;

    public async Task<FinancialCreditCardInvoicePaymentResult> CreateAsync(Guid familyId, Guid cardId, Guid invoiceId, Guid userId, FinancialCreditCardInvoicePaymentInput input, CancellationToken ct)
    {
        if (!await IsParticipant(familyId, userId, ct)) return new(FinancialCreditCardInvoicePaymentAccess.NotFound, null, null);
        var errors = new Dictionary<string, string[]>();
        if (input.FinancialAccountId is null || input.FinancialAccountId == Guid.Empty) errors["financialAccountId"] = ["Informe a conta de pagamento."];
        if (input.PaymentDate is null) errors["paymentDate"] = ["Informe a data do pagamento."];
        else if (input.PaymentDate > BusinessCalendar.Today(timeProvider.GetUtcNow())) errors["paymentDate"] = ["A data não pode ser futura."];
        if (input.Amount is null || input.Amount <= 0) errors["amount"] = ["O valor deve ser maior que zero."];
        else if (Money.Validate(input.Amount.Value, "O valor") is { } error) errors["amount"] = [error];
        if (errors.Count > 0) return new(FinancialCreditCardInvoicePaymentAccess.Invalid, null, errors);
        var result = await payments.CreateAsync(familyId, cardId, invoiceId, input.FinancialAccountId!.Value, input.PaymentDate!.Value, input.Amount!.Value, userId, BusinessCalendar.Today(timeProvider.GetUtcNow()), timeProvider.GetUtcNow(), ct);
        return result.Result switch
        {
            PaymentOperationResult.Success => new(FinancialCreditCardInvoicePaymentAccess.Allowed, await payments.GetAsync(familyId, cardId, invoiceId, result.PaymentId!.Value, ct), null),
            PaymentOperationResult.OpenInvoice => new(FinancialCreditCardInvoicePaymentAccess.Conflict, null, null, "A fatura precisa estar fechada para receber pagamentos."),
            PaymentOperationResult.InvalidAccount => new(FinancialCreditCardInvoicePaymentAccess.Invalid, null, new Dictionary<string, string[]> { ["financialAccountId"] = ["A conta não existe ou está inativa."] }),
            PaymentOperationResult.ExceedsDue => new(FinancialCreditCardInvoicePaymentAccess.Invalid, null, new Dictionary<string, string[]> { ["amount"] = ["O valor não pode exceder o saldo devido da fatura."] }),
            _ => new(FinancialCreditCardInvoicePaymentAccess.NotFound, null, null),
        };
    }

    public async Task<FinancialCreditCardInvoicePaymentResult> CancelAsync(Guid familyId, Guid cardId, Guid invoiceId, Guid paymentId, Guid userId, CancellationToken ct)
    {
        if (!await IsParticipant(familyId, userId, ct)) return new(FinancialCreditCardInvoicePaymentAccess.NotFound, null, null);
        return await payments.CancelAsync(familyId, cardId, invoiceId, paymentId, userId, timeProvider.GetUtcNow(), ct)
            ? new(FinancialCreditCardInvoicePaymentAccess.Allowed, await payments.GetAsync(familyId, cardId, invoiceId, paymentId, ct), null)
            : new(FinancialCreditCardInvoicePaymentAccess.NotFound, null, null);
    }

    private async Task<bool> IsParticipant(Guid familyId, Guid userId, CancellationToken ct) => await families.GetMemberRoleAsync(familyId, userId, ct) is not null;
}

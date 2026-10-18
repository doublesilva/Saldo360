using System.Globalization;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialCreditCardInvoiceAdjustmentService(
    IFinancialCreditCardInvoiceAdjustmentRepository repository,
    TimeProvider clock)
{
    public Task<IReadOnlyList<FinancialCreditCardInvoiceAdjustmentDto>?> ListAsync(
        Guid familyId, Guid cardId, Guid invoiceId, CancellationToken cancellationToken) =>
        repository.ListAsync(familyId, cardId, invoiceId, cancellationToken);

    public async Task<InvoiceAdjustmentOperationResult> CreateAsync(
        Guid familyId, Guid cardId, Guid invoiceId, Guid userId,
        FinancialCreditCardInvoiceAdjustmentInput? input, CancellationToken cancellationToken)
    {
        if (input is null) return Invalid("body", "Informe o ajuste da fatura.");
        var errors = new Dictionary<string, string[]>();
        var validType = Enum.TryParse<FinancialCreditCardInvoiceAdjustmentType>(input.Type, true, out var type);
        if (!validType)
            errors["type"] = ["Tipo deve ser Fee, Credit ou Refund."];
        var validNature = Enum.TryParse<FinancialCreditCardInvoiceAdjustmentNature>(input.Nature, true, out var nature);
        if (!validNature)
            errors["nature"] = ["Natureza deve ser Debit ou Credit."];
        if (validType && validNature && ((type == FinancialCreditCardInvoiceAdjustmentType.Fee) != (nature == FinancialCreditCardInvoiceAdjustmentNature.Debit)))
            errors["nature"] = ["Fee deve ser Debit; Credit e Refund devem ser Credit."];
        if (!DateOnly.TryParseExact(input.TransactionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            errors["transactionDate"] = ["Use o formato yyyy-MM-dd."];
        var description = input.Description?.Trim() ?? string.Empty;
        if (description.Length == 0) errors["description"] = ["Informe a descrição."];
        else if (description.Length > FinancialCreditCardInvoiceAdjustment.DescriptionMaxLength) errors["description"] = [$"A descrição deve ter no máximo {FinancialCreditCardInvoiceAdjustment.DescriptionMaxLength} caracteres."];
        if (!decimal.TryParse(input.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0m || Money.Validate(amount, "O valor") is not null)
            errors["amount"] = ["O valor deve ser decimal positivo, com no máximo duas casas decimais."];
        var externalId = input.ExternalId?.Trim() ?? string.Empty;
        if (externalId.Length == 0) errors["externalId"] = ["Informe a identidade externa."];
        else if (externalId.Length > FinancialCreditCardInvoiceAdjustment.ExternalIdMaxLength) errors["externalId"] = [$"A identidade externa deve ter no máximo {FinancialCreditCardInvoiceAdjustment.ExternalIdMaxLength} caracteres."];
        if (errors.Count > 0) return new(InvoiceAdjustmentAccess.Invalid, null, errors);
        var normalized = input with { Description = description, Amount = amount.ToString("0.00", CultureInfo.InvariantCulture), ExternalId = externalId };
        return await repository.CreateAsync(familyId, cardId, invoiceId, userId, normalized, clock.GetUtcNow(), cancellationToken);
    }

    public Task<InvoiceAdjustmentOperationResult> CancelAsync(
        Guid familyId, Guid cardId, Guid invoiceId, Guid adjustmentId,
        Guid userId, CancellationToken cancellationToken) =>
        repository.CancelAsync(familyId, cardId, invoiceId, adjustmentId, userId, clock.GetUtcNow(), cancellationToken);

    private static InvoiceAdjustmentOperationResult Invalid(string field, string message) =>
        new(InvoiceAdjustmentAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

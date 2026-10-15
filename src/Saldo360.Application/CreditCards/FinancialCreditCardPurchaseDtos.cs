using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed record FinancialCreditCardInstallmentDto(Guid Id, int Number, string Amount, CreditCardInstallmentStatus Status);
public sealed record FinancialCreditCardPurchaseDto(Guid Id, Guid CreditCardId, Guid CategoryId, string Description, DateOnly PurchaseDate, string TotalAmount, int InstallmentCount, CreditCardPurchaseStatus Status, IReadOnlyList<FinancialCreditCardInstallmentDto> Installments);
public sealed record CreditCardPurchaseInput(string? Description, DateOnly? PurchaseDate, decimal? TotalAmount, int? InstallmentCount, Guid? CategoryId);
public enum CreditCardPurchaseAccess { Allowed, NotFound, Invalid }
public sealed record CreditCardPurchaseResult(CreditCardPurchaseAccess Access, FinancialCreditCardPurchaseDto? Purchase, IDictionary<string, string[]>? Errors)
{
    public static CreditCardPurchaseResult NotFound { get; } = new(CreditCardPurchaseAccess.NotFound, null, null);
    public static CreditCardPurchaseResult Invalid(IDictionary<string, string[]> errors) => new(CreditCardPurchaseAccess.Invalid, null, errors);
    public static CreditCardPurchaseResult Ok(FinancialCreditCardPurchaseDto purchase) => new(CreditCardPurchaseAccess.Allowed, purchase, null);
}

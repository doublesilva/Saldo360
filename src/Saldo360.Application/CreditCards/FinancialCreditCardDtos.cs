namespace Saldo360.Application.CreditCards;

public sealed record FinancialCreditCardDto(
    Guid Id, Guid FamilyId, string Name, string Issuer, Guid? HolderUserId, string CreditLimit,
    int ClosingDay, int DueDay, Guid? DefaultPaymentAccountId, bool IsArchived,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt);

public sealed record CreditCardInput(
    string? Name, string? Issuer, Guid? HolderUserId, decimal? CreditLimit,
    int? ClosingDay, int? DueDay, Guid? DefaultPaymentAccountId);

public enum CreditCardAccess { Allowed, NotFound, Invalid }

public sealed record CreditCardResult(CreditCardAccess Access, FinancialCreditCardDto? Card, IDictionary<string, string[]>? Errors)
{
    public static CreditCardResult NotFound { get; } = new(CreditCardAccess.NotFound, null, null);
    public static CreditCardResult Invalid(IDictionary<string, string[]> errors) => new(CreditCardAccess.Invalid, null, errors);
    public static CreditCardResult Ok(FinancialCreditCardDto card) => new(CreditCardAccess.Allowed, card, null);
}

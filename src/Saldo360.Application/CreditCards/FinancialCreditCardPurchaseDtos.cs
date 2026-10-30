using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed record FinancialCreditCardInstallmentDto(Guid Id, int Number, string Amount, CreditCardInstallmentStatus Status);
public sealed record FinancialCreditCardPurchaseDto(Guid Id, Guid CreditCardId, Guid CategoryId, string Description, DateOnly PurchaseDate, string TotalAmount, int InstallmentCount, CreditCardPurchaseStatus Status, IReadOnlyList<FinancialCreditCardInstallmentDto> Installments, int PreviouslyPaidInstallments = 0, DateOnly? FirstPendingCompetence = null, Saldo360.Domain.Categories.ExpenseNature Nature = Saldo360.Domain.Categories.ExpenseNature.Undefined);
public sealed record CreditCardPurchaseInput(string? Description, DateOnly? PurchaseDate, decimal? TotalAmount, int? InstallmentCount, Guid? CategoryId,
    string AmountMode = "Total", int PreviouslyPaidInstallments = 0, DateOnly? FirstPendingCompetence = null, string? ExpenseNature = null);
public sealed record CreditCardPortfolioDto(int CardCount, string TotalLimit, string InvoiceTotal, string AvailableLimit, IReadOnlyList<CreditCardPortfolioItemDto> Cards)
{
    public string PeriodInvoiceTotal { get; init; } = "0.00";
    public string PeriodPaidTotal { get; init; } = "0.00";
    public string PeriodOutstandingTotal { get; init; } = "0.00";
    public string PeriodUnlinkedTotal { get; init; } = "0.00";
}
public sealed record CreditCardPortfolioItemDto(Guid CardId, string CreditLimit, string UsedLimit, string AvailableLimit, string UtilizationPercentage, string InvoiceTotal, int UnlinkedKnownCount, string UnlinkedKnownAmount)
{
    public string UnlinkedPeriodAmount { get; init; } = "0.00";
    public string KnownPeriodTotal { get; init; } = "0.00";
    public IReadOnlyList<KnownCardObligationDto> UnlinkedObligations { get; init; } = [];
    public int UnassignedCompetenceCount { get; init; }
    public bool IsArchived { get; init; }
    public int IncompleteDetailsCount { get; init; }
    public string PaidPeriodAmount { get; init; } = "0.00";
    public string OutstandingPeriodAmount { get; init; } = "0.00";
    public IReadOnlyList<FinancialCreditCardInvoiceDto> Invoices { get; init; } = [];
}
public sealed record KnownCardObligationDto(Guid Id, string Description, string Amount, DateOnly? Competence, DateOnly? DueDate, int? Number, int? Count, string? SourceSystem, string? ExternalReference);
public enum CreditCardPurchaseAccess { Allowed, NotFound, Invalid, Conflict }
public sealed record CreditCardPurchaseResult(CreditCardPurchaseAccess Access, FinancialCreditCardPurchaseDto? Purchase, IDictionary<string, string[]>? Errors)
{
    public static CreditCardPurchaseResult NotFound { get; } = new(CreditCardPurchaseAccess.NotFound, null, null);
    public static CreditCardPurchaseResult Invalid(IDictionary<string, string[]> errors) => new(CreditCardPurchaseAccess.Invalid, null, errors);
    public static CreditCardPurchaseResult Ok(FinancialCreditCardPurchaseDto purchase) => new(CreditCardPurchaseAccess.Allowed, purchase, null);
}

namespace Saldo360.Application.CreditCards;

public sealed record CreditCardImportItemInput(
    int RowNumber,
    string TransactionDate,
    string Description,
    string Amount,
    Guid CategoryId,
    int? InstallmentNumber,
    int? InstallmentCount,
    string? TotalAmount,
    string? ExternalId,
    string? Competence);

public sealed record CreditCardImportConfirmation(
    Guid ImportId,
    bool Confirm,
    IReadOnlyList<CreditCardImportItemInput>? Items);

public sealed record CreditCardImportItemResult(int RowNumber, string Status, Guid? PurchaseId, IReadOnlyList<string> Errors);

public sealed record CreditCardImportResult(
    Guid ImportId,
    string Status,
    int ImportedCount,
    IReadOnlyList<CreditCardImportItemResult> Items);

public enum CreditCardImportAccess { Allowed, NotFound, Invalid, Conflict }

public sealed record CreditCardImportOperationResult(
    CreditCardImportAccess Access,
    CreditCardImportResult? Result,
    IDictionary<string, string[]>? Errors,
    string? Message = null);

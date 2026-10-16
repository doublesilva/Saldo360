namespace Saldo360.Application.CreditCards;

public sealed record CreditCardPdfPreviewStatement(
    string? Issuer,
    string? CardLastDigits,
    string? Competence,
    string? ClosingDate,
    string? DueDate,
    string? TotalAmount);

public sealed record CreditCardPdfPreviewItem(
    int RowNumber,
    int Page,
    string SourceText,
    string? TransactionDate,
    string Description,
    string? Amount,
    string Type,
    int? InstallmentNumber,
    int? InstallmentCount,
    string? Competence,
    string Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record CreditCardPdfPreviewSummary(int Recognized, int Warnings, int Invalid, int Unknown);

public sealed record CreditCardPdfPreview(
    string FileName,
    int PageCount,
    string Parser,
    CreditCardPdfPreviewStatement Statement,
    CreditCardPdfPreviewSummary Summary,
    IReadOnlyList<CreditCardPdfPreviewItem> Items,
    IReadOnlyList<string> Warnings);

public enum CreditCardPdfPreviewAccess { Allowed, NotFound, Invalid }

public sealed record CreditCardPdfPreviewResult(
    CreditCardPdfPreviewAccess Access,
    CreditCardPdfPreview? Preview,
    IDictionary<string, string[]>? Errors);

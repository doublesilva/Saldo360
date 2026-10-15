namespace Saldo360.Application.CreditCards;

public sealed record CreditCardCsvPreviewRequest(
    string? DateColumn,
    string? DescriptionColumn,
    string? AmountColumn,
    string? CategoryColumn,
    string? InstallmentNumberColumn,
    string? InstallmentCountColumn,
    string? ExternalIdColumn,
    string? CompetenceColumn,
    string? Delimiter,
    string? DateFormat,
    string? DecimalFormat);

public sealed record CreditCardCsvPreviewRow(
    int RowNumber,
    string? TransactionDate,
    string Description,
    string? Amount,
    int? InstallmentNumber,
    int? InstallmentCount,
    string? ExternalId,
    string? Category,
    string? Competence,
    string Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record CreditCardCsvPreviewMapping(
    string DateColumn,
    string DescriptionColumn,
    string AmountColumn,
    string? CategoryColumn,
    string? InstallmentNumberColumn,
    string? InstallmentCountColumn,
    string? ExternalIdColumn,
    string? CompetenceColumn,
    string Delimiter,
    string? DateFormat,
    string DecimalFormat);

public sealed record CreditCardCsvPreview(
    int TotalRows,
    int ValidRows,
    int WarningRows,
    int InvalidRows,
    int IgnoredRows,
    CreditCardCsvPreviewMapping Mapping,
    IReadOnlyList<CreditCardCsvPreviewRow> Rows);

public enum CreditCardCsvPreviewAccess { Allowed, NotFound, Invalid }

public sealed record CreditCardCsvPreviewResult(
    CreditCardCsvPreviewAccess Access,
    CreditCardCsvPreview? Preview,
    IDictionary<string, string[]>? Errors);

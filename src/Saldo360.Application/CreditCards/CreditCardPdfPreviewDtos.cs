namespace Saldo360.Application.CreditCards;

public sealed record CreditCardPdfPreviewStatement(
    string? Issuer,
    string? CardLastDigits,
    string? Competence,
    string? ClosingDate,
    string? DueDate,
    string? TotalAmount)
{
    public string? HolderName { get; init; }
}

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
    IReadOnlyList<string> Warnings)
{
    public string? OriginalDescription { get; init; }
    public string? NormalizedDescription { get; init; }
    public string Nature { get; init; } = "Unknown";
    public bool IncludedInCurrentTotal { get; init; }
}

public sealed record CreditCardPdfPreviewSummary(int Recognized, int Warnings, int Invalid, int Unknown);

public sealed record CreditCardPdfPreview(
    string FileName,
    int PageCount,
    string Parser,
    CreditCardPdfPreviewStatement Statement,
    CreditCardPdfPreviewSummary Summary,
    IReadOnlyList<CreditCardPdfPreviewItem> Items,
    IReadOnlyList<string> Warnings)
{
    public CreditCardPdfSecurity Security { get; init; } = new("Unprotected");
    public CreditCardPdfReconciliation Reconciliation { get; init; } =
        new("NeedsReview", null, null, null, true);
}

public sealed record CreditCardPdfSecurity(string Status);
public sealed record CreditCardPdfReconciliation(
    string Status,
    string? OfficialTotal,
    string? ComponentsTotal,
    string? Difference,
    bool HasUnknownCurrentItems);

public enum CreditCardPdfPreviewAccess { Allowed, NotFound, Invalid }

public sealed record CreditCardPdfPreviewResult(
    CreditCardPdfPreviewAccess Access,
    CreditCardPdfPreview? Preview,
    IDictionary<string, string[]>? Errors);

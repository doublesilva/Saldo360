namespace Saldo360.Application.CreditCards;

public sealed record ImportedInstallmentMatchDto(
    Guid InstallmentId,
    Guid InvoiceId,
    string Description,
    string Amount,
    int InstallmentNumber,
    int InstallmentCount,
    string Competence,
    string TransactionDate,
    bool DescriptionSimilar);

public sealed record ImportedInstallmentInput(
    Guid ImportId,
    int RowNumber,
    Guid? InstallmentId,
    string TransactionDate,
    string Description,
    string Amount,
    int InstallmentNumber,
    int InstallmentCount,
    string? Competence,
    string? ExternalId,
    Guid? CategoryId,
    bool Confirm);

public sealed record ImportedInstallmentRecordDto(
    Guid Id,
    Guid? ImportId,
    int? RowNumber,
    Guid? InvoiceId,
    Guid? LinkedInstallmentId,
    string? TransactionDate,
    string Description,
    string Amount,
    int? InstallmentNumber,
    int? InstallmentCount,
    string? ExternalId,
    string? CategoryId,
    bool IsHistorical,
    string Status,
    string CreatedAt,
    string? CancelledAt,
    string? Competence = null, string? DueDate = null, string? SourceSystem = null, string? ExternalReference = null);

public enum ImportedInstallmentAccess { Allowed, NotFound, Invalid, Conflict }

public sealed record ImportedInstallmentOperationResult(
    ImportedInstallmentAccess Access,
    ImportedInstallmentRecordDto? Record,
    IReadOnlyList<ImportedInstallmentMatchDto>? Matches,
    IDictionary<string, string[]>? Errors,
    string? Message = null);

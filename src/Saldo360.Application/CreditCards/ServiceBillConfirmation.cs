using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards;

public sealed record ServiceBillConfirmationResult(string Status, Guid DocumentId, Guid? OccurrenceId, Guid? RecurrenceId,
    string? Competence, string? ProjectedAmount, string? ConfirmedAmount, string? Difference, string? ConfirmedAt,
    IReadOnlyList<string> Warnings);

public enum ServiceBillConfirmationStoreStatus { Confirmed, AlreadyConfirmed, Conflict, Invalid }
public sealed record ServiceBillConfirmationStoreResult(ServiceBillConfirmationStoreStatus Status, Guid? OccurrenceId = null,
    Guid? RecurrenceId = null, string? Competence = null, decimal? ProjectedAmount = null, decimal? ConfirmedAmount = null,
    DateTimeOffset? ConfirmedAt = null);

public interface IServiceBillConfirmationRepository
{
    Task<ServiceBillConfirmationStoreResult> ConfirmAsync(Guid familyId, Guid documentId, Guid userId, Guid occurrenceId,
        Guid recurrenceId, string competence, decimal confirmedAmount, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class ServiceBillConfirmationService(IServiceBillRecurringMatchPreviewService preview,
    IServiceBillConfirmationRepository repository, ServiceBillReconciliationOptions options, TimeProvider clock)
{
    public async Task<(FinancialDocumentClassificationAccess Access, ServiceBillConfirmationResult? Result,
        IDictionary<string, string[]>? Errors)> ConfirmAsync(Guid familyId, Guid documentId, Guid userId, string? password,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled || !options.ConfirmationEnabled)
            return Invalid("feature", "A confirmação de contas de serviço está desabilitada.");
        var reviewed = await preview.PreviewAsync(familyId, documentId, userId, password, cancellationToken);
        if (reviewed.Access != FinancialDocumentClassificationAccess.Allowed || reviewed.Preview is null)
            return (reviewed.Access, null, reviewed.Errors);
        var extraction = reviewed.Preview.Extraction;
        var match = reviewed.Preview.Match;
        if (extraction.Assessment != "Reliable" || match.MatchStatus != "Matched" || match.MatchedOccurrenceId is not { } occurrenceId
            || match.MatchedRecurringTemplateId is not { } recurrenceId || extraction.Result.Competence is not { } competence
            || !decimal.TryParse(extraction.Result.TotalAmount, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0 || Money.Validate(amount, "O valor confirmado") is not null)
            return Invalid("document", "O documento não possui extração e associação confiáveis para confirmação.");

        var stored = await repository.ConfirmAsync(familyId, documentId, userId, occurrenceId, recurrenceId, competence,
            amount, clock.GetUtcNow(), cancellationToken);
        if (stored.Status == ServiceBillConfirmationStoreStatus.Invalid)
            return Invalid("document", "A ocorrência deixou de ser elegível para confirmação.");
        var result = Map(documentId, stored);
        return (FinancialDocumentClassificationAccess.Allowed, result, null);
    }

    private static ServiceBillConfirmationResult Map(Guid documentId, ServiceBillConfirmationStoreResult value) =>
        new(value.Status.ToString(), documentId, value.OccurrenceId, value.RecurrenceId, value.Competence,
            value.ProjectedAmount is { } projected ? Money.Format(projected) : null,
            value.ConfirmedAmount is { } confirmed ? Money.Format(confirmed) : null,
            value.ProjectedAmount is { } p && value.ConfirmedAmount is { } c ? Money.Format(c - p) : null,
            value.ConfirmedAt?.ToString("O"), value.ProjectedAmount is { } original && value.ConfirmedAmount is { } actual
                && (actual > original * 2 || actual * 2 < original) ? ["LargeAmountDifference"] : []);

    private static (FinancialDocumentClassificationAccess, ServiceBillConfirmationResult?, IDictionary<string, string[]>?) Invalid(string field, string message) =>
        (FinancialDocumentClassificationAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

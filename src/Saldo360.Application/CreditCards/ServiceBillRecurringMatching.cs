using System.Globalization;
using System.Text;
using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards;

public enum ServiceBillRecurringMatchStatus { Matched, NotFound, Ambiguous, NeedsReview, Rejected }
public sealed record ServiceBillRecurringCandidate(Guid OccurrenceId, Guid RecurringTemplateId, string Description, string Competence,
    string ProjectedAmount, string DueDate, IReadOnlyList<string> Evidence);
public sealed record ServiceBillRecurringMatchResult(Guid DocumentId, string DocumentType, string? Competence, string? ExtractedAmount,
    string MatchStatus, Guid? MatchedOccurrenceId, Guid? MatchedRecurringTemplateId, IReadOnlyList<ServiceBillRecurringCandidate> Candidates,
    IReadOnlyList<string> Reasons, string Confidence);
public sealed record ServiceBillRecurringMatchPreview(ServiceBillExtractionOutput Extraction, ServiceBillRecurringMatchResult Match);
public sealed record ServiceBillRecurringCandidateData(Guid OccurrenceId, Guid RecurrenceId, string Description, string CategoryName,
    DateOnly DueDate, decimal ProjectedAmount);
public interface IServiceBillRecurringCandidateRepository
{
    Task<IReadOnlyList<ServiceBillRecurringCandidateData>> ListAsync(Guid familyId, string competence, CancellationToken cancellationToken);
}
public interface IServiceBillRecurringMatchService
{
    Task<ServiceBillRecurringMatchResult> MatchAsync(Guid familyId, Guid documentId, ServiceBillExtractionOutput extraction, CancellationToken cancellationToken);
}
public sealed class ServiceBillReconciliationOptions { public bool Enabled { get; set; } public bool ConfirmationEnabled { get; set; } }

public sealed class ServiceBillRecurringMatchService(IServiceBillRecurringCandidateRepository repository) : IServiceBillRecurringMatchService
{
    public async Task<ServiceBillRecurringMatchResult> MatchAsync(Guid familyId, Guid documentId, ServiceBillExtractionOutput extraction, CancellationToken cancellationToken)
    {
        var bill = extraction.Result;
        if (extraction.Assessment != "Reliable" || bill.DocumentType is not ("ElectricityBill" or "WaterBill" or "TelecomBill")
            || bill.Competence is null || bill.TotalAmount is null)
            return Result(ServiceBillRecurringMatchStatus.Rejected, [], ["ExtractionNotReliable"]);
        var data = await repository.ListAsync(familyId, bill.Competence, cancellationToken);
        var plausible = new List<ServiceBillRecurringCandidate>(); var strong = new List<ServiceBillRecurringCandidate>();
        foreach (var candidate in data)
        {
            var evidence = new List<string> { "SameCompetence", "ExpenseOccurrence", "RecurringOccurrence" };
            var combined = Normalize(candidate.Description + " " + candidate.CategoryName);
            if (!Compatible(bill.DocumentType, combined)) continue;
            evidence.Add("DocumentTypeCompatible");
            var issuer = Normalize(bill.Issuer);
            if (issuer.Length >= 3 && (Normalize(candidate.Description) == issuer || ContainsToken(combined, issuer))) evidence.Add("IssuerExactNormalizedMatch");
            if (bill.DueDate is { } due && DateOnly.TryParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var actual)
                && Math.Abs(actual.DayNumber - candidate.DueDate.DayNumber) <= 3) evidence.Add("DueDateCompatible");
            var item = new ServiceBillRecurringCandidate(candidate.OccurrenceId, candidate.RecurrenceId, candidate.Description,
                bill.Competence, Money.Format(candidate.ProjectedAmount), candidate.DueDate.ToString("yyyy-MM-dd"), evidence);
            plausible.Add(item);
            if (evidence.Contains("IssuerExactNormalizedMatch")) strong.Add(item);
        }
        if (strong.Count == 1) return Result(ServiceBillRecurringMatchStatus.Matched, plausible, [], strong[0]);
        if (strong.Count > 1) return Result(ServiceBillRecurringMatchStatus.Ambiguous, plausible, ["MultipleStrongCandidates"]);
        if (plausible.Count > 0) return Result(ServiceBillRecurringMatchStatus.NeedsReview, plausible, ["StrongIdentityEvidenceMissing"]);
        return Result(ServiceBillRecurringMatchStatus.NotFound, [], ["NoCompatibleRecurringOccurrence"]);

        ServiceBillRecurringMatchResult Result(ServiceBillRecurringMatchStatus status, IReadOnlyList<ServiceBillRecurringCandidate> candidates,
            IReadOnlyList<string> reasons, ServiceBillRecurringCandidate? matched = null) => new(documentId, bill.DocumentType, bill.Competence,
                bill.TotalAmount, status.ToString(), matched?.OccurrenceId, matched?.RecurringTemplateId, candidates, reasons,
                status == ServiceBillRecurringMatchStatus.Matched ? "High" : status == ServiceBillRecurringMatchStatus.NeedsReview ? "Medium" : "Low");
    }
    private static bool Compatible(string type, string text) => type switch
    {
        "ElectricityBill" => HasAny(text, "energia", "eletrica", "luz", "ceee"),
        "WaterBill" => HasAny(text, "agua", "esgoto", "hidrometro", "dmae"),
        "TelecomBill" => HasAny(text, "internet", "telefone", "telefonia", "telecom", "vivo"),
        _ => false,
    };
    private static bool HasAny(string text, params string[] values) => values.Any(value => ContainsToken(text, value));
    private static bool ContainsToken(string text, string value) => (" " + text + " ").Contains(" " + value + " ", StringComparison.Ordinal);
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty; var source = value.ToLowerInvariant().Normalize(NormalizationForm.FormD); var result = new StringBuilder();
        foreach (var c in source) result.Append(CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark ? "" : char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

public interface IServiceBillRecurringMatchPreviewService
{
    Task<(FinancialDocumentClassificationAccess Access, ServiceBillRecurringMatchPreview? Preview, IDictionary<string, string[]>? Errors)> PreviewAsync(
        Guid familyId, Guid id, Guid userId, string? password, CancellationToken cancellationToken);
}
public sealed class ServiceBillRecurringMatchPreviewService(ServiceBillPreviewService extraction, IServiceBillRecurringMatchService matching,
    ServiceBillReconciliationOptions options)
    : IServiceBillRecurringMatchPreviewService
{
    public async Task<(FinancialDocumentClassificationAccess Access, ServiceBillRecurringMatchPreview? Preview, IDictionary<string, string[]>? Errors)> PreviewAsync(
        Guid familyId, Guid id, Guid userId, string? password, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return (FinancialDocumentClassificationAccess.Invalid, null, new Dictionary<string, string[]> { ["feature"] = ["A reconciliação experimental está desabilitada."] });
        var extracted = await extraction.PreviewAsync(familyId, id, userId, password, cancellationToken);
        if (extracted.Access != FinancialDocumentClassificationAccess.Allowed || extracted.Preview is null) return (extracted.Access, null, extracted.Errors);
        return (FinancialDocumentClassificationAccess.Allowed, new(extracted.Preview,
            await matching.MatchAsync(familyId, id, extracted.Preview, cancellationToken)), null);
    }
}

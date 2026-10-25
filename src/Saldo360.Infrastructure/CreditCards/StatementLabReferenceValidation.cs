using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

// Offline, independent documentary expectations. Never fed to the model or used to repair its response.
public sealed record StatementLabReference(string DocumentHash, int PageCount, CreditCardStatementAgentDocument Expected);
public sealed record StatementLabReferenceDiagnostic(string Code, string Path, int? ItemIndex = null);
public sealed record StatementLabReferenceResult(bool ExactMatch, bool FinancialValidationPassed,
    IReadOnlyList<StatementLabReferenceDiagnostic> Diagnostics)
{
    public bool Approved => ExactMatch && FinancialValidationPassed;
}

public static class StatementLabReferenceValidation
{
    public static StatementLabReferenceResult Compare(string documentHash, StatementLabReference reference, CreditCardStatementExtractionResult result)
    {
        var diagnostics = new List<StatementLabReferenceDiagnostic>();
        if (reference is null || reference.Expected is null || reference.PageCount is < 1 or > 50
            || reference.DocumentHash != documentHash
            || !CreditCardStatementAgentSchema.TryValidate(JsonSerializer.Serialize(reference.Expected, CreditCardStatementAgentSchema.Json),
                reference.PageCount, out _, out _, purchasesOnly: true))
            return new(false, false, [new("ReferenceInvalidOrSourceMismatch", "$")]);
        if (result.Primary is not { } actual)
            return new(false, false, [new("ExtractionUnavailable", "$.primary")]);
        void Check(bool condition, string path) { if (!condition) diagnostics.Add(new("ReferenceFieldMismatch", path)); }
        Check(actual.PageCount == reference.PageCount, "$.pageCount");
        Check(actual.Header.DueDate == reference.Expected.Header.DueDate, "$.header.dueDate");
        Check(actual.Header.OfficialTotal == reference.Expected.Header.OfficialTotal, "$.header.officialTotal");
        Check(actual.Header.Competence == reference.Expected.Header.Competence, "$.header.competence");
        // Multiset matching: preserve identical legitimate rows and detect extra/missing copies without reordering facts.
        var remaining = actual.Items.Select((item, index) => (item, index)).ToList();
        for (var index = 0; index < reference.Expected.Items.Count; index++)
        {
            var expected = reference.Expected.Items[index];
            var found = remaining.FindIndex(candidate => Same(expected, candidate.item));
            if (found < 0) diagnostics.Add(new("ReferenceItemMissingOrDifferent", "$.items[]", index));
            else remaining.RemoveAt(found);
        }
        diagnostics.AddRange(remaining.Select(extra => new StatementLabReferenceDiagnostic("ReferenceUnexpectedOrDuplicateItem", "$.items[]", extra.index)));
        var exact = diagnostics.Count == 0;
        var financialValid = result.PrimarySource == "Agent" && result.Assessment == AgentExtractionAssessment.Reliable && result.ValidationStatus == "Valid"
            && result.Primary.Reconciliation.Status == "Reconciled" && result.AgentError is null
            && result.ParserShadowExecuted == false && result.OutputCoverage?.TechnicalComplete == true
            && (result.PurchaseDocumentValidation is null || result.PurchaseDocumentValidation is { HasContradiction: false, CoverageComplete: true }
                && result.OutputCoverage.RecognitionComplete)
            && result.PageCoverage?.Count == reference.PageCount && result.PageCoverage.All(p => p.HasImage)
            && (result.EvidenceDiagnostics?.Count ?? 0) == 0 && (result.InstallmentDiagnostics?.Count ?? 0) == 0
            // Identity provenance is reported independently; this does not authorize a card association.
            && (result.DocumentWarnings ?? []).All(w => w is "RecognitionCoverageUnproven" or "PurchasesOnlyScope"
                || result.PurchaseDocumentValidation is { HasContradiction: false, CoverageComplete: true }
                    && w is "DocumentIdentityIncomplete" or "IdentityEvidenceUnverified" or "PurchaseDatesIncomplete");
        if (!financialValid) diagnostics.Add(new("FinancialOrCoverageValidationFailed", "$.extraction"));
        return new(exact, financialValid, diagnostics);
    }

    private static bool Same(CreditCardStatementAgentItem expected, CreditCardStatementAgentItem actual) =>
        expected.OriginalDescription == actual.OriginalDescription && expected.TransactionDate == actual.TransactionDate
        && expected.Amount == actual.Amount && expected.Type == actual.Type && expected.Nature == actual.Nature
        && expected.InstallmentNumber == actual.InstallmentNumber && expected.InstallmentCount == actual.InstallmentCount
        && expected.Section == actual.Section && expected.IncludedInCurrentTotal == actual.IncludedInCurrentTotal && expected.Page == actual.Page;
}

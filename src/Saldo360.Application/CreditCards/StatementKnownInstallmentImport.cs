using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Saldo360.Application.CreditCards;

public sealed record StatementKnownInstallmentRow(int ItemIndex, DateOnly Competence, DateOnly DueDate, DateOnly? TransactionDate, int? AnchorItemIndex = null);
public sealed record StatementFinancialCalendarEvidence(bool Verified, IReadOnlyList<int> Pages, string? FailureCode = null, string? HeaderHash = null);
public sealed record StatementKnownInstallmentEligibility(bool Eligible, string Stage,
    IReadOnlyList<StatementKnownInstallmentRow> Rows, IReadOnlyList<string> Blockers)
{
    // This is an authenticated, document-wide financial decision, NOT documentary identity proof.
    public string AssociationBasis => "ExplicitDocumentConfirmationRequired";
}

public static partial class StatementKnownInstallmentImport
{
    public const string Workflow = "KnownInstallments";
    public const string SourceSystem = "InboxStatement";

    public static StatementKnownInstallmentEligibility Evaluate(CreditCardStatementExtractionResult? extraction,
        string issuer, string? lastFour, int dueDay)
    {
        static StatementKnownInstallmentEligibility Block(string code) => new(false, "Blocked", [], [code]);
        if (extraction?.FinancialClassification is not null) return Block(StatementFinancialClassifier.ReadOnlyReason);
        if (extraction is not { PrimarySource: "Agent", ParserShadowExecuted: false, ParserShadow: null,
            Assessment: AgentExtractionAssessment.Reliable, ValidationStatus: "Valid", Primary: { } primary,
            PurchaseDocumentValidation: { CoverageComplete: true, HasContradiction: false },
            OutputCoverage.RecognitionComplete: true, Suitability.FaithfulExtraction: true }
            || extraction.EvidenceDiagnostics?.Count > 0 || extraction.InstallmentDiagnostics?.Count > 0)
            return Block("FaithfulDocumentExtractionRequired");
        if (extraction.PurchaseDocumentValidation.Pages.Sum(p => p.MatchedRows) != primary.Items.Count
            || extraction.PurchaseDocumentValidation.Pages.Any(p => !p.Complete || p.EligibleRows != p.MatchedRows))
            return Block("DocumentCoverageConflict");
        if (CreditCardStatementResolver.ContradictsSelectedContext(primary.Header, issuer, lastFour)) return Block("DocumentIdentityContradiction");
        if (extraction.FinancialCalendarEvidence is not { Verified: true } calendar)
            return Block(extraction.FinancialCalendarEvidence?.FailureCode ?? "DocumentCalendarEvidenceMissing");
        if (calendar.HeaderHash != CalendarHash(primary.Header)) return Block("DocumentCalendarEvidenceConflict");
        var json = JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", primary.Header, primary.Items), CreditCardStatementAgentSchema.Json);
        if (!CreditCardStatementAgentSchema.TryValidate(json, primary.PageCount, out _, out _, purchasesOnly: true)
            || !CreditCardStatementAgentEvaluator.TryEvaluate(json, primary.FileName, primary.PageCount, out var verified, out _)
            || CreditCardStatementAgentEvaluator.Assess(verified) != AgentExtractionAssessment.Reliable
            || verified!.Reconciliation != primary.Reconciliation) return Block("FinancialValidationRequired");
        if (!DateOnly.TryParseExact(primary.Header.Competence, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var competence)
            || !DateOnly.TryParseExact(primary.Header.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)
            || due.Year != competence.Year || due.Month != competence.Month || dueDay is < 1 or > 31
            || due.Day != Math.Min(dueDay, DateTime.DaysInMonth(due.Year, due.Month))) return Block("DocumentCalendarConflict");
        if (primary.Items.Count == 0) return Block("NoKnownInstallments");
        var rows = new List<StatementKnownInstallmentRow>();
        foreach (var (item, index) in primary.Items.Select((item, index) => (item, index)))
        {
            // No conversion of a purchase to an invented 1/1 installment or original purchase.
            if (item.Type != "Installment" || item.InstallmentNumber is null || item.InstallmentCount is null
                || item.OriginalDescription.Length > 200) return Block("KnownInstallmentContractRequired");
            if (decimal.Parse(item.Amount, CultureInfo.InvariantCulture) <= 0m) return Block("KnownInstallmentAmountInvalid");
            var month = competence;
            int? anchor = null;
            if (item.Section == "FutureInstallments")
            {
                // Monthly registered card schedule + an explicit future row + a unique current occurrence.
                // Never generate absent installments; ambiguous series/dates remain blocked as a whole.
                var parents = primary.Items.Select((p, n) => (Item: p, Index: n)).Where(p => p.Item.Section == "CurrentInvoice" && p.Item.InstallmentCount == item.InstallmentCount
                    && p.Item.InstallmentNumber < item.InstallmentNumber && DescriptionKey(p.Item.OriginalDescription) == DescriptionKey(item.OriginalDescription)
                    && p.Item.TransactionDate == item.TransactionDate).ToArray();
                if (parents.Length != 1) return Block("FutureInstallmentCompetenceUnproven");
                var delta = item.InstallmentNumber.Value - parents[0].Item.InstallmentNumber!.Value;
                if (delta != 1 || competence.Year == 9999 && competence.Month == 12) return Block("FutureInstallmentCompetenceUnproven");
                month = competence.AddMonths(delta);
                anchor = parents[0].Index;
            }
            DateOnly? date = item.TransactionDate is null ? null : DateOnly.ParseExact(item.TransactionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            rows.Add(new(index, month, new(month.Year, month.Month, Math.Min(dueDay, DateTime.DaysInMonth(month.Year, month.Month))), date, anchor));
        }
        return new(true, "EligibleAwaitingConfirmation", rows, []);
    }

    // Presentation-only comparison of an already extracted description; never locates source rows.
    public static string DescriptionKey(string description) => StatementCategorization.Normalize(MarkerSuffix().Replace(description, ""));

    // Checks an already extracted header against labelled source tokens. No purchase parsing,
    // inferred year, altered agent facts or use of file name/current date/reference fixtures.
    public static StatementFinancialCalendarEvidence CorroborateCalendar(CreditCardPdfExtractedDocument source, CreditCardStatementAgentHeader? header)
    {
        if (!DateOnly.TryParseExact(header?.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expected))
            return new(false, [], "DocumentCalendarEvidenceMissing");
        var dates = new List<(int Page, DateOnly Date)>();
        foreach (var page in source.Pages)
            foreach (Match match in LabelledDueDate().Matches(page.Text))
            {
                var prefix = page.Text[Math.Max(0, match.Index - 24)..match.Index];
                if (StatementCategorization.Normalize(prefix).EndsWith("PROXIMO", StringComparison.Ordinal)
                    || StatementCategorization.Normalize(prefix).EndsWith("PROXIMA", StringComparison.Ordinal)) continue;
                if (DateOnly.TryParseExact(match.Groups["date"].Value, ["dd/MM/yyyy", "dd.MM.yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    dates.Add((page.Number, date));
            }
        if (dates.Count == 0) return new(false, [], "DocumentCalendarEvidenceMissing");
        if (dates.Any(d => d.Date != expected)) return new(false, [], "DocumentCalendarEvidenceConflict");
        return new(true, dates.Select(d => d.Page).Distinct().Order().ToArray(), HeaderHash: CalendarHash(header!));
    }

    private static string CalendarHash(CreditCardStatementAgentHeader header) => OfficialStatementAnalysisValidation.Hash($"{header.Competence}|{header.DueDate}");

    [GeneratedRegex(@"(?<![\p{L}])vencimento(?:\s+da\s+fatura)?\s*[:\-]?\s*(?<date>\d{2}/\d{2}/\d{4}|\d{2}\.\d{2}\.\d{4}|\d{4}-\d{2}-\d{2})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex LabelledDueDate();
    [GeneratedRegex(@"(?<![\d/])\d{1,2}\s*/\s*\d{1,2}\s*$")]
    private static partial Regex MarkerSuffix();
}

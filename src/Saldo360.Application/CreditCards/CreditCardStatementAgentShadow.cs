using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards;

public sealed record CreditCardStatementAgentItemDifference(string Key, string ParserValue, string AgentValue);
public enum AgentExtractionAssessment { Reliable, NeedsReview, Rejected }
public sealed record CreditCardStatementShadowSummary(bool Matches, bool HasFinancialDifference,
    bool HasClassificationDifference, bool HasCurrentTotalDifference, int DifferenceCount);
public sealed record CreditCardStatementAgentShadowComparison(
    bool HeaderMatch, bool OfficialTotalMatch, int ParserItemCount, int AgentItemCount,
    string ParserComponentsTotal, string AgentComponentsTotal,
    string ParserReconciliationStatus, string AgentReconciliationStatus,
    string? ParserDifference, string? AgentDifference,
    IReadOnlyList<string> ItemsOnlyInParser, IReadOnlyList<string> ItemsOnlyInAgent,
    IReadOnlyList<CreditCardStatementAgentItemDifference> ClassificationDifferences,
    IReadOnlyList<CreditCardStatementAgentItemDifference> IncludedInCurrentTotalDifferences);
public sealed record CreditCardStatementExtractionResult(
    string Mode,
    string PrimarySource,
    CreditCardStatementAgentPreview? Primary,
    AgentExtractionAssessment Assessment,
    bool ParserShadowExecuted,
    CreditCardStatementAgentPreview? ParserShadow,
    CreditCardStatementAgentShadowComparison? Comparison,
    CreditCardStatementShadowSummary? ShadowSummary,
    string? AgentError)
{
    public int Attempts { get; init; } = 1;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ValidationStatus { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DocumentWarnings { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StatementPageCoverage>? PageCoverage { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public StatementOutputCoverage? OutputCoverage { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CreditCardStatementAgentDocument? StructuredDocument { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StatementEvidenceDiagnostic>? EvidenceDiagnostics { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StatementInstallmentDiagnostic>? InstallmentDiagnostics { get; init; }
}
public sealed record StatementEvidenceDiagnostic(string Code, string Path, int? Page = null, int? ItemIndex = null);
public sealed record StatementPageCoverage(int Page, string TextSource, string Status, bool HasImage);
public interface ICreditCardStatementExtractionOrchestrator
{
    Task<CreditCardStatementExtractionResult> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken cancellationToken);
}
public sealed record CreditCardStatementAgentShadowPreview(CreditCardStatementAgentPreview Parser, CreditCardStatementAgentPreview Agent,
    CreditCardStatementAgentShadowComparison Comparison);
public sealed record CreditCardStatementAgentShadowResult(CreditCardPdfPreviewAccess Access,
    CreditCardStatementAgentShadowPreview? Preview, IDictionary<string, string[]>? Errors);
public interface ICreditCardStatementAgentShadowService
{
    Task<CreditCardStatementAgentShadowResult> PreviewAsync(Guid workspaceId, Guid cardId, Guid userId, Stream pdf, long length,
        string fileName, string? competence, string? password, CancellationToken cancellationToken);
}

public static partial class CreditCardStatementAgentEvaluator
{
    public static bool TryEvaluate(string response, string fileName, int pageCount, out CreditCardStatementAgentPreview? preview, out string error)
    {
        preview = null;
        if (!CreditCardStatementAgentSchema.TryValidate(response, pageCount, out var document, out error)) return false;
        if (document!.Header.OfficialTotal is null && document.Items.Count == 0)
        { error = "O documento não apresenta evidência suficiente de uma fatura de cartão."; return false; }
        var components = document.Items.Where(item => item.IncludedInCurrentTotal)
            .Sum(item => decimal.Parse(item.Amount, CultureInfo.InvariantCulture) * (item.Nature == "Credit" ? -1m : 1m));
        decimal? official = document.Header.OfficialTotal is null ? null : decimal.Parse(document.Header.OfficialTotal, CultureInfo.InvariantCulture);
        decimal? difference = official is null ? null : official.Value - components;
        var unknown = document.Items.Any(item => item.IncludedInCurrentTotal && item.Type == "Unknown");
        var status = official is null || unknown ? "NeedsReview" : difference == 0m ? "Reconciled" : "Divergent";
        preview = new(fileName, pageCount, document.Header, document.Items,
            new(status, document.Header.OfficialTotal, Money.Format(components), difference is null ? null : Money.Format(difference.Value), unknown));
        error = string.Empty;
        return true;
    }

    public static CreditCardStatementAgentShadowComparison Compare(CreditCardStatementAgentPreview parser, CreditCardStatementAgentPreview agent)
    {
        var parserByCore = parser.Items.GroupBy(CoreKey).ToDictionary(group => group.Key, group => group.ToList());
        var agentByCore = agent.Items.GroupBy(CoreKey).ToDictionary(group => group.Key, group => group.ToList());
        var parserFull = parser.Items.Select(FullKey).ToHashSet(StringComparer.Ordinal);
        var agentFull = agent.Items.Select(FullKey).ToHashSet(StringComparer.Ordinal);
        var classifications = new List<CreditCardStatementAgentItemDifference>();
        var included = new List<CreditCardStatementAgentItemDifference>();
        foreach (var key in parserByCore.Keys.Intersect(agentByCore.Keys, StringComparer.Ordinal))
        {
            var left = parserByCore[key][0]; var right = agentByCore[key][0];
            if (left.Type != right.Type) classifications.Add(new(key, left.Type, right.Type));
            if (left.IncludedInCurrentTotal != right.IncludedInCurrentTotal) included.Add(new(key, left.IncludedInCurrentTotal.ToString(), right.IncludedInCurrentTotal.ToString()));
        }
        return new(HeaderKey(parser.Header) == HeaderKey(agent.Header), parser.Header.OfficialTotal == agent.Header.OfficialTotal,
            parser.Items.Count, agent.Items.Count, parser.Reconciliation.ComponentsTotal!, agent.Reconciliation.ComponentsTotal!,
            parser.Reconciliation.Status, agent.Reconciliation.Status, parser.Reconciliation.Difference, agent.Reconciliation.Difference,
            parserFull.Except(agentFull).Order().ToList(), agentFull.Except(parserFull).Order().ToList(), classifications, included);
    }

    public static AgentExtractionAssessment Assess(CreditCardStatementAgentPreview? preview) => preview switch
    {
        null => AgentExtractionAssessment.Rejected,
        { Reconciliation.Status: "Reconciled", Header.OfficialTotal: not null }
            when !preview.Items.Any(item => item.Type == "Unknown") => AgentExtractionAssessment.Reliable,
        _ => AgentExtractionAssessment.NeedsReview,
    };

    public static CreditCardStatementShadowSummary Summarize(CreditCardStatementAgentShadowComparison value)
    {
        var financial = !value.OfficialTotalMatch || value.ParserComponentsTotal != value.AgentComponentsTotal
            || value.ParserDifference != value.AgentDifference || value.ParserReconciliationStatus != value.AgentReconciliationStatus;
        var classification = value.ClassificationDifferences.Count > 0 || value.ItemsOnlyInParser.Count > 0 || value.ItemsOnlyInAgent.Count > 0;
        var current = value.IncludedInCurrentTotalDifferences.Count > 0;
        var count = value.ItemsOnlyInParser.Count + value.ItemsOnlyInAgent.Count + value.ClassificationDifferences.Count
            + value.IncludedInCurrentTotalDifferences.Count + (financial ? 1 : 0) + (!value.HeaderMatch ? 1 : 0);
        return new(!financial && !classification && !current && value.HeaderMatch, financial, classification, current, count);
    }

    private static string HeaderKey(CreditCardStatementAgentHeader value) => string.Join('|', value.Issuer, value.CardLastDigits, value.Competence, value.ClosingDate, value.DueDate);
    private static string CoreKey(CreditCardStatementAgentItem item) => string.Join('|', Normalize(item.OriginalDescription), item.Amount, item.Nature, item.InstallmentNumber, item.InstallmentCount);
    private static string FullKey(CreditCardStatementAgentItem item) => string.Join('|', CoreKey(item), item.Type, item.Section, item.IncludedInCurrentTotal);
    private static string Normalize(string value) => Spaces().Replace(value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant(), " ");
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}

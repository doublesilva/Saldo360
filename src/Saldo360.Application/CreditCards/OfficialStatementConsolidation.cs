using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed record OfficialStatementConsolidationResult(string Status, Guid? InvoiceId = null, int? Version = null, string? OfficialTotal = null, string? FailureCode = null);
public sealed class InboxStatementConsolidationService(CardEmailSearchAuthorization authorization, ICreditCardPdfConfirmationRepository repository, IFinancialEmailCollectorLock operationLock, TimeProvider clock)
{
    public async Task<OfficialStatementConsolidationResult> ConsolidateAsync(Guid workspace, Guid card, Guid document, Guid user, CancellationToken ct)
    {
        if (!await authorization.CanSearchAsync(workspace, user, ct)) return new("NotFound");
        // Same document lease as T16.2: analysis/category decisions cannot change during the financial commit.
        await using var lease = await operationLock.TryAcquireAsync(workspace, $"statement-analysis:{document:N}", ct);
        return lease is null ? new("Busy") : await repository.ConsolidateInboxAsync(workspace, card, document, user, clock.GetUtcNow(), ct);
    }
}

public static class OfficialStatementAnalysisValidation
{
    // A parser discrepancy is diagnostic for extraction, but is not implicit permission for a financial write.
    public static string? ParserReviewReason(CreditCardStatementExtractionResult extraction) =>
        extraction.PrimarySource == "Agent" && (extraction.ShadowSummary is { HasFinancialDifference: true }
        || extraction.Comparison is { } comparison && CreditCardStatementAgentEvaluator.Assess(extraction.ParserShadow) == AgentExtractionAssessment.Reliable
            && (comparison.ItemsOnlyInParser.Count > 0 || comparison.ItemsOnlyInAgent.Count > 0))
            ? "ParserFinancialReviewRequired" : null;

    public static string DecisionHash(InboxStatementAnalysis analysis) => Hash(JsonSerializer.Serialize(new
    {
        analysis.SchemaVersion, analysis.CardId, analysis.InputHash, analysis.Extraction,
        Categories = analysis.Categories.OrderBy(c => c.ItemIndex).ThenBy(c => c.Key).ToArray(),
        Manual = analysis.ManualOverrides.OrderBy(x => x.Key).ToArray(),
        Decisions = analysis.ManualDecisions.OrderBy(x => x.Key).ToArray()
    }));
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool Revalidate(InboxStatementAnalysis analysis, Guid card, out CreditCardStatementAgentPreview? preview)
    {
        preview = null;
        if (analysis.SchemaVersion != "2.0" || analysis.CardId != card || analysis.Status != "Reliable" || analysis.ConsolidationBlockReason is not null
            || analysis.Extraction is not { Assessment: AgentExtractionAssessment.Reliable, Primary: { } primary } extraction
            || !Enum.TryParse<CreditCardStatementAgentMode>(extraction.Mode, out var mode)
            || (mode == CreditCardStatementAgentMode.AgentPrimary ? extraction.PrimarySource != "Agent" : extraction.PrimarySource != "Parser")) return false;
        var json = JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", primary.Header, primary.Items), CreditCardStatementAgentSchema.Json);
        return CreditCardStatementAgentEvaluator.TryEvaluate(json, primary.FileName, primary.PageCount, out preview, out _)
            && CreditCardStatementAgentEvaluator.Assess(preview) == AgentExtractionAssessment.Reliable
            && preview!.Reconciliation == primary.Reconciliation;
    }
}

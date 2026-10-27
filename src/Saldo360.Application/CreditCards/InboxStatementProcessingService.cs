namespace Saldo360.Application.CreditCards;

public sealed record InboxStatementProcessingResult(InboxStatementAnalysisResult Analysis, OfficialStatementConsolidationResult Consolidation);
public interface IInboxStatementProcessingService
{
    Task<InboxStatementProcessingResult> ProcessAsync(Guid workspace, Guid document, Guid candidate, Guid user, string? password, CancellationToken ct);
    Task<InboxStatementProcessingResult> AnalyzeOnlyAsync(Guid workspace, Guid document, Guid candidate, Guid user, string? password, CancellationToken ct);
}

/// <summary>Coordinates the existing analysis and atomic consolidation; owns no financial writer.</summary>
public sealed class InboxStatementProcessingService(InboxStatementAnalysisService analysis, InboxStatementConsolidationService consolidation) : IInboxStatementProcessingService
{
    public async Task<InboxStatementProcessingResult> AnalyzeOnlyAsync(Guid workspace, Guid document, Guid candidate, Guid user, string? password, CancellationToken ct)
    {
        var result = await analysis.AnalyzeAsync(workspace, document, candidate, user, password, ct, automatic: true);
        var eligible = result.Analysis is { Status: "Reliable", ConsolidationBlockReason: null }
            && (result.Analysis.Workflow != StatementKnownInstallmentImport.Workflow || result.Analysis.KnownInstallmentImport?.Eligible == true);
        // Only the explicit confirmation endpoint may call the consolidation service.
        return new(result, new(result.Status == "Busy" ? "Busy" : eligible ? "AwaitingConfirmation" : "NeedsReview",
            FailureCode: result.Analysis?.FailureCode ?? result.Analysis?.ConsolidationBlockReason));
    }

    public async Task<InboxStatementProcessingResult> ProcessAsync(Guid workspace, Guid document, Guid candidate, Guid user, string? password, CancellationToken ct)
    {
        var result = await analysis.AnalyzeAsync(workspace, document, candidate, user, password, ct, automatic: true);
        var financial = result.Analysis?.Workflow == StatementKnownInstallmentImport.Workflow
            ? new OfficialStatementConsolidationResult(result.Status == "AlreadyImported" ? "AlreadyImported"
                : result.Analysis.KnownInstallmentImport?.Eligible == true ? "AwaitingConfirmation" : "NeedsReview", FailureCode: result.Analysis.ConsolidationBlockReason)
            : result.Status == "Busy" ? new OfficialStatementConsolidationResult("Busy", FailureCode: "StatementAnalysisInProgress")
            : result.Analysis?.ConsolidationBlockReason is { } reason ? new OfficialStatementConsolidationResult("NeedsReview", FailureCode: reason)
            : result.Analysis?.Status == "Reliable" || result.Status == "Conflict"
                ? await consolidation.ConsolidateAsync(workspace, result.Analysis?.CardId ?? candidate, document, user, ct)
                : new OfficialStatementConsolidationResult("NeedsReview", FailureCode: result.Analysis?.FailureCode ?? "AnalysisRequired");
        return new(result, financial);
    }
}

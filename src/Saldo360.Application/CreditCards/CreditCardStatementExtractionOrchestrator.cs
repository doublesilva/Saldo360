namespace Saldo360.Application.CreditCards;

public sealed class CreditCardStatementExtractionOrchestrator(
    CreditCardStatementAgentOptions options,
    IParserBackedCreditCardStatementExtractorAgent parser,
    LlmCreditCardStatementExtractorAgent agent) : ICreditCardStatementExtractionOrchestrator
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("Saldo360.CreditCardStatementAgent", "1.0");
    private static readonly System.Diagnostics.Metrics.Histogram<double> Duration = Meter.CreateHistogram<double>("saldo360.statement.extraction.duration", "ms");
    private static readonly System.Diagnostics.Metrics.Counter<long> Results = Meter.CreateCounter<long>("saldo360.statement.extraction.results");

    public async Task<CreditCardStatementExtractionResult> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken cancellationToken)
    {
        return options.Mode switch
        {
            CreditCardStatementAgentMode.AgentPrimary => await AgentPrimaryAsync(request, cancellationToken),
            CreditCardStatementAgentMode.Shadow => await ParserPrimaryAsync(request, true, cancellationToken),
            _ => await ParserPrimaryAsync(request, false, cancellationToken),
        };
    }

    private async Task<CreditCardStatementExtractionResult> AgentPrimaryAsync(CreditCardStatementAgentRequest request, CancellationToken cancellationToken)
    {
        var parserResult = await EvaluateAsync(parser, "parser", request, cancellationToken);
        var agentResult = await EvaluateAsync(agent, "agent", request, cancellationToken);
        if (agentResult.Preview is null)
        {
            Record(options.Mode, "agent", request.Document.Pages.Count, null, AgentExtractionAssessment.Rejected, false);
            return new(options.Mode.ToString(), "Agent", null, AgentExtractionAssessment.Rejected, true,
                parserResult.Preview, null, null, agentResult.Error);
        }
        var comparison = parserResult.Preview is null ? null : CreditCardStatementAgentEvaluator.Compare(parserResult.Preview, agentResult.Preview);
        var assessment = CreditCardStatementAgentEvaluator.Assess(agentResult.Preview);
        Record(options.Mode, "agent", request.Document.Pages.Count, agentResult.Preview, assessment, comparison is not null && !CreditCardStatementAgentEvaluator.Summarize(comparison).Matches);
        return new(options.Mode.ToString(), "Agent", agentResult.Preview, assessment, true,
            parserResult.Preview, comparison, comparison is null ? null : CreditCardStatementAgentEvaluator.Summarize(comparison), null);
    }

    private async Task<CreditCardStatementExtractionResult> ParserPrimaryAsync(CreditCardStatementAgentRequest request, bool shadow, CancellationToken cancellationToken)
    {
        var parserResult = await EvaluateAsync(parser, "parser", request, cancellationToken);
        if (parserResult.Preview is null)
            return new(options.Mode.ToString(), "Parser", null, AgentExtractionAssessment.Rejected, false, null, null, null, parserResult.Error);
        if (!shadow)
            return new(options.Mode.ToString(), "Parser", parserResult.Preview, CreditCardStatementAgentEvaluator.Assess(parserResult.Preview), false, null, null, null, null);
        var agentResult = await EvaluateAsync(agent, "agent", request, cancellationToken);
        var comparison = agentResult.Preview is null ? null : CreditCardStatementAgentEvaluator.Compare(parserResult.Preview, agentResult.Preview);
        return new(options.Mode.ToString(), "Parser", parserResult.Preview, CreditCardStatementAgentEvaluator.Assess(parserResult.Preview), true,
            null, comparison, comparison is null ? null : CreditCardStatementAgentEvaluator.Summarize(comparison), agentResult.Error);
    }

    private static async Task<(CreditCardStatementAgentPreview? Preview, string? Error)> EvaluateAsync(
        ICreditCardStatementExtractorAgent extractor, string source, CreditCardStatementAgentRequest request, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var json = await extractor.ExtractAsync(request, cancellationToken);
            return CreditCardStatementAgentEvaluator.TryEvaluate(json, request.FileName, request.Document.Pages.Count, out var preview, out var error)
                ? (preview, null) : (null, error);
        }
        catch (CreditCardStatementAgentUnavailableException) { return (null, "Provider experimental indisponível."); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return (null, "Falha sanitizada na extração experimental."); }
        finally { Duration.Record(started.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("source", source)); }
    }

    private static void Record(CreditCardStatementAgentMode mode, string primarySource, int pages, CreditCardStatementAgentPreview? preview,
        AgentExtractionAssessment assessment, bool shadowHasDifferences) => Results.Add(1,
        new("mode", mode.ToString()), new("primary_source", primarySource), new("pages", pages),
        new("items", preview?.Items.Count ?? 0), new("assessment", assessment.ToString()),
        new("reconciliation", preview?.Reconciliation.Status ?? "Rejected"), new("shadow_has_differences", shadowHasDifferences));
}

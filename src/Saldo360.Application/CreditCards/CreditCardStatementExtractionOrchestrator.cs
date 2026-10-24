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
        if (request.RequireAgentIdentity)
        {
            if (!options.Enabled || options.Mode == CreditCardStatementAgentMode.Disabled)
                return new(options.Mode.ToString(), "Agent", null, AgentExtractionAssessment.Rejected, false, null, null, null, "AgentUnavailable");
            return await AgentPrimaryAsync(request, cancellationToken);
        }
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
        var attempts = 1;
        var officialAnchor = parserResult.Preview?.Header.OfficialTotal ?? agentResult.Preview?.Header.OfficialTotal;
        if (request.ReconcileWithRetry && agentResult.Preview?.Reconciliation.Status == "Divergent")
        {
            attempts++;
            agentResult = await EvaluateAsync(agent, "agent", request with
            {
                CorrectionInstruction = $"Releia as evidências: componentes omitidos, créditos, pagamentos e parcelas futuras. Total oficial imutável: {officialAnchor ?? "desconhecido"}. Diferença anterior: {agentResult.Preview.Reconciliation.Difference}. Não fabrique ajustes ou lançamentos."
            }, cancellationToken);
        }
        if (agentResult.Preview is null)
        {
            Record(options.Mode, "agent", request.Document.Pages.Count, null, AgentExtractionAssessment.Rejected, false);
            return new(options.Mode.ToString(), "Agent", null, AgentExtractionAssessment.Rejected, true,
                parserResult.Preview, null, null, agentResult.Error) { Attempts = attempts };
        }
        var comparison = parserResult.Preview is null ? null : CreditCardStatementAgentEvaluator.Compare(parserResult.Preview, agentResult.Preview);
        var assessment = CreditCardStatementAgentEvaluator.Assess(agentResult.Preview);
        var identityMissing = request.RequireAgentIdentity && !IdentityGrounded(agentResult.Preview.Header, request.Document);
        if (request.ReconcileWithRetry && (!Grounded(agentResult.Preview, request.Document)
            || identityMissing
            || officialAnchor != agentResult.Preview.Header.OfficialTotal
            || (CreditCardStatementAgentEvaluator.Assess(parserResult.Preview) == AgentExtractionAssessment.Reliable
                && comparison is { } reliableComparison && (reliableComparison.ItemsOnlyInParser.Count > 0 || reliableComparison.ItemsOnlyInAgent.Count > 0))))
            assessment = AgentExtractionAssessment.Rejected;
        Record(options.Mode, "agent", request.Document.Pages.Count, agentResult.Preview, assessment, comparison is not null && !CreditCardStatementAgentEvaluator.Summarize(comparison).Matches);
        return new(request.RequireAgentIdentity ? "AgentPrimary" : options.Mode.ToString(), "Agent", agentResult.Preview, assessment, true,
            parserResult.Preview, comparison, comparison is null ? null : CreditCardStatementAgentEvaluator.Summarize(comparison),
            assessment == AgentExtractionAssessment.Rejected ? identityMissing ? "CardIdentificationRequired" : "EvidenceOrOfficialTotalMismatch" : null) { Attempts = attempts };
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

    public static bool IdentityGrounded(CreditCardStatementAgentHeader header, CreditCardPdfExtractedDocument document)
    {
        if (string.IsNullOrWhiteSpace(header.IdentityEvidence) || header.IdentityPage is null
            || string.IsNullOrWhiteSpace(header.Issuer) || string.IsNullOrWhiteSpace(header.CardLastDigits)) return false;
        var evidence = header.IdentityEvidence;
        return document.Pages.Any(p => p.Number == header.IdentityPage && p.Text.Contains(evidence, StringComparison.OrdinalIgnoreCase))
            && new[] { header.Issuer, header.CardLastDigits, header.CardProduct, header.HolderName }
                .Where(v => v is not null).All(v => StatementCategorization.Normalize(evidence).Contains(StatementCategorization.Normalize(v!), StringComparison.Ordinal));
    }

    private static bool Grounded(CreditCardStatementAgentPreview preview, CreditCardPdfExtractedDocument document)
    {
        static bool HasAmount(string text, string money)
        {
            if (!decimal.TryParse(money, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var amount)) return false;
            var variants = new[] { money, amount.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")), amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) };
            return variants.Any(value => System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![\d.,]){System.Text.RegularExpressions.Regex.Escape(value)}(?![\d.,])"));
        }
        if (preview.Header.OfficialTotal is { } total && !document.Pages.Any(page => HasAmount(page.Text, total))) return false;
        foreach (var duplicate in preview.Items.GroupBy(item => (item.Page, item.Evidence)).Where(group => group.Count() > 1))
        {
            var text = document.Pages.FirstOrDefault(page => page.Number == duplicate.Key.Page)?.Text ?? string.Empty;
            var count = 0; var position = 0;
            while ((position = text.IndexOf(duplicate.Key.Evidence, position, StringComparison.OrdinalIgnoreCase)) >= 0)
            { count++; position += duplicate.Key.Evidence.Length; }
            if (count < duplicate.Count()) return false;
        }
        if (preview.Items.Any(item => item.Type is "Purchase" or "Installment" or "Fee"
            && StatementCategorization.Normalize(item.OriginalDescription) is "TOTAL" or "TOTALDAFATURA" or "VALORTOTAL" or "AJUSTEDECONCILIACAO")) return false;
        return preview.Items.All(item => document.Pages.Any(page => page.Number == item.Page
            && page.Text.Contains(item.Evidence, StringComparison.OrdinalIgnoreCase) && HasAmount(item.Evidence, item.Amount)
            && StatementCategorization.Normalize(item.Evidence).Contains(StatementCategorization.Normalize(item.OriginalDescription), StringComparison.Ordinal)));
    }

    private static void Record(CreditCardStatementAgentMode mode, string primarySource, int pages, CreditCardStatementAgentPreview? preview,
        AgentExtractionAssessment assessment, bool shadowHasDifferences) => Results.Add(1,
        new("mode", mode.ToString()), new("primary_source", primarySource), new("pages", pages),
        new("items", preview?.Items.Count ?? 0), new("assessment", assessment.ToString()),
        new("reconciliation", preview?.Reconciliation.Status ?? "Rejected"), new("shadow_has_differences", shadowHasDifferences));
}

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
        if (request.RequireAgent || request.PurchasesOnly)
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
        (CreditCardStatementAgentPreview? Preview, string? Error) parserResult = (null, null);
        var batched = request.BatchPages ? await agent.ExtractBatchesAsync(request, cancellationToken) : null;
        CreditCardStatementAgentPreview? financial = null;
        var validationError = batched?.Coverage.Batches.FirstOrDefault(b => b.FailureCode is not null)?.FailureCode ?? "OutputCoverageIncomplete";
        if (batched?.Document is not null)
            CreditCardStatementAgentEvaluator.TryEvaluate(System.Text.Json.JsonSerializer.Serialize(batched.Document, CreditCardStatementAgentSchema.Json),
                request.FileName, request.Document.Pages.Max(p => p.Number), out financial, out validationError);
        (CreditCardStatementAgentPreview? Preview, string? Error) agentResult = batched is null
            ? await EvaluateAsync(agent, "agent", request, cancellationToken) : (financial, financial is null ? validationError : null);
        var attempts = 1;
        // No successful batch is repeated within the operation; divergence is returned for review.
        var officialAnchor = agentResult.Preview?.Header.OfficialTotal;
        if (batched is null && !request.PurchasesOnly && request.ReconcileWithRetry && request.AllowCorrectionRetry && agentResult.Preview?.Reconciliation.Status == "Divergent")
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
            return new(options.Mode.ToString(), "Agent", null, AgentExtractionAssessment.Rejected, false,
                parserResult.Preview, null, null, agentResult.Error) { Attempts = attempts, ValidationStatus = "Rejected", OutputCoverage = batched?.Coverage, StructuredDocument = batched?.Document,
                    PageCoverage = batched is null ? null : request.Document.Pages.Select(p => new StatementPageCoverage(p.Number, p.TextSource, p.ProcessingStatus, p.ImagePng is not null)).ToArray() };
        }
        var comparison = parserResult.Preview is null ? null : CreditCardStatementAgentEvaluator.Compare(parserResult.Preview, agentResult.Preview);
        var assessment = CreditCardStatementAgentEvaluator.Assess(agentResult.Preview);
        var invalidEvidence = !Grounded(agentResult.Preview, request.Document);
        var visualOnly = agentResult.Preview.Items.Where(i => i.Evidence.StartsWith("VISUAL:", StringComparison.Ordinal)).ToArray();
        var visualReview = visualOnly.Length > 0 && visualOnly.All(i => request.Document.Pages.Any(p => p.Number == i.Page && p.ImagePng is not null))
            && Grounded(agentResult.Preview with { Header = agentResult.Preview.Header with { OfficialTotal = null },
                Items = agentResult.Preview.Items.Except(visualOnly).ToArray() }, request.Document);
        if (visualReview) invalidEvidence = false; // Visual facts are reviewable, never declared text-grounded or reliable.
        var incomplete = request.Document.Pages.Any(p => p.ProcessingStatus is "Unavailable" or "Unreadable" or "ImageLimitExceeded");
        var ocrReview = request.Document.Pages.Any(p => p.TextSource == "OCR");
        var changedTotal = officialAnchor != agentResult.Preview.Header.OfficialTotal;
        if (invalidEvidence || changedTotal) assessment = AgentExtractionAssessment.Rejected;
        else if (visualReview || incomplete || ocrReview || batched is not null && (batched.Warnings.Count > 0 || !batched.Coverage.TechnicalComplete)) assessment = AgentExtractionAssessment.NeedsReview;
        var warnings = IdentityWarnings(agentResult.Preview.Header, request.Document).ToList();
        if (batched is not null) { warnings.AddRange(batched.Warnings); warnings.Add("RecognitionCoverageUnproven"); }
        if (request.PurchasesOnly) warnings.Add("PurchasesOnlyScope");
        if (visualReview) warnings.Add("VisualEvidenceRequiresReview");
        if (incomplete) warnings.Add("DocumentCoverageIncomplete");
        if (ocrReview) warnings.Add("OCRRequiresReview");
        if (comparison is not null && !CreditCardStatementAgentEvaluator.Summarize(comparison).Matches) warnings.Add("ParserAgentDifference");
        Record(options.Mode, "agent", request.Document.Pages.Count, agentResult.Preview, assessment, comparison is not null && !CreditCardStatementAgentEvaluator.Summarize(comparison).Matches);
        return new(request.RequireAgent ? "AgentPrimary" : options.Mode.ToString(), "Agent", agentResult.Preview, assessment, false,
            parserResult.Preview, comparison, comparison is null ? null : CreditCardStatementAgentEvaluator.Summarize(comparison),
            invalidEvidence ? "FinancialEvidenceInvalid" : changedTotal ? "OfficialTotalChanged" : null)
        { Attempts = attempts, ValidationStatus = invalidEvidence || changedTotal ? "Rejected" : visualReview || incomplete || ocrReview ? "NeedsReview" : "Valid", DocumentWarnings = warnings.Count == 0 ? null : warnings,
            PageCoverage = request.Document.Pages.Select(p => new StatementPageCoverage(p.Number, p.TextSource, p.ProcessingStatus, p.ImagePng is not null)).ToArray(), OutputCoverage = batched?.Coverage, StructuredDocument = batched?.Document };
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
            if (request.PurchasesOnly)
            {
                if (!CreditCardStatementAgentSchema.TryValidate(json, request.Document.Pages.Count, out _, out var schemaError, purchasesOnly: true)) return (null, schemaError);
            }
            return CreditCardStatementAgentEvaluator.TryEvaluate(json, request.FileName, request.Document.Pages.Count, out var preview, out var error)
                ? (preview, null) : (null, error);
        }
        catch (CreditCardStatementAgentUnavailableException exception) { return (null, exception.FailureCode ?? "Provider experimental indisponível."); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return (null, "Falha sanitizada na extração experimental."); }
        finally { Duration.Record(started.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("source", source)); }
    }

    private static IEnumerable<string> IdentityWarnings(CreditCardStatementAgentHeader header, CreditCardPdfExtractedDocument document)
    {
        if (string.IsNullOrWhiteSpace(header.Issuer) || string.IsNullOrWhiteSpace(header.CardLastDigits)) yield return "DocumentIdentityIncomplete";
        if (header.IdentityEvidence is { } evidence && (header.IdentityPage is null
            || !document.Pages.Any(p => p.Number == header.IdentityPage && p.Text.Contains(evidence, StringComparison.OrdinalIgnoreCase))
            || !new[] { header.Issuer, header.CardLastDigits, header.CardProduct, header.HolderName }
                .Where(v => v is not null).All(v => StatementCategorization.Normalize(evidence).Contains(StatementCategorization.Normalize(v!), StringComparison.Ordinal))))
            yield return "IdentityEvidenceUnverified";
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

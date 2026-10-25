using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Saldo360.Application.CreditCards;

public sealed record StatementOutputPage(int Page, int Batch, bool Prepared, bool Sent, bool ValidReturn, int ItemCount, string Status, string? FailureCode);
public sealed record StatementOutputBatch(int Batch, IReadOnlyList<int> Pages, int ItemCount, string Status, string? FailureCode);
public sealed record StatementOutputCoverage(int TotalPages, IReadOnlyList<StatementOutputPage> Pages, IReadOnlyList<StatementOutputBatch> Batches)
{
    public bool TechnicalComplete => Pages.Count == TotalPages && Pages.All(p => p.Prepared && p.Sent && p.ValidReturn);
    public bool RecognitionComplete => false; // Neither JSON validity nor a model assertion proves recognition completeness.
    public int PreparedPages => Pages.Count(p => p.Prepared);
    public int SentPages => Pages.Count(p => p.Sent);
    public int ValidReturnPages => Pages.Count(p => p.ValidReturn);
    public int InconclusivePages => Pages.Count(p => p.Status == "Inconclusive");
    public int FailedPages => Pages.Count(p => p.Status == "Failed");
}
public sealed record StatementBatchExtraction(CreditCardStatementAgentDocument? Document, StatementOutputCoverage Coverage, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<StatementInstallmentDiagnostic> InstallmentDiagnostics { get; init; } = [];
}

public sealed partial class LlmCreditCardStatementExtractorAgent
{
    public async Task<StatementBatchExtraction> ExtractBatchesAsync(CreditCardStatementAgentRequest request, CancellationToken ct)
    {
        var pages = request.Document.Pages.OrderBy(p => p.Number).ToArray();
        if (pages.Length is 0 or > 50 || pages.Select(p => p.Number).Distinct().Count() != pages.Length || pages.Any(p => p.Number < 1))
            throw new CreditCardStatementAgentUnavailableException("Contexto de páginas inválido.", failureCode: "InvalidPageContext");
        var outputPages = new List<StatementOutputPage>(); var outputBatches = new List<StatementOutputBatch>();
        var diagnostics = new List<StatementInstallmentDiagnostic>();
        var items = new List<CreditCardStatementAgentItem>(); var warnings = new HashSet<string>(StringComparer.Ordinal);
        var headers = new JsonObject(); var conflicts = new HashSet<string>(StringComparer.Ordinal);
        var fields = new[] { "issuer", "cardLastDigits", "competence", "closingDate", "dueDate", "officialTotal", "cardProduct", "holderName", "identityEvidence", "identityPage" };
        var batches = pages.Chunk(6).ToArray(); // <=6 * 4 MiB, below the existing 12-image /24-MiB request limits.
        var providerFailed = false;
        for (var n = 0; n < batches.Length; n++)
        {
            ct.ThrowIfCancellationRequested(); var batch = batches[n]; var batchNumber = n + 1;
            string? failure = null; CreditCardStatementAgentDocument? document = null; var sent = false;
            if (providerFailed) failure = "ProviderStoppedAfterFailure";
            else if (batch.Any(p => !Prepared(p))) failure = "PagePreparationIncomplete";
            else try
            {
                sent = true;
                var json = await CompletePagesAsync(request with { Document = new(batch), BatchPages = true }, ct);
                if (!CreditCardStatementAgentSchema.TryValidate(json, pages.Max(p => p.Number), out document, out var error, out var batchDiagnostics, request.PurchasesOnly)) failure = error;
                else if (document!.SchemaVersion != "2.0") failure = "BatchSchemaVersionInvalid";
                else if (document.Items.Any(i => !batch.Any(p => p.Number == i.Page)) || document.Header.IdentityPage is { } ip && !batch.Any(p => p.Number == ip)) failure = "BatchPageMismatch";
                diagnostics.AddRange(batchDiagnostics.Select(d => d with { Batch = batchNumber }));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (CreditCardStatementAgentUnavailableException e)
            {
                providerFailed = true;
                failure = e.FailureCode is "ProviderOutputTruncated" or "ProviderIncompleteResponse" or "ProviderRefused" or "ProviderResponseLimitExceeded"
                    or "ProviderAuthenticationFailed" or "ProviderPermissionDenied" or "ProviderQuotaExceeded" or "ProviderRateLimited"
                    or "ProviderModelUnavailable" or "ProviderInvalidRequest" or "ProviderNetworkFailure" or "ProviderTimeout"
                    or "ProviderHttpFailure" or "ProviderNoStructuredOutput" or "InvalidProviderResponse" or "LabCallBudgetExceeded"
                    ? e.FailureCode : "BatchProviderUnavailable";
            }
            catch (Exception) { failure = "BatchProcessingFailed"; }
            var valid = failure is null && document is not null;
            foreach (var page in batch)
            {
                var count = valid ? document!.Items.Count(i => i.Page == page.Number) : 0;
                var inconclusive = valid && (count == 0 || HasUnrepresentedRows(page.Text, document!.Items.Where(i => i.Page == page.Number)));
                var status = !valid ? "Failed" : inconclusive ? "Inconclusive" : "ReturnedValidUnproven";
                outputPages.Add(new(page.Number, batchNumber, Prepared(page), sent, valid, count, status, failure));
                if (inconclusive) warnings.Add(count == 0 ? "PageWithoutItemsUnproven" : "UnextractedDocumentRows");
            }
            outputBatches.Add(new(batchNumber, batch.Select(p => p.Number).ToArray(), valid ? document!.Items.Count : 0, valid ? "ReturnedValidUnproven" : "Failed", failure));
            if (!valid) { warnings.Add("OutputCoverageIncomplete"); continue; }
            // Stable sort preserves the model's within-page order and all identical-but-distinct transactions.
            items.AddRange(document!.Items.OrderBy(i => i.Page));
            var header = JsonSerializer.SerializeToNode(document.Header, CreditCardStatementAgentSchema.Json)!;
            foreach (var field in fields)
            {
                if (conflicts.Contains(field) || header[field] is null) continue;
                if (headers[field] is not null && !JsonNode.DeepEquals(headers[field], header[field]))
                { conflicts.Add(field); headers[field] = null; warnings.Add("BatchHeaderConflict:" + field); }
                else headers[field] = header[field]?.DeepClone();
            }
        }
        foreach (var field in fields) if (!headers.ContainsKey(field)) headers[field] = null;
        if (items.Count > 2000) { warnings.Add("OutputItemLimitExceeded"); items = items.Take(2000).ToList(); }
        var merged = outputBatches.Any(b => b.Status != "Failed") ? new CreditCardStatementAgentDocument("2.0",
            headers.Deserialize<CreditCardStatementAgentHeader>(CreditCardStatementAgentSchema.Json)!, items) : null;
        return new(merged, new(pages.Length, outputPages, outputBatches), warnings.Order(StringComparer.Ordinal).ToArray())
        { InstallmentDiagnostics = diagnostics };
    }

    private static bool Prepared(CreditCardPdfExtractedPage page) => page.ProcessingStatus is not ("Unavailable" or "Unreadable" or "ImageLimitExceeded")
        && (!string.IsNullOrWhiteSpace(page.Text) || page.ImagePng is not null);

    private static bool HasUnrepresentedRows(string text, IEnumerable<CreditCardStatementAgentItem> items)
    {
        var evidence = items.Select(i => i.Evidence).ToList();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Conservative omission indicator, not a financial parser: no amounts, dates or transactions are created.
            if (!RowCandidate().IsMatch(line)) continue;
            var found = evidence.FindIndex(e => e.Contains(line.Trim(), StringComparison.OrdinalIgnoreCase) || line.Contains(e, StringComparison.OrdinalIgnoreCase));
            if (found < 0) return true;
            evidence.RemoveAt(found); // Identical rows require distinct returned occurrences.
        }
        return false;
    }
    [GeneratedRegex(@"^\s*(?:\d{2}/\d{2}(?:/\d{4})?|\d{4}-\d{2}-\d{2})\s+.+\d[.,]\d{2}\s*$")]
    private static partial Regex RowCandidate();
}

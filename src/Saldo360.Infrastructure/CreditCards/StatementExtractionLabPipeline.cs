using Microsoft.Extensions.Logging.Abstractions;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

// Deliberately explicit construction: no host, database, Inbox, Gmail, scheduler or financial writer.
public sealed class StatementExtractionLabPipeline : IDisposable
{
    private readonly BudgetProvider budget;
    private readonly ClientFactory clients = new();
    private readonly string image;
    public int Calls => budget.Calls;
    public StatementExtractionLabPipeline(string apiKey, string model, string dockerImage, int maxCalls,
        string reasoningEffort = "medium", int maxOutputTokens = 32768, string imageDetail = "high")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCalls, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCalls, 20);
        var tuning = new OpenAiStatementRequestTuning(reasoningEffort, maxOutputTokens, imageDetail);
        tuning.Validate(model);
        image = dockerImage;
        var provider = new OpenAiCreditCardStatementAgentProvider(clients, new()
        { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = apiKey, Model = model }, NullLogger<OpenAiCreditCardStatementAgentProvider>.Instance, tuning);
        budget = new(provider, maxCalls);
    }
    public async Task<CreditCardStatementExtractionResult> ExtractAsync(Stream pdf, string? password, CancellationToken ct)
    {
        var text = new CreditCardPdfTextExtractor(new LocalStatementPageReader(image), batchPages: true, renderAllPages: true);
        var read = await new PdfPasswordReader(text, new ManualOnlyPdfPasswordProvider()).ReadAsync(pdf,
            new(Guid.Empty, Guid.Empty), password, ct, allowAutomatic: false);
        if (read.Document.Pages.Sum(p => (long)p.Text.Length) > 200000 || read.Document.Pages.Any(p => p.Text.Length > 40000))
            throw new CreditCardStatementAgentUnavailableException("Limite de entrada excedido.", failureCode: "LabTextLimitExceeded");
        var request = new CreditCardStatementAgentRequest("local-statement.pdf", read.Document, null)
        { RequireAgent = true, AllowCorrectionRetry = false, BatchPages = true, PurchasesOnly = true, ValidatePurchaseDocument = true };
        // Render failure on native-text pages must not silently downgrade the lab to text-only extraction.
        if (read.Document.Pages.Any(p => p.ImagePng is null || p.ProcessingStatus is "Unavailable" or "Unreadable" or "ImageLimitExceeded"))
            throw new CreditCardStatementAgentUnavailableException("Cobertura visual incompleta.", failureCode: "LabPageImagesIncomplete");
        if ((read.Document.Pages.Count + 5) / 6 > budget.Remaining)
            throw new CreditCardStatementAgentUnavailableException("Orçamento insuficiente.", failureCode: "LabCallBudgetExceeded");
        return await new CreditCardStatementExtractionOrchestrator(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary },
            new ForbiddenParser(), new(budget)).ExtractAsync(request, ct);
    }
    public void Dispose() => clients.Dispose();
    private sealed class ForbiddenParser : IParserBackedCreditCardStatementExtractorAgent
    { public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken ct) => throw new InvalidOperationException("LabParserForbidden"); }
    private sealed class ClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new("https://api.openai.com/v1/"), Timeout = TimeSpan.FromSeconds(180) };
        public HttpClient CreateClient(string name) => client;
        public void Dispose() => client.Dispose();
    }
    internal sealed class BudgetProvider(ICreditCardStatementAgentProvider inner, int max) : ICreditCardStatementAgentProvider
    {
        public int Calls { get; private set; }
        public int Remaining => max - Calls;
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct)
        {
            if (Remaining <= 0) throw new CreditCardStatementAgentUnavailableException("Orçamento excedido.", failureCode: "LabCallBudgetExceeded");
            Calls++; return inner.CompleteAsync(request, ct);
        }
    }
}

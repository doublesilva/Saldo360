using Microsoft.Extensions.Logging;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

// Composition of the homologated components, not another extraction/import engine.
internal sealed class StatementDocumentaryPipeline(IHttpClientFactory clients, CreditCardStatementAgentOptions options,
    LocalStatementPageReader reader, ICreditCardPdfPasswordProvider passwords,
    ILogger<OpenAiCreditCardStatementAgentProvider> logger, CardEmailSearchAuthorization authorization) : IStatementDocumentaryPipeline
{
    public async Task<CreditCardStatementExtractionResult> ExtractAsync(Stream pdf, PdfPasswordContext context, string? password,
        string fileName, string? competence, StatementCategoryContext? categories, CancellationToken ct)
    {
        var profile = options.DocumentaryImport;
        if (!profile.Enabled) throw new CreditCardStatementAgentUnavailableException("Perfil desabilitado.", failureCode: "DocumentaryImportDisabled");
        if (context.CardId is null || !await authorization.CanSearchAsync(context.WorkspaceId, context.UserId, ct))
            throw new CreditCardStatementAgentUnavailableException("Contexto não autorizado.", failureCode: "DocumentaryContextNotAuthorized");
        var tuning = new OpenAiStatementRequestTuning(profile.ReasoningEffort, profile.MaxOutputTokens, "high");
        tuning.Validate(profile.Model);
        var read = await new PdfPasswordReader(new CreditCardPdfTextExtractor(reader, batchPages: true, renderAllPages: true), passwords)
            .ReadAsync(pdf, context, password, ct);
        if (read.Document.Pages.Count == 0 || read.Document.Pages.Count > 6)
            throw new CreditCardStatementAgentUnavailableException("Orçamento documental excedido.", failureCode: "StatementCallBudgetExceeded");
        if (read.Document.Pages.Any(p => p.ImagePng is null || p.Text.Length > 40000
            || p.ProcessingStatus is "Unavailable" or "Unreadable" or "ImageLimitExceeded")
            || read.Document.Pages.Sum(p => (long)p.Text.Length) > 200000)
            throw new CreditCardStatementAgentUnavailableException("Preparação incompleta.", failureCode: "StatementPagePreparationIncomplete");
        var configured = new CreditCardStatementAgentOptions
        {
            Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, Model = profile.Model,
            Provider = options.Provider, Endpoint = options.Endpoint, ApiKey = options.ApiKey, ApiKeyFile = options.ApiKeyFile
        };
        if (categories is not null)
        {
            var text = StatementCategorization.Normalize(string.Join("\n", read.Document.Pages.Select(p => p.Text)));
            categories = categories with { History = categories.History.Where(h => text.Contains(StatementCategorization.Normalize(h.Description), StringComparison.Ordinal)).Take(200).ToArray() };
        }
        var provider = new OpenAiCreditCardStatementAgentProvider(clients, configured, logger, tuning, "CreditCardStatementDocumentary");
        var result = await new CreditCardStatementExtractionOrchestrator(configured, new ForbiddenParser(), new(provider)).ExtractAsync(
            new(fileName, read.Document, competence)
            {
                Categories = categories, PurchasesOnly = true, ValidatePurchaseDocument = true,
                RequireAgent = true, BatchPages = true, AllowCorrectionRetry = false,
                AuthorizedIdentity = context.Card is { } card ? new(card.Issuer, card.LastFourDigits) : null
            }, ct);
        return result with { FinancialCalendarEvidence = StatementKnownInstallmentImport.CorroborateCalendar(read.Document, result.Primary?.Header) };
    }

    private sealed class ForbiddenParser : IParserBackedCreditCardStatementExtractorAgent
    {
        public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("DocumentaryParserForbidden");
    }
}

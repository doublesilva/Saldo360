namespace Saldo360.Application.CreditCards;

public sealed class CreditCardStatementAgentPreviewService(
    ICreditCardPdfContextRepository context,
    ICreditCardPdfTextExtractor pdfExtractor,
    ICreditCardPdfPasswordProvider passwords,
    ICreditCardStatementExtractionOrchestrator orchestrator,
    CreditCardStatementAgentOptions? options = null, IStatementDocumentaryPipeline? documentary = null) : ICreditCardStatementAgentPreviewService
{
    public Task<CreditCardStatementAgentPreviewResult> PreviewDiagnosticAsync(Guid workspaceId, Guid cardId, Guid userId, Stream pdf,
        long length, string fileName, string? competence, string? password, StatementCategoryContext categories, CancellationToken ct) =>
        PreviewCoreAsync(workspaceId, cardId, userId, pdf, length, fileName, competence, password, ct, categories, true, false);
    public Task<CreditCardStatementAgentPreviewResult> PreviewForProcessingAsync(Guid workspaceId, Guid cardId, Guid userId, Stream pdf,
        long length, string fileName, string? competence, string? password, StatementCategoryContext categories, CancellationToken ct) =>
        PreviewCoreAsync(workspaceId, cardId, userId, pdf, length, fileName, competence, password, ct, categories, true);
    public Task<CreditCardStatementAgentPreviewResult> PreviewWithCategoriesAsync(Guid workspaceId, Guid cardId, Guid userId, Stream pdf,
        long length, string fileName, string? competence, string? password, StatementCategoryContext categories, CancellationToken ct) =>
        PreviewCoreAsync(workspaceId, cardId, userId, pdf, length, fileName, competence, password, ct, categories);

    public Task<CreditCardStatementAgentPreviewResult> PreviewAsync(Guid workspaceId, Guid cardId, Guid userId, Stream pdf,
        long length, string fileName, string? competence, string? password, CancellationToken ct) =>
        PreviewCoreAsync(workspaceId, cardId, userId, pdf, length, fileName, competence, password, ct, null);

    private async Task<CreditCardStatementAgentPreviewResult> PreviewCoreAsync(Guid workspaceId, Guid cardId, Guid userId, Stream pdf,
        long length, string fileName, string? competence, string? password, CancellationToken cancellationToken, StatementCategoryContext? categories, bool automatic = false, bool allowCorrectionRetry = true)
    {
        var card = await context.GetAsync(workspaceId, cardId, userId, cancellationToken);
        if (card is null) return new(CreditCardPdfPreviewAccess.NotFound, null, null);
        if (length <= 0) return Invalid("file", "O arquivo PDF está vazio.");
        if (length > CreditCardPdfPreviewService.MaxFileBytes) return Invalid("file", "O arquivo excede o limite de 10 MB.");
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return Invalid("file", "O arquivo deve ter extensão .pdf.");
        if (password?.Length > 128) return Invalid("password", "A senha informada excede o limite permitido.");
        using var content = new MemoryStream((int)length);
        await pdf.CopyToAsync(content, cancellationToken);
        if (options?.DocumentaryImport.Enabled == true && categories is not null && allowCorrectionRetry)
        {
            try
            {
                content.Position = 0;
                if (documentary is null) return Invalid("agent", "Perfil documental indisponível.") with { FailureCode = "DocumentaryPipelineUnavailable" };
                var documentaryResult = await documentary.ExtractAsync(content, new(workspaceId, userId, cardId, Card: card), password,
                    fileName, competence, categories, cancellationToken);
                return new(CreditCardPdfPreviewAccess.Allowed, documentaryResult, null);
            }
            catch (CreditCardPdfReadException e) when (e.Failure is CreditCardPdfReadFailure.PasswordRequired or CreditCardPdfReadFailure.PasswordRejected)
            { return Invalid("password", CreditCardPdfPreviewService.PasswordMessage(e.PasswordResolutionCode)) with { FailureCode = "PasswordRequired", PasswordResolutionCode = e.PasswordResolutionCode }; }
            catch (CreditCardStatementAgentUnavailableException e)
            { return Invalid("agent", "Extração documental indisponível.") with { FailureCode = e.FailureCode ?? "AgentUnavailable" }; }
            catch (Exception e) when (e is not OperationCanceledException)
            { return Invalid("file", "Preparação documental indisponível.") with { FailureCode = "StatementDocumentPreparationFailed" }; }
        }
        CreditCardPdfExtractedDocument extracted;
        try { extracted = (await new PdfPasswordReader(pdfExtractor, passwords).ReadAsync(content, new(workspaceId, userId, cardId, Card: card), password, cancellationToken, allowAutomatic: allowCorrectionRetry)).Document; }
        catch (CreditCardPdfReadException exception) when (exception.Failure == CreditCardPdfReadFailure.PasswordRequired)
        {
            return Invalid("password", CreditCardPdfPreviewService.PasswordMessage(exception.PasswordResolutionCode)) with
            { FailureCode = "PasswordRequired", PasswordResolutionCode = exception.PasswordResolutionCode };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return Invalid("file", "O PDF está corrompido ou não pôde ser interpretado."); }
        if (extracted.Pages.Count == 0 || extracted.Pages.All(page => string.IsNullOrWhiteSpace(page.Text) && page.ImagePng is null))
            return Invalid("file", "Não foi possível recuperar texto ou imagens do PDF.");

        CreditCardStatementExtractionResult result;
        if (categories is not null)
        {
            var text = StatementCategorization.Normalize(string.Join("\n", extracted.Pages.Select(p => p.Text)));
            categories = categories with { History = categories.History.Where(h => text.Contains(StatementCategorization.Normalize(h.Description), StringComparison.Ordinal)).Take(200).ToArray() };
        }
        try { result = await orchestrator.ExtractAsync(new(fileName, extracted, competence) { Categories = categories, ReconcileWithRetry = categories is not null, RequireAgent = automatic, AllowCorrectionRetry = allowCorrectionRetry }, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return Invalid("agent", "O agente não conseguiu interpretar a fatura com segurança."); }
        if (result.Primary is null) return new(CreditCardPdfPreviewAccess.Allowed, result, null);
        var warnings = (result.DocumentWarnings ?? []).ToList();
        var mismatch = CreditCardStatementResolver.ContradictsSelectedContext(result.Primary.Header, card.Issuer, card.LastFourDigits);
        // Parser contradictions require context review, not loss of a valid financial preview.
        if (categories is not null && result.PrimarySource != "Agent")
        {
            try
            {
                var local = pdfExtractor.Parse(extracted, fileName, competence, null);
                mismatch |= local.Statement.CardLastDigits is { } localDigits && card.LastFourDigits is { } selectedDigits && localDigits != selectedDigits;
            }
            catch (Exception error) when (error is not OperationCanceledException) { warnings.Add("ParserContextUnavailable"); }
        }
        if (mismatch) warnings.Add("CardMismatch");
        result = result with { DocumentWarnings = warnings.Count == 0 ? null : warnings.Distinct().ToArray() };
        return new(CreditCardPdfPreviewAccess.Allowed, result, null) { FailureCode = mismatch ? "CardMismatch" : null };
    }

    private static CreditCardStatementAgentPreviewResult Invalid(string field, string message) =>
        new(CreditCardPdfPreviewAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

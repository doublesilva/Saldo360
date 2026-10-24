namespace Saldo360.Application.CreditCards;

public sealed class CreditCardStatementAgentPreviewService(
    ICreditCardPdfContextRepository context,
    ICreditCardPdfTextExtractor pdfExtractor,
    ICreditCardPdfPasswordProvider passwords,
    ICreditCardStatementExtractionOrchestrator orchestrator) : ICreditCardStatementAgentPreviewService
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
        CreditCardPdfExtractedDocument extracted;
        try { extracted = await pdfExtractor.ExtractAsync(content, CreditCardPdfPreviewService.MaxPages, [], cancellationToken); }
        catch (CreditCardPdfReadException exception) when (exception.Failure == CreditCardPdfReadFailure.PasswordRequired)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(password)) candidates.Add(password);
            if (card.HolderUserId is { } holder)
            {
                var resolved = await passwords.ResolveAsync(holder, card.Issuer, cancellationToken);
                candidates.AddRange(resolved.Candidates.Take(2).Where(candidate => !candidates.Contains(candidate, StringComparer.Ordinal)));
            }
            if (candidates.Count == 0) return Invalid("password", "Informe a senha para continuar o processamento.") with { FailureCode = "PasswordRequired" };
            try { extracted = await pdfExtractor.ExtractAsync(content, CreditCardPdfPreviewService.MaxPages, candidates, cancellationToken); }
            catch (CreditCardPdfReadException rejected) when (rejected.Failure == CreditCardPdfReadFailure.PasswordRejected)
            { return Invalid("password", "Não foi possível desbloquear o PDF com as credenciais disponíveis.") with { FailureCode = "PasswordRejected" }; }
            catch (Exception failed) when (failed is not OperationCanceledException)
            { return Invalid("file", "O PDF está corrompido ou não pôde ser interpretado."); }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return Invalid("file", "O PDF está corrompido ou não pôde ser interpretado."); }
        if (extracted.Pages.Count == 0 || extracted.Pages.All(page => string.IsNullOrWhiteSpace(page.Text)))
            return Invalid("file", "Não foi possível extrair texto do PDF; OCR não é suportado.");

        if (categories is not null && !automatic)
        {
            var local = pdfExtractor.Parse(extracted, fileName, competence, null);
            if (local.Statement.CardLastDigits is { } localDigits && card.LastFourDigits is { } selectedDigits && localDigits != selectedDigits)
                return Invalid("cardId", "O PDF corresponde a outro cartão. A extração por agente não foi iniciada.");
        }

        CreditCardStatementExtractionResult result;
        if (categories is not null)
        {
            var text = StatementCategorization.Normalize(string.Join("\n", extracted.Pages.Select(p => p.Text)));
            categories = categories with { History = categories.History.Where(h => text.Contains(StatementCategorization.Normalize(h.Description), StringComparison.Ordinal)).Take(200).ToArray() };
        }
        try { result = await orchestrator.ExtractAsync(new(fileName, extracted, competence) { Categories = categories, ReconcileWithRetry = categories is not null, RequireAgentIdentity = automatic, AllowCorrectionRetry = allowCorrectionRetry }, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return Invalid("agent", "O agente não conseguiu interpretar a fatura com segurança."); }
        if (result.Primary is null) return new(CreditCardPdfPreviewAccess.Allowed, result, null);
        if (automatic)
        {
            // A deterministic contradiction still blocks finance, but never prevents agent interpretation.
            var local = pdfExtractor.Parse(extracted, fileName, competence, null);
            if (local.Statement.CardLastDigits is { } localDigits && result.Primary.Header.CardLastDigits is { } agentDigits && localDigits != agentDigits)
                return !allowCorrectionRetry
                    ? new(CreditCardPdfPreviewAccess.Allowed, result, null) { FailureCode = "CardMismatch" }
                    : Invalid("cardId", "Identificações documentais divergentes; revise o documento.") with { FailureCode = "CardMismatch" };
            return new(CreditCardPdfPreviewAccess.Allowed, result, null);
        }
        if (result.Primary.Header.CardLastDigits is { } digits && card.LastFourDigits is { } selected && digits != selected)
            return Invalid("cardId", $"A fatura pertence ao cartão •••• {digits}, diferente do cartão selecionado •••• {selected}.");
        return new(CreditCardPdfPreviewAccess.Allowed, result, null);
    }

    private static CreditCardStatementAgentPreviewResult Invalid(string field, string message) =>
        new(CreditCardPdfPreviewAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

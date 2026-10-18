namespace Saldo360.Application.CreditCards;

public sealed class CreditCardStatementAgentShadowService(
    ICreditCardPdfContextRepository context,
    ICreditCardPdfTextExtractor extractor,
    ICreditCardPdfPasswordProvider passwords,
    IParserBackedCreditCardStatementExtractorAgent parserAgent,
    LlmCreditCardStatementExtractorAgent llmAgent) : ICreditCardStatementAgentShadowService
{
    public async Task<CreditCardStatementAgentShadowResult> PreviewAsync(Guid familyId, Guid cardId, Guid userId, Stream pdf,
        long length, string fileName, string? competence, string? password, CancellationToken cancellationToken)
    {
        var card = await context.GetAsync(familyId, cardId, userId, cancellationToken);
        if (card is null) return new(CreditCardPdfPreviewAccess.NotFound, null, null);
        if (length <= 0 || length > CreditCardPdfPreviewService.MaxFileBytes || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Invalid("file", "Informe um arquivo PDF válido de até 10 MB.");
        if (password?.Length > 128) return Invalid("password", "A senha informada excede o limite permitido.");
        using var content = new MemoryStream((int)length); await pdf.CopyToAsync(content, cancellationToken);
        CreditCardPdfExtractedDocument document;
        try { document = await extractor.ExtractAsync(content, CreditCardPdfPreviewService.MaxPages, [], cancellationToken); }
        catch (CreditCardPdfReadException exception) when (exception.Failure == CreditCardPdfReadFailure.PasswordRequired)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(password)) candidates.Add(password);
            if (card.HolderUserId is { } holder)
            {
                var resolution = await passwords.ResolveAsync(holder, card.Issuer, cancellationToken);
                candidates.AddRange(resolution.Candidates.Take(2).Where(value => !candidates.Contains(value, StringComparer.Ordinal)));
            }
            if (candidates.Count == 0) return Invalid("file", "O PDF exige senha, mas o material de identidade não está disponível.");
            try { document = await extractor.ExtractAsync(content, CreditCardPdfPreviewService.MaxPages, candidates, cancellationToken); }
            catch (Exception failed) when (failed is not OperationCanceledException) { return Invalid("file", "Não foi possível desbloquear ou interpretar o PDF."); }
        }
        catch (Exception failed) when (failed is not OperationCanceledException) { return Invalid("file", "O PDF está corrompido ou não pôde ser interpretado."); }
        if (document.Pages.Count == 0 || document.Pages.All(page => string.IsNullOrWhiteSpace(page.Text)))
            return Invalid("file", "Não foi possível extrair texto suficiente do PDF.");
        var request = new CreditCardStatementAgentRequest(fileName, document, competence);
        string parserJson;
        string agentJson;
        try { parserJson = await parserAgent.ExtractAsync(request, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException) { return Invalid("parser", "O parser de referência falhou na comparação experimental."); }
        try { agentJson = await llmAgent.ExtractAsync(request, cancellationToken); }
        catch (CreditCardStatementAgentUnavailableException) { return Invalid("agent", "O provider experimental está desabilitado ou indisponível."); }
        catch (Exception error) when (error is not OperationCanceledException) { return Invalid("agent", "O provider experimental falhou de forma segura."); }
        if (!CreditCardStatementAgentEvaluator.TryEvaluate(parserJson, fileName, document.Pages.Count, out var parser, out var parserError)) return Invalid("parser", parserError);
        if (!CreditCardStatementAgentEvaluator.TryEvaluate(agentJson, fileName, document.Pages.Count, out var agent, out var agentError)) return Invalid("agent", agentError);
        if (agent!.Header.CardLastDigits is { } digits && card.LastFourDigits is { } selected && digits != selected)
            return Invalid("cardId", "O resultado do agente identifica outro final de cartão.");
        return new(CreditCardPdfPreviewAccess.Allowed, new(parser!, agent, CreditCardStatementAgentEvaluator.Compare(parser!, agent)), null);
    }

    private static CreditCardStatementAgentShadowResult Invalid(string field, string message) =>
        new(CreditCardPdfPreviewAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

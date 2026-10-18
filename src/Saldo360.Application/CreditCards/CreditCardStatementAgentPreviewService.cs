using System.Globalization;
using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards;

public sealed class CreditCardStatementAgentPreviewService(
    ICreditCardPdfContextRepository context,
    ICreditCardPdfTextExtractor pdfExtractor,
    ICreditCardPdfPasswordProvider passwords,
    ICreditCardStatementExtractorAgent agent) : ICreditCardStatementAgentPreviewService
{
    public async Task<CreditCardStatementAgentPreviewResult> PreviewAsync(Guid familyId, Guid cardId, Guid userId, Stream pdf,
        long length, string fileName, string? competence, string? password, CancellationToken cancellationToken)
    {
        var card = await context.GetAsync(familyId, cardId, userId, cancellationToken);
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
            if (candidates.Count == 0) return Invalid("file", "O PDF exige senha, mas o material de identidade do titular não está disponível.");
            try { extracted = await pdfExtractor.ExtractAsync(content, CreditCardPdfPreviewService.MaxPages, candidates, cancellationToken); }
            catch (CreditCardPdfReadException rejected) when (rejected.Failure == CreditCardPdfReadFailure.PasswordRejected)
            { return Invalid("file", "Não foi possível desbloquear o PDF com as credenciais disponíveis."); }
            catch (Exception failed) when (failed is not OperationCanceledException)
            { return Invalid("file", "O PDF está corrompido ou não pôde ser interpretado."); }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return Invalid("file", "O PDF está corrompido ou não pôde ser interpretado."); }
        if (extracted.Pages.Count == 0 || extracted.Pages.All(page => string.IsNullOrWhiteSpace(page.Text)))
            return Invalid("file", "Não foi possível extrair texto do PDF; OCR não é suportado.");

        string response;
        try { response = await agent.ExtractAsync(new(fileName, extracted, competence), cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return Invalid("agent", "O agente não conseguiu interpretar a fatura com segurança."); }
        if (!CreditCardStatementAgentSchema.TryValidate(response, extracted.Pages.Count, out var document, out var schemaError))
            return Invalid("agent", schemaError);
        if (document!.Header.CardLastDigits is { } digits && card.LastFourDigits is { } selected && digits != selected)
            return Invalid("cardId", $"A fatura pertence ao cartão •••• {digits}, diferente do cartão selecionado •••• {selected}.");

        var components = document.Items.Where(item => item.IncludedInCurrentTotal)
            .Sum(item => decimal.Parse(item.Amount, CultureInfo.InvariantCulture) * (item.Nature == "Credit" ? -1m : 1m));
        decimal? official = document.Header.OfficialTotal is null ? null : decimal.Parse(document.Header.OfficialTotal, CultureInfo.InvariantCulture);
        decimal? difference = official is null ? null : official.Value - components;
        var hasUnknown = document.Items.Any(item => item.IncludedInCurrentTotal && item.Type == "Unknown");
        var status = official is null || hasUnknown ? "NeedsReview" : difference == 0m ? "Reconciled" : "Divergent";
        var reconciliation = new CreditCardPdfReconciliation(status, document.Header.OfficialTotal, Money.Format(components),
            difference is null ? null : Money.Format(difference.Value), hasUnknown);
        return new(CreditCardPdfPreviewAccess.Allowed,
            new(fileName, extracted.Pages.Count, document.Header, document.Items, reconciliation), null);
    }

    private static CreditCardStatementAgentPreviewResult Invalid(string field, string message) =>
        new(CreditCardPdfPreviewAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

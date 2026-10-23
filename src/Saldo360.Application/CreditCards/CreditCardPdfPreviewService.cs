namespace Saldo360.Application.CreditCards;

public sealed class CreditCardPdfPreviewService(
    ICreditCardPdfContextRepository context,
    ICreditCardPdfTextExtractor extractor,
    ICreditCardPdfPasswordProvider passwords,
    CreditCardStatementResolver? resolver = null) : ICreditCardPdfPreviewService
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxPages = 50;
    public async Task<CreditCardPdfPreviewResult> PreviewAsync(Guid workspaceId, Guid cardId, Guid userId, Stream pdf, long length, string fileName, string? issuerProfile, string? competence, string? dateFormat, string? password, CancellationToken cancellationToken)
    {
        var card = await context.GetAsync(workspaceId, cardId, userId, cancellationToken);
        if (card is null) return new(CreditCardPdfPreviewAccess.NotFound, null, null);
        if (length <= 0) return Invalid("file", "O arquivo PDF está vazio.");
        if (length > MaxFileBytes) return Invalid("file", "O arquivo excede o limite de 10 MB.");
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return Invalid("file", "O arquivo deve ter extensão .pdf.");
        if (password?.Length > 128) return Invalid("password", "A senha informada excede o limite permitido.");
        using var content = new MemoryStream((int)length);
        await pdf.CopyToAsync(content, cancellationToken);
        try
        {
            var extracted = await extractor.ExtractAsync(content, MaxPages, [], cancellationToken);
            if (extracted.Pages.Count == 0 || extracted.Pages.All(page => string.IsNullOrWhiteSpace(page.Text))) return Invalid("file", "Não foi possível extrair texto do PDF; OCR não é suportado.", "TextUnavailable");
            var preview = extractor.Parse(extracted, fileName, competence, dateFormat);
            return await ValidateCardAsync(workspaceId, cardId, card, preview, cancellationToken);
        }
        catch (CreditCardPdfReadException exception) when (exception.Failure == CreditCardPdfReadFailure.PasswordRequired)
        {
            return await PreviewProtectedAsync(workspaceId, cardId, card, content, fileName, issuerProfile, competence, dateFormat, password, cancellationToken);
        }
        catch (CreditCardPdfReadException exception) when (exception.Failure == CreditCardPdfReadFailure.UnsupportedEncryption)
        {
            return Invalid("file", "A criptografia deste PDF não é suportada.", "UnsupportedEncryption");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid("file", "O PDF está corrompido ou não pôde ser interpretado.");
        }
    }

    private async Task<CreditCardPdfPreviewResult> PreviewProtectedAsync(Guid workspaceId, Guid cardId, CreditCardPdfContext card, Stream pdf, string fileName, string? issuerProfile, string? competence, string? dateFormat, string? manualPassword, CancellationToken cancellationToken)
    {
        var candidates = new List<string>(3);
        if (!string.IsNullOrEmpty(manualPassword)) candidates.Add(manualPassword);

        var resolution = card.HolderUserId is { } holder
            ? await passwords.ResolveAsync(holder, card.Issuer, cancellationToken)
            : CreditCardPdfPasswordResolution.Unavailable;
        candidates.AddRange(resolution.Candidates.Take(2).Where(candidate => !candidates.Contains(candidate, StringComparer.Ordinal)));

        if (candidates.Count == 0)
            return Invalid("file", "O PDF exige senha, mas o material de identidade do titular não está disponível.", "PasswordRequired");

        try
        {
            var extracted = await extractor.ExtractAsync(pdf, MaxPages, candidates, cancellationToken);
            if (extracted.Pages.Count == 0 || extracted.Pages.All(page => string.IsNullOrWhiteSpace(page.Text))) return Invalid("file", "Não foi possível extrair texto do PDF; OCR não é suportado.", "TextUnavailable");
            var preview = extractor.Parse(extracted, fileName, competence, dateFormat) with { Security = new("Unlocked") };
            return await ValidateCardAsync(workspaceId, cardId, card, preview, cancellationToken);
        }
        catch (CreditCardPdfReadException exception) when (exception.Failure == CreditCardPdfReadFailure.PasswordRejected)
        {
            return Invalid("file", "Não foi possível desbloquear o PDF com as credenciais disponíveis.", "PasswordRequired");
        }
        catch (CreditCardPdfReadException exception) when (exception.Failure == CreditCardPdfReadFailure.UnsupportedEncryption)
        {
            return Invalid("file", "A criptografia deste PDF não é suportada.", "UnsupportedEncryption");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid("file", "O PDF está corrompido ou não pôde ser interpretado.");
        }
    }

    private static CreditCardPdfPreviewResult Invalid(string field, string message, string? failureCode = null) => new(CreditCardPdfPreviewAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] }, failureCode);

    private async Task<CreditCardPdfPreviewResult> ValidateCardAsync(Guid workspaceId, Guid selectedCardId, CreditCardPdfContext card, CreditCardPdfPreview preview, CancellationToken cancellationToken)
    {
        var documentDigits = preview.Statement.CardLastDigits;
        if (documentDigits is not null && card.LastFourDigits is not null && documentDigits != card.LastFourDigits)
            return Invalid("cardId", $"A fatura pertence ao cartão •••• {documentDigits}, diferente do cartão selecionado •••• {card.LastFourDigits}.");
        var resolution = resolver is null
            ? new CreditCardStatementResolution(CreditCardStatementResolutionStatus.InsufficientEvidence)
            : await resolver.ResolveAsync(workspaceId, preview.Statement.Issuer, documentDigits, card.HolderUserId, cancellationToken);
        if (resolution.Status == CreditCardStatementResolutionStatus.Matched && resolution.CardId != selectedCardId)
            return Invalid("cardId", "A fatura corresponde inequivocamente a outro cartão da família.");
        return new(CreditCardPdfPreviewAccess.Allowed, preview with { CardResolution = new(resolution.Status.ToString(), resolution.CardId) }, null);
    }
}

public sealed record CreditCardPdfExtractedPage(
    int Number,
    string Text,
    IReadOnlyList<CreditCardPdfExtractedWord>? Words = null);
public sealed record CreditCardPdfExtractedWord(string Text, double X, double Top, double Right, double Bottom);
public sealed record CreditCardPdfExtractedDocument(IReadOnlyList<CreditCardPdfExtractedPage> Pages);

public interface ICreditCardPdfTextExtractor
{
    Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken);
    CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat);
}

public enum CreditCardPdfReadFailure { PasswordRequired, PasswordRejected, UnsupportedEncryption, Corrupt }

public sealed class CreditCardPdfReadException(CreditCardPdfReadFailure failure, Exception? innerException = null)
    : Exception("Não foi possível ler o PDF.", innerException)
{
    public CreditCardPdfReadFailure Failure { get; } = failure;
}

public interface ICreditCardPdfPasswordProvider
{
    Task<CreditCardPdfPasswordResolution> ResolveAsync(Guid holderUserId, string issuer, CancellationToken cancellationToken);
}

public interface ICreditCardPdfContextRepository
{
    Task<CreditCardPdfContext?> GetAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken);
}

public sealed record CreditCardPdfContext(string Issuer, Guid? HolderUserId, string? LastFourDigits = null);

public sealed record CreditCardPdfPasswordResolution(IReadOnlyList<string> Candidates)
{
    public static CreditCardPdfPasswordResolution Unavailable { get; } = new([]);
}

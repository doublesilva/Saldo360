namespace Saldo360.Application.CreditCards;

public sealed class CreditCardPdfPreviewService(
    ICreditCardPdfContextRepository context,
    ICreditCardPdfTextExtractor extractor,
    ICreditCardPdfPasswordProvider passwords,
    CreditCardStatementResolver? resolver = null) : ICreditCardPdfPreviewService
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxPages = 50;
    public async Task<CreditCardPdfPreviewResult?> ValidateExtractedAsync(Guid workspaceId, Guid cardId, Guid userId, CreditCardPdfExtractedDocument document, string fileName, bool unlocked, CancellationToken ct)
    {
        var card = await context.GetAsync(workspaceId, cardId, userId, ct);
        if (card is null) return new(CreditCardPdfPreviewAccess.NotFound, null, null);
        var preview = extractor.Parse(document, fileName, null, null) with { Security = new(unlocked ? "Unlocked" : "Unprotected") };
        return await ValidateCardAsync(workspaceId, cardId, card, preview, ct);
    }
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
            var read = await new PdfPasswordReader(extractor, passwords).ReadAsync(content, new(workspaceId, userId, cardId, Card: card), password, cancellationToken);
            var extracted = read.Document;
            if (extracted.Pages.Count == 0 || extracted.Pages.All(page => string.IsNullOrWhiteSpace(page.Text))) return Invalid("file", "Não foi possível extrair texto do PDF; OCR não é suportado.", "TextUnavailable");
            var preview = extractor.Parse(extracted, fileName, competence, dateFormat) with { Security = new(read.Unlocked ? "Unlocked" : "Unprotected") };
            return await ValidateCardAsync(workspaceId, cardId, card, preview, cancellationToken);
        }
        catch (CreditCardPdfReadException exception) when (exception.Failure == CreditCardPdfReadFailure.PasswordRequired)
        {
            return Invalid("file", PasswordMessage(exception.PasswordResolutionCode), "PasswordRequired") with { PasswordResolutionCode = exception.PasswordResolutionCode };
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

    internal static string PasswordMessage(string? code) => code switch
    {
        "CpfNotRegistered" => "CPF não cadastrado no perfil. Informe a senha do PDF manualmente.",
        "CpfInvalid" => "CPF do perfil inválido. Informe a senha do PDF manualmente.",
        "CpfSessionInvalid" => "Sessão de perfil inválida ou expirada. Informe a senha manualmente ou autentique novamente.",
        "CpfAccessDenied" => "Acesso ao perfil não autorizado. Informe a senha do PDF manualmente.",
        "CpfUnavailable" => "Perfil temporariamente indisponível. Informe a senha do PDF manualmente.",
        "IdentityMaterialUnavailable" => "O PDF exige senha, mas o material de identidade do titular não está disponível. Informe a senha manualmente.",
        _ => "Não foi possível desbloquear o PDF com as credenciais disponíveis. Informe a senha manualmente."
    };

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
    IReadOnlyList<CreditCardPdfExtractedWord>? Words = null)
{
    public string TextSource { get; init; } = "Native";
    public string ProcessingStatus { get; init; } = "Native";
    public bool HasUntranscribedImageRegion { get; init; }
    public double PageWidth { get; init; }
    public double PageHeight { get; init; }
    public IReadOnlyList<StatementVisualRegion> VisualRegions { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<CreditCardPdfExtractedWord>? NativeWords { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PreparationFailureCode { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public byte[]? ImagePng { get; init; }
}
public sealed record CreditCardPdfExtractedWord(string Text, double X, double Top, double Right, double Bottom);
public sealed record StatementVisualRegion(int Page, int Index, double Left, double Top, double Right, double Bottom, string Status = "Pending")
{
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<CreditCardPdfExtractedWord> Words { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public string Text { get; init; } = string.Empty;
}
public sealed record CreditCardPdfExtractedDocument(IReadOnlyList<CreditCardPdfExtractedPage> Pages);

public interface ICreditCardPdfTextExtractor
{
    Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken);
    CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat);
}

public enum CreditCardPdfReadFailure { PasswordRequired, PasswordRejected, UnsupportedEncryption, Corrupt }

public sealed class CreditCardPdfReadException(CreditCardPdfReadFailure failure, Exception? innerException = null, string? passwordResolutionCode = null)
    : Exception("Não foi possível ler o PDF.", innerException)
{
    public CreditCardPdfReadFailure Failure { get; } = failure;
    public string? PasswordResolutionCode { get; } = passwordResolutionCode;
}

public interface ICreditCardPdfPasswordProvider
{
    Task<CreditCardPdfPasswordResolution> ResolveAsync(Guid holderUserId, string issuer, CancellationToken cancellationToken);
    Task<CreditCardPdfPasswordResolution> ResolveForDocumentAsync(PdfPasswordContext context, string? manualPassword, CancellationToken ct)
        => !string.IsNullOrEmpty(manualPassword) ? Task.FromResult(new CreditCardPdfPasswordResolution([manualPassword]))
            : context.Card?.HolderUserId is { } holder ? ResolveAsync(holder, context.Card.Issuer, ct)
            : Task.FromResult(CreditCardPdfPasswordResolution.Unavailable);
}

public interface ICreditCardPdfContextRepository
{
    Task<CreditCardPdfContext?> GetAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken);
}

public sealed record CreditCardPdfContext(string Issuer, Guid? HolderUserId, string? LastFourDigits = null);

public sealed class CreditCardPdfPasswordResolution(IReadOnlyList<string> candidates, string? failureCode = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<string> Candidates { get; } = candidates;
    public string? FailureCode { get; } = failureCode;
    public override string ToString() => "PdfPasswordResolution";
    public static CreditCardPdfPasswordResolution Unavailable { get; } = new([]);
}

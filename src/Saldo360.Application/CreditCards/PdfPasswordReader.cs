namespace Saldo360.Application.CreditCards;

public sealed record PdfPasswordContext(Guid WorkspaceId, Guid UserId, Guid? CardId = null, Guid? DocumentId = null, CreditCardPdfContext? Card = null);
public sealed record PdfPasswordReadResult(CreditCardPdfExtractedDocument Document, bool Unlocked);

/// <summary>Only ephemeral candidates; no logging, persistence or financial processing.</summary>
public sealed class PdfPasswordReader(ICreditCardPdfTextExtractor extractor, ICreditCardPdfPasswordProvider provider)
{
    public async Task<PdfPasswordReadResult> ReadAsync(Stream pdf, PdfPasswordContext context, string? manualPassword, CancellationToken ct, bool allowAutomatic = true)
    {
        try { return new(await extractor.ExtractAsync(pdf, CreditCardPdfPreviewService.MaxPages, [], ct), false); }
        catch (CreditCardPdfReadException error) when (error.Failure == CreditCardPdfReadFailure.PasswordRequired) { }
        var resolution = allowAutomatic
            ? await provider.ResolveForDocumentAsync(context, manualPassword, ct)
            : string.IsNullOrEmpty(manualPassword) ? CreditCardPdfPasswordResolution.Unavailable : new CreditCardPdfPasswordResolution([manualPassword]);
        foreach (var candidate in resolution.Candidates.Distinct(StringComparer.Ordinal).Take(string.IsNullOrEmpty(manualPassword) ? 2 : 1))
        {
            if (pdf.CanSeek) pdf.Position = 0;
            try { return new(await extractor.ExtractAsync(pdf, CreditCardPdfPreviewService.MaxPages, [candidate], ct), true); }
            catch (CreditCardPdfReadException error) when (error.Failure is CreditCardPdfReadFailure.PasswordRejected or CreditCardPdfReadFailure.PasswordRequired) { }
        }
        throw new CreditCardPdfReadException(CreditCardPdfReadFailure.PasswordRequired,
            passwordResolutionCode: resolution.FailureCode ?? (resolution.Candidates.Count == 0 ? "IdentityMaterialUnavailable" : "PasswordNotRecognized"));
    }
}

public sealed class ManualOnlyPdfPasswordProvider : ICreditCardPdfPasswordProvider
{
    public Task<CreditCardPdfPasswordResolution> ResolveAsync(Guid holderUserId, string issuer, CancellationToken ct) => Task.FromResult(CreditCardPdfPasswordResolution.Unavailable);
}

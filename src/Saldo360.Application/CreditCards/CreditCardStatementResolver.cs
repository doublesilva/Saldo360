using System.Globalization;
using System.Text;

namespace Saldo360.Application.CreditCards;

public enum CreditCardStatementResolutionStatus { Matched, NotFound, Ambiguous, InsufficientEvidence }
public sealed record CreditCardStatementResolution(CreditCardStatementResolutionStatus Status, Guid? CardId = null);
public sealed record CreditCardStatementCandidate(Guid CardId, string Issuer, string LastFourDigits, Guid? HolderUserId, string? HolderName = null);

public interface ICreditCardStatementResolutionRepository
{
    Task<IReadOnlyList<CreditCardStatementCandidate>> ListActiveAsync(Guid workspaceId, CancellationToken cancellationToken);
}

public sealed class CreditCardStatementResolver(ICreditCardStatementResolutionRepository repository)
{
    public async Task<CreditCardStatementResolution> ResolveDocumentAsync(Guid workspaceId, CreditCardStatementAgentHeader header, CancellationToken ct)
    {
        var resolved = await ResolveAsync(workspaceId, header.Issuer, header.CardLastDigits, null, ct);
        if (resolved.Status != CreditCardStatementResolutionStatus.Matched || string.IsNullOrWhiteSpace(header.HolderName)) return resolved;
        var candidate = (await repository.ListActiveAsync(workspaceId, ct)).Single(c => c.CardId == resolved.CardId);
        // Display names corroborate, never authorize or replace a missing issuer/last-four identity.
        return candidate.HolderName is { Length: > 0 } name && NormalizeIssuer(name) != NormalizeIssuer(header.HolderName)
            ? new(CreditCardStatementResolutionStatus.InsufficientEvidence) : resolved;
    }

    public async Task<CreditCardStatementResolution> ResolveAsync(Guid workspaceId, string? issuer, string? lastFourDigits, Guid? holderUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lastFourDigits) || lastFourDigits.Length != 4 || lastFourDigits.Any(character => character is < '0' or > '9') || string.IsNullOrWhiteSpace(issuer))
            return new(CreditCardStatementResolutionStatus.InsufficientEvidence);
        var issuerKey = NormalizeIssuer(issuer);
        var candidates = (await repository.ListActiveAsync(workspaceId, cancellationToken))
            .Where(card => card.LastFourDigits == lastFourDigits && NormalizeIssuer(card.Issuer) == issuerKey)
            .Where(card => holderUserId is null || card.HolderUserId == holderUserId)
            .ToList();
        return candidates.Count switch
        {
            0 => new(CreditCardStatementResolutionStatus.NotFound),
            1 => new(CreditCardStatementResolutionStatus.Matched, candidates[0].CardId),
            _ => new(CreditCardStatementResolutionStatus.Ambiguous),
        };
    }

    internal static string NormalizeIssuer(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(character)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

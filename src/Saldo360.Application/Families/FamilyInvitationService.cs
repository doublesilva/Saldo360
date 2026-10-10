using System.Buffers.Text;
using Saldo360.Domain.Families;

namespace Saldo360.Application.Families;

/// <summary>
/// Convites para o núcleo familiar. Só o Owner cria ou revoga; quem aceita entra sempre como Member.
/// O usuário vem da sessão; papel e identidade do novo membro nunca vêm do cliente.
/// </summary>
public sealed class FamilyInvitationService(
    IFamilyRepository families,
    IFamilyInvitationRepository invitations,
    TimeProvider timeProvider)
{
    public async Task<CreateInvitationResult> CreateAsync(Guid familyId, Guid userId, CancellationToken cancellationToken)
    {
        switch (await families.GetMemberRoleAsync(familyId, userId, cancellationToken))
        {
            case null:
                return new CreateInvitationResult(InvitationAccess.NotFound, null);
            case not FamilyRole.Owner:
                return new CreateInvitationResult(InvitationAccess.Forbidden, null);
        }

        var (invitation, token) = FamilyInvitation.Create(familyId, userId, timeProvider.GetUtcNow());
        await invitations.AddAsync(invitation, cancellationToken);

        return new CreateInvitationResult(
            InvitationAccess.Allowed,
            new CreatedInvitation(invitation.Id, Base64Url.EncodeToString(token), invitation.ExpiresAt));
    }

    public async Task<RevokeInvitationResult> RevokeAsync(
        Guid familyId, Guid invitationId, Guid userId, CancellationToken cancellationToken)
    {
        switch (await families.GetMemberRoleAsync(familyId, userId, cancellationToken))
        {
            case null:
                return new RevokeInvitationResult(InvitationAccess.NotFound, RevokeInvitationOutcome.NotFound);
            case not FamilyRole.Owner:
                return new RevokeInvitationResult(InvitationAccess.Forbidden, RevokeInvitationOutcome.NotFound);
        }

        var outcome = await invitations.RevokeAsync(familyId, invitationId, timeProvider.GetUtcNow(), cancellationToken);
        return new RevokeInvitationResult(InvitationAccess.Allowed, outcome);
    }

    public async Task<AcceptInvitationOutcome> AcceptAsync(string? token, Guid userId, CancellationToken cancellationToken)
    {
        if (!TryDecodeToken(token, out var tokenBytes))
        {
            return new AcceptInvitationOutcome(AcceptInvitationStatus.Unavailable);
        }

        return await invitations.AcceptAsync(
            FamilyInvitation.HashToken(tokenBytes), userId, timeProvider.GetUtcNow(), cancellationToken);
    }

    private static bool TryDecodeToken(string? token, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(token) || token.Length > 64)
        {
            return false;
        }

        try
        {
            bytes = Base64Url.DecodeFromChars(token);
        }
        catch (FormatException)
        {
            return false;
        }

        return bytes.Length == FamilyInvitation.TokenSizeInBytes;
    }
}

public enum InvitationAccess
{
    Allowed,

    /// <summary>Família inexistente ou usuário não participante (404).</summary>
    NotFound,

    /// <summary>Participante sem papel de Owner (403).</summary>
    Forbidden,
}

/// <param name="Token">Token em texto puro (base64url). Devolvido só aqui; não é persistido.</param>
public sealed record CreatedInvitation(Guid Id, string Token, DateTimeOffset ExpiresAt);

public sealed record CreateInvitationResult(InvitationAccess Access, CreatedInvitation? Invitation);

public sealed record RevokeInvitationResult(InvitationAccess Access, RevokeInvitationOutcome Outcome);

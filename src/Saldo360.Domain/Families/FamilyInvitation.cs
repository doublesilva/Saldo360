using System.Security.Cryptography;
using Saldo360.Domain.Common;

namespace Saldo360.Domain.Families;

/// <summary>
/// Convite de uso único para entrar num núcleo familiar como <see cref="FamilyRole.Member"/>.
/// Quem tiver o link pode aceitá-lo depois de autenticar. Só o hash SHA-256 do token é guardado;
/// o token em si existe apenas na resposta de criação.
/// </summary>
public sealed class FamilyInvitation : Entity
{
    public const int TokenSizeInBytes = 32;
    public const int TokenHashSizeInBytes = 32;

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(48);

    private FamilyInvitation()
    {
        TokenHash = [];
    }

    public Guid FamilyId { get; private init; }

    public Guid CreatedByUserId { get; private init; }

    /// <summary>SHA-256 do token (32 bytes).</summary>
    public byte[] TokenHash { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset ExpiresAt { get; private init; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    public Guid? ConsumedByUserId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Cria o convite e devolve também o token em texto puro, que não é persistido.</summary>
    public static (FamilyInvitation Invitation, byte[] Token) Create(Guid familyId, Guid createdByUserId, DateTimeOffset now)
    {
        var token = RandomNumberGenerator.GetBytes(TokenSizeInBytes);
        var invitation = new FamilyInvitation
        {
            FamilyId = familyId,
            CreatedByUserId = createdByUserId,
            TokenHash = HashToken(token),
            CreatedAt = now,
            ExpiresAt = now.Add(DefaultLifetime),
        };
        return (invitation, token);
    }

    public static byte[] HashToken(ReadOnlySpan<byte> token) => SHA256.HashData(token);

    public FamilyInvitationStatus GetStatus(DateTimeOffset now) => this switch
    {
        { ConsumedAt: not null } => FamilyInvitationStatus.Consumed,
        { RevokedAt: not null } => FamilyInvitationStatus.Revoked,
        _ when ExpiresAt <= now => FamilyInvitationStatus.Expired,
        _ => FamilyInvitationStatus.Pending,
    };
}

public enum FamilyInvitationStatus
{
    Pending,
    Consumed,
    Revoked,
    Expired,
}

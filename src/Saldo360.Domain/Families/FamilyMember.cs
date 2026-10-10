using Saldo360.Domain.Common;

namespace Saldo360.Domain.Families;

/// <summary>Vínculo de um usuário interno a um núcleo familiar. Um usuário aparece no máximo uma vez por família.</summary>
public sealed class FamilyMember : Entity
{
    private FamilyMember()
    {
    }

    public Guid FamilyId { get; private init; }

    public Guid UserId { get; private init; }

    public FamilyRole Role { get; private set; }

    public DateTimeOffset JoinedAt { get; private init; }

    /// <summary>Vínculo de quem entra por convite: o papel é sempre <see cref="FamilyRole.Member"/>.</summary>
    public static FamilyMember JoinAsMember(Guid familyId, Guid userId, DateTimeOffset now) =>
        Create(familyId, userId, FamilyRole.Member, now);

    internal static FamilyMember Create(Guid familyId, Guid userId, FamilyRole role, DateTimeOffset now) => new()
    {
        FamilyId = familyId,
        UserId = userId,
        Role = role,
        JoinedAt = now,
    };
}

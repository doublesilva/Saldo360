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

    internal static FamilyMember Create(Guid familyId, Guid userId, FamilyRole role, DateTimeOffset now) => new()
    {
        FamilyId = familyId,
        UserId = userId,
        Role = role,
        JoinedAt = now,
    };
}

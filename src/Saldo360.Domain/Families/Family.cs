using Saldo360.Domain.Common;

namespace Saldo360.Domain.Families;

/// <summary>
/// Núcleo familiar: a unidade do orçamento conjunto. Nasce com exatamente um membro, o criador, como Owner.
/// Não há limite de membros.
/// </summary>
public sealed class Family : Entity
{
    public const int NameMaxLength = 100;

    private readonly List<FamilyMember> _members = [];

    private Family()
    {
        Name = string.Empty;
    }

    public string Name { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public IReadOnlyCollection<FamilyMember> Members => _members;

    /// <summary>Cria a família com o criador como único Owner. Valide o nome com <see cref="TryNormalizeName"/> antes.</summary>
    public static Family Create(string name, Guid ownerUserId, DateTimeOffset now)
    {
        if (!TryNormalizeName(name, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(name));
        }

        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("O criador é obrigatório.", nameof(ownerUserId));
        }

        var family = new Family { Name = normalized, CreatedAt = now };
        family._members.Add(FamilyMember.Create(family.Id, ownerUserId, FamilyRole.Owner, now));
        return family;
    }

    /// <summary>
    /// Regras do nome: obrigatório, sem espaços nas pontas e com até <see cref="NameMaxLength"/> caracteres.
    /// </summary>
    public static bool TryNormalizeName(string? name, out string normalized, out string? error)
    {
        normalized = name?.Trim() ?? string.Empty;
        error = normalized.Length switch
        {
            0 => "Informe o nome do núcleo familiar.",
            > NameMaxLength => $"O nome deve ter no máximo {NameMaxLength} caracteres.",
            _ => null,
        };
        return error is null;
    }
}

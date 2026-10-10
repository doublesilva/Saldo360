using Saldo360.Domain.Families;

namespace Saldo360.Application.Families;

/// <summary>Família na lista do usuário, com o papel dele nela.</summary>
public sealed record FamilySummary(Guid Id, string Name, DateTimeOffset CreatedAt, FamilyRole MyRole, int MemberCount);

/// <summary>Família com seus membros, vista por um integrante.</summary>
public sealed record FamilyDetails(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    FamilyRole MyRole,
    IReadOnlyList<FamilyMemberInfo> Members);

/// <summary>
/// Membro como exibido aos demais. Não inclui Id interno, emissor, subject nem e-mail.
/// </summary>
public sealed record FamilyMemberInfo(string DisplayName, FamilyRole Role, DateTimeOffset JoinedAt, bool IsCurrentUser);

/// <summary>Resultado da criação: a família criada ou os erros de validação por campo.</summary>
public sealed record CreateFamilyResult(FamilyDetails? Family, IDictionary<string, string[]>? Errors)
{
    public static CreateFamilyResult Created(FamilyDetails family) => new(family, null);

    public static CreateFamilyResult Invalid(string field, string message) =>
        new(null, new Dictionary<string, string[]> { [field] = [message] });
}

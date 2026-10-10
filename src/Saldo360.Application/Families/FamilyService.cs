using Saldo360.Domain.Families;

namespace Saldo360.Application.Families;

/// <summary>
/// Casos de uso do núcleo familiar. O usuário vem sempre da sessão (resolvido pela API);
/// nenhum identificador de usuário, papel ou identidade externa é aceito do cliente.
/// </summary>
public sealed class FamilyService(IFamilyRepository families, TimeProvider timeProvider)
{
    public const string NameField = "name";

    public async Task<CreateFamilyResult> CreateAsync(Guid userId, string? name, CancellationToken cancellationToken)
    {
        if (!Family.TryNormalizeName(name, out var normalized, out var error))
        {
            return CreateFamilyResult.Invalid(NameField, error!);
        }

        var family = Family.Create(normalized, userId, timeProvider.GetUtcNow());
        await families.AddAsync(family, cancellationToken);

        var created = await families.FindForMemberAsync(family.Id, userId, cancellationToken)
            ?? throw new InvalidOperationException("A família recém-criada não foi encontrada para o criador.");
        return CreateFamilyResult.Created(created);
    }

    public Task<IReadOnlyList<FamilySummary>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        families.ListForUserAsync(userId, cancellationToken);

    public Task<FamilyDetails?> GetAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        families.FindForMemberAsync(familyId, userId, cancellationToken);
}

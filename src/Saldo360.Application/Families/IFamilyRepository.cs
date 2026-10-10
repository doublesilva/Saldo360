using Saldo360.Domain.Families;

namespace Saldo360.Application.Families;

public interface IFamilyRepository
{
    /// <summary>Grava a família e seus vínculos numa única transação: ou tudo, ou nada.</summary>
    Task AddAsync(Family family, CancellationToken cancellationToken);

    /// <summary>Famílias das quais o usuário é membro.</summary>
    Task<IReadOnlyList<FamilySummary>> ListForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>A família com membros, ou null se ela não existir ou o usuário não for membro.</summary>
    Task<FamilyDetails?> FindForMemberAsync(Guid familyId, Guid userId, CancellationToken cancellationToken);
}

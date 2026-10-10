using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Families;
using Saldo360.Domain.Families;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Families;

internal sealed class FamilyRepository(Saldo360DbContext db) : IFamilyRepository
{
    private const string UnnamedMember = "Membro";

    public async Task AddAsync(Family family, CancellationToken cancellationToken)
    {
        // Família e vínculo Owner num único SaveChanges, que o EF executa numa transação:
        // se qualquer INSERT falhar, nada é gravado.
        db.Families.Add(family);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FamilySummary>> ListForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.FamilyMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Join(db.Families, member => member.FamilyId, family => family.Id, (member, family) => new
            {
                family.Id,
                family.Name,
                family.CreatedAt,
                member.Role,
                MemberCount = db.FamilyMembers.Count(other => other.FamilyId == family.Id),
            })
            .OrderBy(row => row.Name)
            .ThenBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new FamilySummary(row.Id, row.Name, row.CreatedAt, row.Role, row.MemberCount)).ToList();
    }

    public async Task<FamilyRole?> GetMemberRoleAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        await db.FamilyMembers
            .AsNoTracking()
            .Where(member => member.FamilyId == familyId && member.UserId == userId)
            .Select(member => (FamilyRole?)member.Role)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<FamilyDetails?> FindForMemberAsync(Guid familyId, Guid userId, CancellationToken cancellationToken)
    {
        // Família inexistente e família de outros têm a mesma resposta (null → 404).
        var family = await db.Families
            .AsNoTracking()
            .Where(f => f.Id == familyId && db.FamilyMembers.Any(m => m.FamilyId == f.Id && m.UserId == userId))
            .Select(f => new { f.Id, f.Name, f.CreatedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (family is null)
        {
            return null;
        }

        var members = await db.FamilyMembers
            .AsNoTracking()
            .Where(member => member.FamilyId == familyId)
            .Join(db.Users, member => member.UserId, user => user.Id, (member, user) => new
            {
                MemberId = member.Id,
                member.UserId,
                member.Role,
                member.JoinedAt,
                user.DisplayName,
            })
            .OrderBy(member => member.JoinedAt)
            .ToListAsync(cancellationToken);

        return new FamilyDetails(
            family.Id,
            family.Name,
            family.CreatedAt,
            members.Single(member => member.UserId == userId).Role,
            members
                .Select(member => new FamilyMemberInfo(
                    member.MemberId,
                    member.DisplayName ?? UnnamedMember,
                    member.Role,
                    member.JoinedAt,
                    member.UserId == userId))
                .ToList());
    }
}

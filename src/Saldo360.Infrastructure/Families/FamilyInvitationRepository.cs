using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Families;
using Saldo360.Domain.Families;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Families;

/// <summary>
/// Persistência dos convites. Consumo e revogação usam UPDATE condicional
/// (<c>WHERE consumed_at IS NULL AND revoked_at IS NULL …</c>): no PostgreSQL, a segunda transação a
/// tocar a mesma linha espera a primeira terminar e reavalia a condição, então só uma delas vence.
/// </summary>
internal sealed class FamilyInvitationRepository(Saldo360DbContext db) : IFamilyInvitationRepository
{
    public async Task AddAsync(FamilyInvitation invitation, CancellationToken cancellationToken)
    {
        db.FamilyInvitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RevokeInvitationOutcome> RevokeAsync(
        Guid familyId, Guid invitationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var revoked = await db.FamilyInvitations
            .Where(i => i.Id == invitationId && i.FamilyId == familyId
                && i.ConsumedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(i => i.RevokedAt, now), cancellationToken);
        if (revoked == 1)
        {
            return RevokeInvitationOutcome.Revoked;
        }

        var current = await db.FamilyInvitations
            .AsNoTracking()
            .Where(i => i.Id == invitationId && i.FamilyId == familyId)
            .Select(i => new { i.RevokedAt })
            .SingleOrDefaultAsync(cancellationToken);

        return current switch
        {
            null => RevokeInvitationOutcome.NotFound,
            { RevokedAt: not null } => RevokeInvitationOutcome.AlreadyRevoked,
            _ => RevokeInvitationOutcome.NotPending,
        };
    }

    public async Task<AcceptInvitationOutcome> AcceptAsync(
        byte[] tokenHash, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var invitation = await db.FamilyInvitations
            .AsNoTracking()
            .Where(i => i.TokenHash == tokenHash)
            .Select(i => new { i.Id, i.FamilyId })
            .SingleOrDefaultAsync(cancellationToken);
        if (invitation is null)
        {
            return Unavailable;
        }

        // Já participante: não duplica o vínculo, não muda o papel e não consome o convite.
        if (await IsMemberAsync(invitation.FamilyId, userId, cancellationToken))
        {
            return AlreadyMember(invitation.FamilyId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var consumed = await db.FamilyInvitations
                .Where(i => i.Id == invitation.Id
                    && i.ConsumedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(i => i.ConsumedAt, now)
                        .SetProperty(i => i.ConsumedByUserId, userId),
                    cancellationToken);

            if (consumed == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                // Pode ter sido consumido agora mesmo por este usuário (duas abas, por exemplo).
                return await IsMemberAsync(invitation.FamilyId, userId, cancellationToken)
                    ? AlreadyMember(invitation.FamilyId)
                    : Unavailable;
            }

            db.FamilyMembers.Add(FamilyMember.JoinAsMember(invitation.FamilyId, userId, now));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AcceptInvitationOutcome(AcceptInvitationStatus.Joined, invitation.FamilyId);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Entrou na família por outro convite em paralelo: desfaz o consumo deste.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return AlreadyMember(invitation.FamilyId);
        }
    }

    private static AcceptInvitationOutcome Unavailable => new(AcceptInvitationStatus.Unavailable);

    private static AcceptInvitationOutcome AlreadyMember(Guid familyId) =>
        new(AcceptInvitationStatus.AlreadyMember, familyId);

    private Task<bool> IsMemberAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        db.FamilyMembers.AnyAsync(m => m.FamilyId == familyId && m.UserId == userId, cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

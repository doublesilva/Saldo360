using Saldo360.Domain.Families;

namespace Saldo360.Application.Families;

public interface IFamilyInvitationRepository
{
    Task AddAsync(FamilyInvitation invitation, CancellationToken cancellationToken);

    /// <summary>
    /// Revoga o convite da família se ele ainda estiver pendente. A verificação e a escrita são uma única
    /// operação no banco, para não competir com uma aceitação simultânea.
    /// </summary>
    Task<RevokeInvitationOutcome> RevokeAsync(Guid familyId, Guid invitationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Aceita o convite do hash informado para o usuário: numa única transação, consome o convite
    /// (só se pendente) e grava o vínculo Member. Se o usuário já for membro, nada é alterado.
    /// </summary>
    Task<AcceptInvitationOutcome> AcceptAsync(byte[] tokenHash, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
}

public enum RevokeInvitationOutcome
{
    Revoked,
    AlreadyRevoked,
    NotPending,
    NotFound,
}

/// <param name="Status">Resultado.</param>
/// <param name="FamilyId">Família, quando o usuário entrou ou já era membro.</param>
public sealed record AcceptInvitationOutcome(AcceptInvitationStatus Status, Guid? FamilyId = null);

public enum AcceptInvitationStatus
{
    Joined,
    AlreadyMember,

    /// <summary>Token inexistente, expirado, revogado ou consumido: indistinguíveis para o cliente.</summary>
    Unavailable,
}

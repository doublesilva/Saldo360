namespace Saldo360.Infrastructure.Authentication;

/// <summary>
/// Sessão autenticada mantida pela API. O cookie do navegador carrega apenas <see cref="Id"/>;
/// o ticket (claims do usuário) fica aqui, protegido com Data Protection.
/// </summary>
public sealed class UserSession
{
    public required string Id { get; init; }

    /// <summary>Identificador do usuário no provedor (claim <c>sub</c>).</summary>
    public required string Subject { get; init; }

    public required byte[] ProtectedTicket { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; set; }
}

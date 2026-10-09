namespace Saldo360.Infrastructure.Authentication;

/// <summary>
/// Sessão autenticada mantida pela API. O cookie do navegador carrega apenas <see cref="Id"/>;
/// o ticket (claims do usuário) fica aqui, protegido com Data Protection.
/// </summary>
public sealed class UserSession
{
    public required string Id { get; init; }

    /// <summary>
    /// Emissor (claim <c>iss</c>) que autenticou o usuário. Nulo apenas em sessões criadas antes
    /// de o emissor ser registrado; essas sessões são recusadas e exigem novo login.
    /// </summary>
    public string? Issuer { get; init; }

    /// <summary>Identificador do usuário no emissor (claim <c>sub</c>). Só é único junto com <see cref="Issuer"/>.</summary>
    public required string Subject { get; init; }

    public required byte[] ProtectedTicket { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; set; }
}

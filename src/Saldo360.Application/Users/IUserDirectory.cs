namespace Saldo360.Application.Users;

/// <summary>Resolve o usuário interno a partir da identidade externa validada da sessão.</summary>
public interface IUserDirectory
{
    /// <summary>
    /// Devolve o Id interno do usuário <paramref name="issuer"/> + <paramref name="subject"/>, criando-o na
    /// primeira vez. Chamadas concorrentes para a mesma identidade devolvem o mesmo Id, sem duplicar.
    /// <paramref name="displayName"/> só atualiza o nome de exibição; nunca participa da identificação.
    /// </summary>
    Task<Guid> ResolveAsync(string issuer, string subject, string? displayName, CancellationToken cancellationToken);
}

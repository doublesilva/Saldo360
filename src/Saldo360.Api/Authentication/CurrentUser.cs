using System.Security.Claims;
using Saldo360.Application.Users;
using Saldo360.Infrastructure.Authentication;

namespace Saldo360.Api.Authentication;

/// <summary>
/// Usuário interno da requisição, resolvido (e criado na primeira vez) a partir do issuer + subject
/// da sessão validada. E-mail, username e <c>/api/me.id</c> não participam da resolução.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor, IUserDirectory users)
{
    private Guid? _userId;

    /// <summary>Id interno do usuário, ou null se a sessão não tiver identidade externa válida.</summary>
    public async Task<Guid?> GetUserIdAsync(CancellationToken cancellationToken)
    {
        if (_userId is not null)
        {
            return _userId;
        }

        var principal = httpContextAccessor.HttpContext?.User;
        if (principal is null || !ExternalIdentity.TryFromPrincipal(principal, out var identity))
        {
            return null;
        }

        var displayName = principal.FindFirstValue("name") ?? principal.FindFirstValue("preferred_username");
        _userId = await users.ResolveAsync(identity.Issuer, identity.Subject, displayName, cancellationToken);
        return _userId;
    }
}

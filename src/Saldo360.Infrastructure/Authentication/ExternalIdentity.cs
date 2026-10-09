using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace Saldo360.Infrastructure.Authentication;

/// <summary>
/// Identidade do usuário no provedor OIDC: o par <c>iss</c> + <c>sub</c>.
/// <c>sub</c> só é único dentro de um emissor, então nenhum dos dois identifica o usuário sozinho.
/// Ambos são strings opacas: comparadas de forma ordinal, sem conversão de tipo, caixa ou normalização.
/// </summary>
public sealed record ExternalIdentity(string Issuer, string Subject)
{
    public const string IssuerClaimType = "iss";
    public const string SubjectClaimType = "sub";

    /// <summary>
    /// Lê a identidade das claims, exigindo exatamente uma claim <c>iss</c> e uma <c>sub</c>, ambas preenchidas.
    /// </summary>
    public static bool TryFromPrincipal(ClaimsPrincipal principal, [NotNullWhen(true)] out ExternalIdentity? identity)
    {
        identity = null;
        var issuers = principal.FindAll(IssuerClaimType).ToList();
        var subjects = principal.FindAll(SubjectClaimType).ToList();

        if (issuers.Count != 1 || subjects.Count != 1 || !IsValid(issuers[0].Value) || !IsValid(subjects[0].Value))
        {
            return false;
        }

        identity = new ExternalIdentity(issuers[0].Value, subjects[0].Value);
        return true;
    }

    public static bool IsValid([NotNullWhen(true)] string? value) => !string.IsNullOrWhiteSpace(value);
}

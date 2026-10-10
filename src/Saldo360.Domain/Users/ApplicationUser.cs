using Saldo360.Domain.Common;

namespace Saldo360.Domain.Users;

/// <summary>
/// Usuário interno do Saldo360, vinculado a uma identidade externa (emissor OIDC + subject).
/// O par <see cref="Issuer"/> + <see cref="Subject"/> é único e opaco: comparado de forma
/// sensível à caixa e sem normalização. E-mail e nome nunca servem de chave.
/// </summary>
public sealed class ApplicationUser : Entity
{
    public const int IssuerMaxLength = 2048;
    public const int SubjectMaxLength = 255;
    public const int DisplayNameMaxLength = 200;

    private ApplicationUser()
    {
        Issuer = string.Empty;
        Subject = string.Empty;
    }

    public string Issuer { get; private init; }

    public string Subject { get; private init; }

    /// <summary>Nome para exibição aos demais membros; atualizado a partir da sessão. Não é identidade.</summary>
    public string? DisplayName { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static ApplicationUser Create(string issuer, string subject, string? displayName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        return new ApplicationUser
        {
            Issuer = issuer,
            Subject = subject,
            DisplayName = NormalizeDisplayName(displayName),
            CreatedAt = now,
        };
    }

    /// <summary>Atualiza o nome de exibição; devolve true se mudou.</summary>
    public bool UpdateDisplayName(string? displayName)
    {
        var normalized = NormalizeDisplayName(displayName);
        if (normalized is null || normalized == DisplayName)
        {
            return false;
        }

        DisplayName = normalized;
        return true;
    }

    private static string? NormalizeDisplayName(string? displayName)
    {
        var trimmed = displayName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= DisplayNameMaxLength ? trimmed : trimmed[..DisplayNameMaxLength];
    }
}

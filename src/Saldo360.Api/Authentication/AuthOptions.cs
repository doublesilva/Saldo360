namespace Saldo360.Api.Authentication;

/// <summary>
/// Configuração do provedor OpenID Connect (seção <c>Authentication:Oidc</c>).
/// <see cref="ClientSecret"/> deve vir de user-secrets ou variável de ambiente, nunca de appsettings.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Authentication:Oidc";

    /// <summary>URL do emissor (issuer); a discovery é lida de <c>{Authority}/.well-known/openid-configuration</c>.</summary>
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>Caminho, relativo à API, que recebe o retorno do provedor. Registre-o como redirect URI.</summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>
    /// Opcional: CA (PEM) que assina o certificado do provedor, para provedores com CA própria
    /// (ex.: Authelia de desenvolvimento). Só essa CA é aceita no canal servidor-provedor.
    /// Caminho absoluto ou relativo ao content root da API.
    /// </summary>
    public string? BackchannelCaCertificatePath { get; set; }
}

/// <summary>Configuração do cookie de sessão (seção <c>Authentication:Session</c>).</summary>
public sealed class SessionOptions
{
    public const string SectionName = "Authentication:Session";

    /// <summary>Nome do cookie. Em produção use o prefixo <c>__Host-</c> (exige HTTPS).</summary>
    public string CookieName { get; set; } = "__Host-saldo360.session";

    /// <summary>Tempo de inatividade até a sessão expirar (renovação deslizante).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(8);
}

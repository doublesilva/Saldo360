namespace Saldo360.Api.Authentication;

/// <summary>
/// Configuração do provedor OpenID Connect (seção <c>Authentication:Oidc</c>).
/// O segredo vem de <see cref="ClientSecretFile"/> ou de <see cref="ClientSecret"/> (user-secrets ou
/// variável de ambiente), nunca de appsettings.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Authentication:Oidc";

    /// <summary>URL do emissor (issuer); a discovery é lida de <c>{Authority}/.well-known/openid-configuration</c>.</summary>
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Opcional: arquivo com o segredo original do client (ex.: <c>/run/secrets/…</c> montado só para leitura).
    /// Quando definido, tem precedência sobre <see cref="ClientSecret"/>. Caminho absoluto ou relativo ao content root.
    /// </summary>
    public string? ClientSecretFile { get; set; }

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

/// <summary>
/// Proxies reversos confiáveis (seção <c>ReverseProxy</c>). Só requisições vindas deles podem definir
/// esquema e host por <c>X-Forwarded-Proto</c>/<c>X-Forwarded-Host</c>; de qualquer outra origem os headers são ignorados.
/// O loopback (127.0.0.0/8 e ::1) continua confiável, como no padrão do ASP.NET Core.
/// </summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>IPs exatos dos proxies (ex.: <c>172.20.0.2</c>).</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Redes em notação CIDR (ex.: <c>172.20.0.0/16</c> da rede Docker do Caddy).</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>
    /// Hosts aceitos em <c>X-Forwarded-Host</c> (ex.: <c>saldo360.exemplo.com.br</c>).
    /// Vazio aceita qualquer host enviado por um proxy confiável.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];
}

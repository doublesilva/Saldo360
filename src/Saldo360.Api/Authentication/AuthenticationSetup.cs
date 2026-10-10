using System.Net.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Saldo360.Infrastructure.Authentication;

namespace Saldo360.Api.Authentication;

public static class AuthenticationSetup
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    // Claims de exibição guardadas na sessão, além de iss e sub. Tokens e demais claims do provedor são descartados.
    private static readonly HashSet<string> ProfileClaimTypes = ["name", "preferred_username", "email"];

    // Identidade do ID token validado, mantida só durante o callback. As ClaimActions padrão do handler
    // apagam a claim "iss" depois do OnTokenValidated, então ela não pode ser relida do principal depois.
    private static readonly object ValidatedIdentityKey = new();

    private const string OidcHandlerLogCategory = "Microsoft.AspNetCore.Authentication.OpenIdConnect";

    // Códigos de erro padronizados (RFC 6749, OIDC Core, RFC 9126) que podem ir para o log como estão.
    // Qualquer outro valor vindo do provedor é tratado como texto livre e não é registrado.
    private static readonly HashSet<string> StandardProviderErrors =
    [
        "invalid_request", "unauthorized_client", "access_denied", "unsupported_response_type", "invalid_scope",
        "server_error", "temporarily_unavailable", "invalid_client", "invalid_grant", "unsupported_grant_type",
        "interaction_required", "login_required", "account_selection_required", "consent_required",
        "invalid_request_uri", "invalid_request_object", "request_not_supported", "request_uri_not_supported",
        "registration_not_supported",
    ];

    public static IServiceCollection AddSaldo360Authentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var oidc = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
        var session = configuration.GetSection(SessionOptions.SectionName).Get<SessionOptions>() ?? new SessionOptions();
        oidc.ClientSecret = ResolveClientSecret(oidc, environment);
        EnsureConfigured(oidc);

        // O handler OIDC registra mensagens do provedor (error_description) e, em níveis detalhados, URLs
        // de callback com o code. Ele fica silenciado; as falhas são registradas por OnRemoteFailure
        // com motivo controlado e id de correlação.
        services.AddLogging(logging => logging.AddFilter(OidcHandlerLogCategory, LogLevel.None));

        // Carregada já na inicialização: um caminho errado falha no startup, não no primeiro login.
        var backchannelCa = string.IsNullOrWhiteSpace(oidc.BackchannelCaCertificatePath)
            ? null
            : LoadCaCertificate(Path.GetFullPath(oidc.BackchannelCaCertificatePath, environment.ContentRootPath));

        // Em desenvolvimento o frontend e a API rodam em HTTP local; fora dele os cookies só trafegam em HTTPS.
        var securePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieScheme;
                options.DefaultChallengeScheme = CookieScheme;
            })
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = session.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = securePolicy;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = session.IdleTimeout;
                options.SlidingExpiration = true;

                // É uma API: responde com status em vez de redirecionar para uma página de login.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(OidcScheme, options =>
            {
                options.SignInScheme = CookieScheme;
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.CallbackPath = oidc.CallbackPath;

                // Authorization code + PKCE; o retorno vem por query string (navegação GET),
                // o que funciona com cookies SameSite=Lax mesmo com o provedor em outro site.
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;

                options.Scope.Clear();
                foreach (var scope in oidc.Scopes)
                {
                    options.Scope.Add(scope);
                }

                options.GetClaimsFromUserInfoEndpoint = true;
                // Os mapeamentos padrão do userinfo não incluem preferred_username.
                options.ClaimActions.MapUniqueJsonKey("preferred_username", "preferred_username");
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.DisableTelemetry = true;
                options.TokenValidationParameters.NameClaimType = "name";

                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = securePolicy;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = securePolicy;

                if (backchannelCa is not null)
                {
                    options.BackchannelHttpHandler = CreateHandlerTrustingOnly(backchannelCa);
                }

                // iss e sub vêm do ID token já validado pelo middleware (assinatura, emissor, audiência, nonce),
                // nunca da requisição, do navegador ou da configuração (Authority).
                options.Events.OnTokenValidated = context =>
                {
                    var issuer = context.SecurityToken?.Issuer;
                    var subject = context.SecurityToken?.Subject;
                    if (!ExternalIdentity.IsValid(issuer) || !ExternalIdentity.IsValid(subject))
                    {
                        context.Fail(new OidcIdentityException("O ID token não contém issuer ou subject válidos."));
                        return Task.CompletedTask;
                    }

                    context.HttpContext.Items[ValidatedIdentityKey] = new ExternalIdentity(issuer, subject);
                    return Task.CompletedTask;
                };
                options.Events.OnTicketReceived = context =>
                {
                    if (context.HttpContext.Items[ValidatedIdentityKey] is not ExternalIdentity identity)
                    {
                        context.Fail(new OidcIdentityException("Identidade validada do ID token ausente."));
                        return Task.CompletedTask;
                    }

                    context.Principal = CreateSessionPrincipal(context.Principal!, identity);
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    // Nunca registra a mensagem/exceção original: ela pode conter texto livre do provedor.
                    var reason = ClassifyFailure(context.Failure);
                    var correlationId = context.HttpContext.TraceIdentifier;
                    context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(AuthenticationSetup))
                        .LogWarning("Login OIDC não concluído. Motivo: {Reason}. Correlação: {CorrelationId}.", reason, correlationId);

                    context.Response.Redirect($"/?login=failed&ref={Uri.EscapeDataString(correlationId)}");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });

        // As sessões ficam no banco; o cookie leva apenas o identificador.
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<ITicketStore>((options, store) => options.SessionStore = store);

        services.AddAuthorization();

        services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeaderName;
            options.Cookie.Name = environment.IsDevelopment() ? "saldo360.csrf" : "__Host-saldo360.csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = securePolicy;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        return services;
    }


    /// <summary>Lê o segredo de <see cref="OidcOptions.ClientSecretFile"/>, quando configurado; senão usa ClientSecret.</summary>
    private static string ResolveClientSecret(OidcOptions oidc, IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(oidc.ClientSecretFile))
        {
            return oidc.ClientSecret;
        }

        var path = Path.GetFullPath(oidc.ClientSecretFile, environment.ContentRootPath);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Arquivo do segredo do client não encontrado ({OidcOptions.SectionName}:ClientSecretFile = '{path}').");
        }

        // Só remove a quebra de linha final que editores costumam acrescentar.
        var secret = File.ReadAllText(path).TrimEnd('\r', '\n');
        return string.IsNullOrWhiteSpace(secret)
            ? throw new InvalidOperationException(
                $"O arquivo do segredo do client está vazio ({OidcOptions.SectionName}:ClientSecretFile = '{path}').")
            : secret;
    }

    private static void EnsureConfigured(OidcOptions oidc)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(oidc.Authority)) missing.Add($"{OidcOptions.SectionName}:Authority");
        if (string.IsNullOrWhiteSpace(oidc.ClientId)) missing.Add($"{OidcOptions.SectionName}:ClientId");
        if (string.IsNullOrWhiteSpace(oidc.ClientSecret)) missing.Add($"{OidcOptions.SectionName}:ClientSecret");

        if (missing.Count > 0)
        {
            // Lista apenas as chaves ausentes, nunca valores.
            throw new InvalidOperationException(
                $"Configuração de autenticação incompleta. Defina: {string.Join(", ", missing)}. " +
                "O segredo deve vir de user-secrets, da variável de ambiente Authentication__Oidc__ClientSecret " +
                "ou de um arquivo indicado em Authentication__Oidc__ClientSecretFile.");
        }
    }

    private static X509Certificate2 LoadCaCertificate(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Certificado de CA do provedor não encontrado em '{path}' ({OidcOptions.SectionName}:BackchannelCaCertificatePath). " +
                "Veja docs/autenticacao.md (CA do Caddy do servidor ou CA do Authelia local).");
        }

        return X509CertificateLoader.LoadCertificateFromFile(path);
    }

    /// <summary>
    /// Principal da sessão: a identidade validada (iss + sub) e apenas as claims de exibição.
    /// Qualquer iss/sub vindo de outra fonte (ex.: userinfo) é ignorado.
    /// </summary>
    private static ClaimsPrincipal CreateSessionPrincipal(ClaimsPrincipal principal, ExternalIdentity identity)
    {
        var claims = principal.Claims
            .Where(claim => ProfileClaimTypes.Contains(claim.Type))
            .Select(claim => new Claim(claim.Type, claim.Value))
            .Prepend(new Claim(ExternalIdentity.SubjectClaimType, identity.Subject))
            .Prepend(new Claim(ExternalIdentity.IssuerClaimType, identity.Issuer));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, principal.Identity?.AuthenticationType, "name", "role"));
    }

    /// <summary>
    /// Motivo controlado para o log de uma falha de login: só categorias fixas e códigos de erro padronizados.
    /// Mensagens, descrições e respostas do provedor nunca são devolvidas.
    /// </summary>
    internal static string ClassifyFailure(Exception? failure) => failure switch
    {
        OidcIdentityException => "identidade_invalida",
        OpenIdConnectProtocolException protocol when protocol.Data["error"] is string code
            && StandardProviderErrors.Contains(code) => $"provedor:{code}",
        OpenIdConnectProtocolException => "provedor:erro_nao_padronizado",
        SecurityTokenException => "id_token_invalido",
        HttpRequestException => "provedor_inacessivel",
        { Message: "Correlation failed." } => "correlacao_invalida",
        { Message: "The oauth state was missing or invalid." } => "state_invalido",
        { Message: "Access was denied by the resource owner or by the remote server." } => "provedor:access_denied",
        null => "falha_nao_informada",
        _ => "falha_nao_classificada",
    };

    /// <summary>
    /// Handler que só aceita certificados emitidos pela CA informada (sem cair no repositório do sistema).
    /// </summary>
    private static SocketsHttpHandler CreateHandlerTrustingOnly(X509Certificate2 ca) => new()
    {
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, certificate, presentedChain, errors) =>
                ValidateServerCertificate(ca, certificate, presentedChain, errors),
        },
    };

    /// <summary>
    /// Aceita o certificado do provedor somente se: não houver erro de nome nem ausência de certificado
    /// (verificados pelo TLS do sistema), a cadeia terminar em <paramref name="ca"/> e o certificado servir
    /// para autenticação de servidor. Intermediárias enviadas no handshake (ex.: a do Caddy) são usadas
    /// apenas para montar a cadeia, nunca como raiz confiável.
    /// </summary>
    internal static bool ValidateServerCertificate(
        X509Certificate2 ca, X509Certificate? certificate, X509Chain? presentedChain, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // serverAuth

        if (presentedChain is not null)
        {
            foreach (var element in presentedChain.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        var leaf = certificate as X509Certificate2
            ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return chain.Build(leaf);
    }
}

/// <summary>Falha do Saldo360 ao extrair a identidade do ID token validado.</summary>
internal sealed class OidcIdentityException(string message) : Exception(message);

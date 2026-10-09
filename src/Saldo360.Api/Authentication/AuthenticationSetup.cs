using System.Net.Security;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Saldo360.Api.Authentication;

public static class AuthenticationSetup
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    // Claims guardadas na sessão. Tokens e demais claims do provedor são descartados.
    private static readonly HashSet<string> SessionClaimTypes = ["sub", "name", "preferred_username", "email"];

    public static IServiceCollection AddSaldo360Authentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var oidc = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
        var session = configuration.GetSection(SessionOptions.SectionName).Get<SessionOptions>() ?? new SessionOptions();
        EnsureConfigured(oidc);

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

                options.Events.OnTicketReceived = context =>
                {
                    context.Principal = KeepSessionClaims(context.Principal!);
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(AuthenticationSetup));
                    logger.LogWarning("Login OIDC não concluído: {Reason}", context.Failure?.Message);

                    context.Response.Redirect("/?login=failed");
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
                "O ClientSecret deve vir de user-secrets ou da variável de ambiente Authentication__Oidc__ClientSecret.");
        }
    }

    private static X509Certificate2 LoadCaCertificate(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Certificado de CA do provedor não encontrado em '{path}' ({OidcOptions.SectionName}:BackchannelCaCertificatePath). " +
                "Em desenvolvimento, gere-o com scripts/setup-dev-auth.ps1.");
        }

        return X509CertificateLoader.LoadCertificateFromFile(path);
    }

    private static ClaimsPrincipal KeepSessionClaims(ClaimsPrincipal principal)
    {
        var claims = principal.Claims.Where(claim => SessionClaimTypes.Contains(claim.Type));
        var identity = new ClaimsIdentity(claims, principal.Identity?.AuthenticationType, "name", "role");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Handler que só aceita certificados emitidos pela CA informada (sem cair no repositório do sistema).
    /// </summary>
    private static SocketsHttpHandler CreateHandlerTrustingOnly(X509Certificate2 ca)
    {
        return new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    // Erros de nome ou ausência de certificado nunca são aceitos; só a cadeia é revalidada.
                    if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
                    {
                        return false;
                    }

                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(ca);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                    var leaf = certificate as X509Certificate2
                        ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    return chain.Build(leaf);
                },
            },
        };
    }
}

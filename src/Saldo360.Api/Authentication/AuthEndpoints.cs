using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Saldo360.Infrastructure.Authentication;
using Saldo360.Application.Users;

namespace Saldo360.Api.Authentication;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth");
        auth.MapGet("/login", Login);
        auth.MapGet("/csrf", GetCsrfToken);
        auth.MapPost("/logout", Logout).AddEndpointFilter<ValidateCsrfTokenFilter>();

        app.MapGet("/api/me", GetCurrentUser).RequireAuthorization();

        return app;
    }

    /// <summary>Inicia o login no provedor e, ao final, volta para <paramref name="returnUrl"/> (apenas URLs locais).</summary>
    private static IResult Login(HttpContext context, string? returnUrl, bool? reauthenticate)
    {
        var target = IsLocalUrl(returnUrl) ? returnUrl! : "/";

        if (context.User.Identity?.IsAuthenticated == true && reauthenticate != true)
        {
            return Results.LocalRedirect(target);
        }

        // A valid application cookie does not renew an expired/missing profile access token.
        // Reuse OIDC + PKCE; never replace it with an ID token or a privileged CPF lookup.
        return Results.Challenge(new OpenIdConnectChallengeProperties
        { RedirectUri = target, Prompt = reauthenticate == true ? "login" : null }, [AuthenticationSetup.OidcScheme]);
    }

    /// <summary>Entrega o token CSRF que o frontend envia no header <c>X-CSRF-TOKEN</c> das operações que alteram estado.</summary>
    private static IResult GetCsrfToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new CsrfTokenResponse(tokens.RequestToken!));
    }

    /// <summary>Remove a sessão do servidor e apaga o cookie. Idempotente.</summary>
    private static async Task<IResult> Logout(HttpContext context, ILoggerFactory loggerFactory)
    {
        var hadIdentity = ExternalIdentity.TryFromPrincipal(context.User, out var identity);
        await context.SignOutAsync(AuthenticationSetup.CookieScheme);

        if (hadIdentity)
        {
            loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogInformation(
                "Sessão encerrada para o usuário {Subject} do emissor {Issuer}.", identity!.Subject, identity.Issuer);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> GetCurrentUser(HttpContext context, ClaimsPrincipal user, ICurrentUserCpf cpf, CancellationToken ct)
    {
        if (!ExternalIdentity.TryFromPrincipal(user, out var identity))
        {
            return Results.Unauthorized();
        }

        var username = user.FindFirstValue("preferred_username");
        var profile = await cpf.GetAsync(ct);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new CurrentUserResponse(
            Id: identity.Subject,
            Issuer: identity.Issuer,
            Subject: identity.Subject,
            DisplayName: user.FindFirstValue("name") ?? username ?? identity.Subject,
            Username: username,
            Email: user.FindFirstValue("email"),
            CpfStatus: profile.Status.ToString()));
    }

    // Mesma regra do UrlHelper do MVC: caminho absoluto local, sem "//" ou "/\" (que levariam a outro host).
    private static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(char.IsControl);
}

/// <summary>Usuário da sessão atual.</summary>
/// <param name="Id">
/// Igual a <paramref name="Subject"/>, mantido temporariamente por compatibilidade com o frontend.
/// Sozinho NÃO identifica o usuário globalmente: o mesmo subject pode existir em outro emissor.
/// Vínculos (ex.: com famílias) devem usar o par <paramref name="Issuer"/> + <paramref name="Subject"/>.
/// </param>
/// <param name="Issuer">Emissor que autenticou o usuário (claim <c>iss</c> do ID token validado).</param>
/// <param name="Subject">Identificador do usuário no emissor (claim <c>sub</c>), string opaca.</param>
public sealed record CurrentUserResponse(
    string Id,
    string Issuer,
    string Subject,
    string DisplayName,
    string? Username,
    string? Email,
    string CpfStatus = "SessionInvalid");

public sealed record CsrfTokenResponse(string Token);

internal sealed class ValidateCsrfTokenFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await antiforgery.IsRequestValidAsync(context.HttpContext))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Token CSRF ausente ou inválido.");
        }

        return await next(context);
    }
}

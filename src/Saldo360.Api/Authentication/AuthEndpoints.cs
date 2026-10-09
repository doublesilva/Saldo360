using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

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
    private static IResult Login(HttpContext context, string? returnUrl)
    {
        var target = IsLocalUrl(returnUrl) ? returnUrl! : "/";

        if (context.User.Identity?.IsAuthenticated == true)
        {
            return Results.LocalRedirect(target);
        }

        return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [AuthenticationSetup.OidcScheme]);
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
        var subject = context.User.FindFirstValue("sub");
        await context.SignOutAsync(AuthenticationSetup.CookieScheme);

        if (subject is not null)
        {
            loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogInformation("Sessão encerrada para o usuário {Subject}.", subject);
        }

        return Results.NoContent();
    }

    private static IResult GetCurrentUser(HttpContext context, ClaimsPrincipal user)
    {
        var id = user.FindFirstValue("sub");
        if (string.IsNullOrEmpty(id))
        {
            return Results.Unauthorized();
        }

        var username = user.FindFirstValue("preferred_username");
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new CurrentUserResponse(
            id,
            user.FindFirstValue("name") ?? username ?? id,
            username,
            user.FindFirstValue("email")));
    }

    // Mesma regra do UrlHelper do MVC: caminho absoluto local, sem "//" ou "/\" (que levariam a outro host).
    private static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(char.IsControl);
}

public sealed record CurrentUserResponse(string Id, string DisplayName, string? Username, string? Email);

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

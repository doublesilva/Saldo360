using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Families;

namespace Saldo360.Api.Families;

public static class FamilyEndpoints
{
    public static IEndpointRouteBuilder MapFamilyEndpoints(this IEndpointRouteBuilder app)
    {
        var families = app.MapGroup("/api/families").RequireAuthorization();
        families.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        families.MapGet("/", List);
        families.MapGet("/{id:guid}", Get);
        return app;
    }

    /// <summary>Cria a família com o usuário da sessão como único Owner.</summary>
    private static async Task<IResult> Create(
        CreateFamilyRequest? request, CurrentUser currentUser, FamilyService families, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await families.CreateAsync(userId, request?.Name, cancellationToken);
        return result.Family is { } family
            ? Results.Created($"/api/families/{family.Id}", family)
            : Results.ValidationProblem(result.Errors!);
    }

    private static async Task<IResult> List(CurrentUser currentUser, FamilyService families, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await families.ListAsync(userId, cancellationToken));
    }

    /// <summary>404 tanto para família inexistente quanto para família da qual o usuário não é membro.</summary>
    private static async Task<IResult> Get(
        Guid id, CurrentUser currentUser, FamilyService families, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await families.GetAsync(id, userId, cancellationToken) is { } family
            ? Results.Ok(family)
            : Results.NotFound();
    }
}

/// <summary>
/// Corpo de POST /api/families: somente o nome. Qualquer outro campo (userId, role, issuer, subject…)
/// faz a requisição ser recusada com 400, em vez de ser ignorado silenciosamente.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateFamilyRequest(string? Name);

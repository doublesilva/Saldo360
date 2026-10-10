using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Families;

namespace Saldo360.Api.Families;

public static class FamilyInvitationEndpoints
{
    /// <summary>Mensagem única para token inexistente, expirado, revogado ou já usado: não revela a família.</summary>
    public const string UnavailableInvitationTitle = "Convite inválido, expirado ou já utilizado.";

    public static IEndpointRouteBuilder MapFamilyInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        var invitations = app.MapGroup("/api/families/{familyId:guid}/invitations").RequireAuthorization();
        invitations.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        invitations.MapDelete("/{invitationId:guid}", Revoke).AddEndpointFilter<ValidateCsrfTokenFilter>();

        app.MapPost("/api/family-invitations/accept", Accept)
            .RequireAuthorization()
            .AddEndpointFilter<ValidateCsrfTokenFilter>();

        return app;
    }

    /// <summary>Somente Owner. O token só aparece nesta resposta.</summary>
    private static async Task<IResult> Create(
        Guid familyId, HttpContext context, CurrentUser currentUser, FamilyInvitationService invitations,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await invitations.CreateAsync(familyId, userId, cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return result.Access switch
        {
            InvitationAccess.NotFound => Results.NotFound(),
            InvitationAccess.Forbidden => Results.Forbid(),
            _ => Results.Json(result.Invitation, statusCode: StatusCodes.Status201Created),
        };
    }

    /// <summary>Somente Owner. Revogar de novo é idempotente; convite já usado ou expirado dá 409.</summary>
    private static async Task<IResult> Revoke(
        Guid familyId, Guid invitationId, CurrentUser currentUser, FamilyInvitationService invitations,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await invitations.RevokeAsync(familyId, invitationId, userId, cancellationToken);
        return (result.Access, result.Outcome) switch
        {
            (InvitationAccess.NotFound, _) => Results.NotFound(),
            (InvitationAccess.Forbidden, _) => Results.Forbid(),
            (_, RevokeInvitationOutcome.Revoked or RevokeInvitationOutcome.AlreadyRevoked) => Results.NoContent(),
            (_, RevokeInvitationOutcome.NotPending) => Results.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "O convite não está mais pendente."),
            _ => Results.NotFound(),
        };
    }

    /// <summary>
    /// Aceita o convite para o usuário da sessão, que entra como Member. Devolve a família.
    /// Já participante: 200 com <c>alreadyMember = true</c>, sem alterar papel nem consumir o convite.
    /// </summary>
    private static async Task<IResult> Accept(
        AcceptInvitationRequest? request, HttpContext context, CurrentUser currentUser,
        FamilyInvitationService invitations, FamilyService families, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        context.Response.Headers.CacheControl = "no-store";
        var outcome = await invitations.AcceptAsync(request?.Token, userId, cancellationToken);
        if (outcome.Status == AcceptInvitationStatus.Unavailable || outcome.FamilyId is not { } familyId)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: UnavailableInvitationTitle);
        }

        var family = await families.GetAsync(familyId, userId, cancellationToken);
        return family is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: UnavailableInvitationTitle)
            : Results.Ok(new AcceptInvitationResponse(outcome.Status == AcceptInvitationStatus.AlreadyMember, family));
    }
}

/// <summary>Corpo do aceite: somente o token. Papel e identidade do novo membro nunca vêm do cliente.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptInvitationRequest(string? Token);

public sealed record AcceptInvitationResponse(bool AlreadyMember, FamilyDetails Family);

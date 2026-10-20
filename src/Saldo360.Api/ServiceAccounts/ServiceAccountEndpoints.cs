using Saldo360.Api.Authentication;
using Saldo360.Application.ServiceAccounts;
using Saldo360.Domain.ServiceAccounts;

namespace Saldo360.Api.ServiceAccounts;

public static class ServiceAccountEndpoints
{
    public static IEndpointRouteBuilder MapServiceAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/service-accounts").RequireAuthorization();
        group.MapGet("/", async (CurrentUser user, ServiceAccountService service, CancellationToken ct) =>
            await user.GetUserIdAsync(ct) is { } id ? Results.Ok(await service.ListAsync(id, ct)) : Results.Unauthorized());
        group.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/identifiers", AddIdentifier).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }
    private static async Task<IResult> Create(CreateServiceAccountRequest request, CurrentUser user, ServiceAccountService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        if (!Enum.TryParse<ServiceType>(request.ServiceType, false, out var type) || !Enum.IsDefined(type) || request.Issuer is null || request.DisplayName is null)
            return Invalid("Informe tipo, emissor e nome válidos.");
        try
        {
            var account = await service.CreateAsync(userId, type, request.Issuer, request.DisplayName, ct);
            return Results.Created("/api/me/service-accounts", new { account.Id, account.ServiceType, account.Issuer, account.DisplayName, account.IsActive });
        }
        catch (ArgumentException) { return Invalid("Informe emissor e nome válidos."); }
    }
    private static async Task<IResult> AddIdentifier(Guid id, AddServiceIdentifierRequest request, CurrentUser user, ServiceAccountService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        if (!Enum.TryParse<ServiceIdentifierType>(request.Type, false, out var type) || !Enum.IsDefined(type) || request.Value is null)
            return Invalid("Informe tipo e identificador válidos.");
        try
        {
            return await service.AddIdentifierAsync(userId, id, type, request.Value, ct) switch
            {
                "NotFound" => Results.NotFound(),
                "Inactive" => Results.Conflict(new { code = "SERVICE_ACCOUNT_INACTIVE" }),
                "ProtectionUnavailable" => Results.Problem(statusCode: 503, title: "Proteção de identificadores indisponível.", extensions: new Dictionary<string, object?> { ["code"] = "IDENTIFIER_PROTECTION_UNAVAILABLE" }),
                "Conflict" => Results.Conflict(new { code = "SERVICE_IDENTIFIER_CONFLICT" }),
                _ => Results.StatusCode(201)
            };
        }
        catch (ArgumentException) { return Invalid("Identificador inválido."); }
    }
    private static IResult Invalid(string message) => Results.ValidationProblem(new Dictionary<string, string[]> { ["serviceAccount"] = [message] });
}
public sealed record CreateServiceAccountRequest(string? ServiceType, string? Issuer, string? DisplayName);
public sealed record AddServiceIdentifierRequest(string? Type, string? Value);

using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Categories;

namespace Saldo360.Api.Categories;

public static class FinancialCategoryEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var categories = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/categories").RequireAuthorization();
        categories.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        categories.MapGet("/", List);
        categories.MapGet("/{categoryId:guid}", Get);
        categories.MapPut("/{categoryId:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Create(
        Guid workspaceId, CreateCategoryRequest? request, CurrentUser currentUser, FinancialCategoryService categories,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await categories.CreateAsync(workspaceId, userId, request?.Name, request?.Type, request?.ParentCategoryId, cancellationToken);
        return ToResponse(result, category => Results.Created($"/api/financial-workspaces/{workspaceId}/categories/{category.Id}", category));
    }

    /// <summary>Categorias ativas; com <c>includeInactive=true</c>, também as desativadas.</summary>
    private static async Task<IResult> List(
        Guid workspaceId, bool? includeInactive, CurrentUser currentUser, FinancialCategoryService categories,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await categories.ListAsync(workspaceId, userId, includeInactive ?? false, cancellationToken) is { } list
            ? Results.Ok(list)
            : Results.NotFound();
    }

    private static async Task<IResult> Get(
        Guid workspaceId, Guid categoryId, CurrentUser currentUser, FinancialCategoryService categories,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await categories.GetAsync(workspaceId, categoryId, userId, cancellationToken) is { } category
            ? Results.Ok(category)
            : Results.NotFound();
    }

    /// <summary>Renomeia e ativa/desativa (name e isActive obrigatórios). O tipo não pode ser enviado.</summary>
    private static async Task<IResult> Update(
        Guid workspaceId, Guid categoryId, UpdateCategoryRequest? request, CurrentUser currentUser,
        FinancialCategoryService categories, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await categories.UpdateAsync(workspaceId, categoryId, userId, request?.Name, request?.IsActive, cancellationToken);
        return ToResponse(result, Results.Ok);
    }

    private static IResult ToResponse(CategoryResult result, Func<FinancialCategoryDto, IResult> ok) => result.Access switch
    {
        CategoryAccess.NotFound => Results.NotFound(),
        CategoryAccess.Invalid => Results.ValidationProblem(result.Errors!),
        CategoryAccess.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: CategoryResult.DuplicateMessage),
        _ => ok(result.Category!),
    };
}

/// <summary>
/// Cadastro de categoria: nome e tipo (<c>Income</c> ou <c>Expense</c>) para uma categoria principal; para uma subcategoria, nome e
/// <c>parentCategoryId</c> (o tipo é o da principal e, se enviado, precisa coincidir). Orçamento vem da rota e criador da sessão;
/// campos como workspaceId, createdByUserId, isActive ou createdAt são recusados (400).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCategoryRequest(string? Name, string? Type, Guid? ParentCategoryId = null);

/// <summary>Edição: nome e situação. O tipo é imutável: enviá-lo (ou qualquer campo do servidor) dá 400.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateCategoryRequest(string? Name, bool? IsActive);

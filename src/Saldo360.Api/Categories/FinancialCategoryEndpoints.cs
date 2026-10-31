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
        categories.MapGet("/{categoryId:guid}/impact", Impact);
        categories.MapDelete("/{categoryId:guid}", Delete).AddEndpointFilter<ValidateCsrfTokenFilter>();
        categories.MapGet("/defaults", PreviewDefaults);
        categories.MapPost("/defaults", ApplyDefaults).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    public const string InUseCode = "CATEGORY_IN_USE";
    public const string ConfirmationRequiredCode = "CATEGORY_DELETE_CONFIRMATION_REQUIRED";
    public const string ActiveDependenciesCode = "CATEGORY_HAS_ACTIVE_DEPENDENCIES";

    /// <summary>Efeito de excluir ou inativar: registros vinculados por origem e configurações ativas dependentes.</summary>
    private static async Task<IResult> Impact(
        Guid workspaceId, Guid categoryId, CurrentUser currentUser, FinancialCategoryService categories, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await categories.GetImpactAsync(workspaceId, categoryId, userId, cancellationToken) is { } impact
            ? Results.Ok(impact)
            : Results.NotFound();
    }

    /// <summary>
    /// Exclui a categoria e as suas subcategorias, só se nada as referenciar. Com subcategorias, exige <c>confirmSubcategories=true</c>.
    /// 409 com o impacto quando há vínculos (nada é apagado; a alternativa é inativar) ou falta a confirmação.
    /// </summary>
    private static async Task<IResult> Delete(
        Guid workspaceId, Guid categoryId, bool? confirmSubcategories, CurrentUser currentUser, FinancialCategoryService categories,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await categories.DeleteAsync(workspaceId, categoryId, userId, confirmSubcategories ?? false, cancellationToken);
        return result.Outcome switch
        {
            CategoryDeleteOutcome.Deleted => Results.NoContent(),
            CategoryDeleteOutcome.InUse => Results.Json(
                new CategoryProblem(InUseCode, "A categoria está em uso e não pode ser excluída. Inative-a para preservar o histórico.", result.Impact),
                statusCode: StatusCodes.Status409Conflict),
            CategoryDeleteOutcome.ConfirmationRequired => Results.Json(
                new CategoryProblem(ConfirmationRequiredCode, "Confirme a exclusão da categoria e das suas subcategorias.", result.Impact),
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.NotFound(),
        };
    }

    /// <summary>O que "Adicionar categorias padrão" criaria neste orçamento (não altera nada).</summary>
    private static async Task<IResult> PreviewDefaults(
        Guid workspaceId, CurrentUser currentUser, FinancialCategoryService categories, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await categories.PreviewDefaultsAsync(workspaceId, userId, cancellationToken) is { } preview
            ? Results.Ok(preview)
            : Results.NotFound();
    }

    /// <summary>Acrescenta ao orçamento as categorias padrão que faltam. Idempotente; não altera o que já existe.</summary>
    private static async Task<IResult> ApplyDefaults(
        Guid workspaceId, CurrentUser currentUser, FinancialCategoryService categories, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await categories.ApplyDefaultsAsync(workspaceId, userId, cancellationToken) is { } applied
            ? Results.Ok(applied)
            : Results.NotFound();
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
        CategoryAccess.Blocked => Results.Json(
            new CategoryProblem(
                ActiveDependenciesCode,
                "Há configurações ativas que usam esta categoria. Resolva-as (por exemplo, trocando a categoria da recorrência ou encerrando-a) antes de inativá-la.",
                result.Impact),
            statusCode: StatusCodes.Status409Conflict),
        _ => ok(result.Category!),
    };
}

/// <summary>Corpo dos 409 de exclusão e inativação: <c>code</c> estável, mensagem em <c>title</c> e o impacto que justifica o impedimento.</summary>
public sealed record CategoryProblem(string Code, string Title, CategoryImpactDto? Impact);

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

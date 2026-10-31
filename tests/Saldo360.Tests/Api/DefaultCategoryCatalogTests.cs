using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Catálogo padrão de categorias sobre PostgreSQL real (e o catálogo real, não o vazio dos demais testes): inicialização na criação do orçamento,
/// idempotência e concorrência, isolamento entre orçamentos, não recriação do que o usuário apagou ou renomeou e a ação explícita para
/// orçamentos existentes.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Categories")]
[Trait("Dependency", "PostgreSQL")]
public sealed class DefaultCategoryCatalogTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private const int CatalogRows = 14 + 35;

    protected override async Task BeforeApiStartsAsync()
    {
        Factory.UseStandardCategoryCatalog = true;
        await base.BeforeApiStartsAsync();
    }

    private static string CategoriesUrl(WorkspaceDto workspace) => $"/api/financial-workspaces/{workspace.Id}/categories";

    [Fact]
    public async Task NewBudget_ReceivesTenExpenseAndFourIncomeCategories_WithSubcategories_AndRecordsTheInitialization()
    {
        var owner = await SignInAsync("catalogo-dona");
        var workspace = await CreateWorkspaceAsync(owner);

        await using var db = Db();
        var rows = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspace.Id).ToListAsync();
        Assert.Equal(CatalogRows, rows.Count);
        Assert.All(rows, c => Assert.True(c.IsActive));
        var mains = rows.Where(c => !c.IsSubcategory).ToList();
        Assert.Equal((10, 4), (mains.Count(c => c.Type == CategoryType.Expense), mains.Count(c => c.Type == CategoryType.Income)));
        Assert.Equal((28, 7), (rows.Count(c => c.IsSubcategory && c.Type == CategoryType.Expense), rows.Count(c => c.IsSubcategory && c.Type == CategoryType.Income)));
        var taxes = mains.Single(c => c.Name == "Impostos e seguros");
        Assert.Equal(["Impostos PF", "Impostos PJ", "Seguros"], rows.Where(c => c.ParentCategoryId == taxes.Id).Select(c => c.Name).Order());
        var ownerId = await db.Users.Where(u => u.Subject == "catalogo-dona").Select(u => u.Id).SingleAsync();
        Assert.All(rows, c => Assert.Equal(ownerId, c.CreatedByUserId));

        var stored = await db.FinancialWorkspaces.AsNoTracking().SingleAsync(w => w.Id == workspace.Id);
        Assert.NotNull(stored.DefaultCategoriesInitializedAt);
        Assert.Equal(1, stored.DefaultCategoriesCatalogVersion);

        var listed = await ReadAsync<List<CategoryDto>>(await SendAsync(HttpMethod.Get, CategoriesUrl(workspace), owner));
        Assert.Equal(CatalogRows, listed.Count);
        Assert.DoesNotContain(listed, c => c.Name == "Sem categoria");
    }

    [Fact]
    public async Task RepeatedAndConcurrentInitialization_NeverDuplicates()
    {
        var owner = await SignInAsync("catalogo-concorrente");

        var created = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CreateWorkspaceAsync(owner)));
        await CreateWorkspaceAsync(owner);

        Assert.Single(created.Select(w => w.Id).Distinct());
        await using var db = Db();
        Assert.Equal(1, await db.FinancialWorkspaces.CountAsync());
        Assert.Equal(CatalogRows, await db.FinancialCategories.CountAsync());
    }

    [Fact]
    public async Task Budgets_AreIsolated_EditsAndDeletionsInOneNeverReachTheOther()
    {
        var ownerA = await SignInAsync("catalogo-a");
        var ownerB = await SignInAsync("catalogo-b");
        var budgetA = await CreateWorkspaceAsync(ownerA);
        var budgetB = await CreateWorkspaceAsync(ownerB);
        var listA = await ListAsync(ownerA, budgetA);
        var listB = await ListAsync(ownerB, budgetB);
        Assert.Empty(listA.Select(c => c.Id).Intersect(listB.Select(c => c.Id)));

        var food = listA.Single(c => c.Name == "Alimentação");
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl(budgetA)}/{food.Id}", ownerA, new { name = "Comida", isActive = true })).StatusCode);
        var education = listA.Single(c => c.Name == "Educação");
        Assert.Equal(HttpStatusCode.NoContent, (await SendWithCsrfAsync(HttpMethod.Delete, $"{CategoriesUrl(budgetA)}/{education.Id}?confirmSubcategories=true", ownerA)).StatusCode);

        var afterB = await ListAsync(ownerB, budgetB);
        Assert.Equal(listB.Select(c => c.Name).Order(), afterB.Select(c => c.Name).Order());
        Assert.Contains(afterB, c => c.Name == "Alimentação");
        Assert.Contains(afterB, c => c.Name == "Educação");
        // Um orçamento nunca enxerga nem altera categoria do outro.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{CategoriesUrl(budgetB)}/{food.Id}", ownerB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl(budgetA)}/{afterB[0].Id}", ownerA, new { name = "Invadida", isActive = true })).StatusCode);
    }

    [Fact]
    public async Task RenamedAndDeletedDefaults_AreNotRestored_ByAccessOrNewInitializationAttempts()
    {
        var owner = await SignInAsync("catalogo-personalizada");
        var workspace = await CreateWorkspaceAsync(owner);
        var all = await ListAsync(owner, workspace);
        var housing = all.Single(c => c.Name == "Moradia");
        var education = all.Single(c => c.Name == "Educação");
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl(workspace)}/{housing.Id}", owner, new { name = "Casa", isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendWithCsrfAsync(HttpMethod.Delete, $"{CategoriesUrl(workspace)}/{education.Id}?confirmSubcategories=true", owner)).StatusCode);

        await CreateWorkspaceAsync(owner);
        await CreateWorkspaceAsync(owner);
        var after = await ListAsync(owner, workspace);

        Assert.Equal(CatalogRows - 3, after.Count);
        Assert.Contains(after, c => c.Name == "Casa");
        Assert.DoesNotContain(after, c => c.Name is "Educação" or "Escola e materiais" or "Cursos e formação");
        Assert.Single(after, c => c.Id == housing.Id);
        Assert.DoesNotContain(after.Where(c => c.ParentCategoryId is null), c => c.Name == "Moradia");
    }

    [Fact]
    public async Task EmptyBudgetWithoutTheMarker_IsNotInitializedAutomatically_NorOnceTheUserDeletedEverything()
    {
        var owner = await SignInAsync("catalogo-legado");
        var workspace = await CreateWorkspaceAsync(owner);
        await MakeLegacyEmptyBudgetAsync(workspace);

        await CreateWorkspaceAsync(owner);
        Assert.Empty(await ListAsync(owner, workspace));
        await using (var db = Db())
        {
            Assert.Null((await db.FinancialWorkspaces.AsNoTracking().SingleAsync()).DefaultCategoriesInitializedAt);
        }

        // E um orçamento já inicializado cujo usuário apagou tudo também continua vazio.
        var other = await SignInAsync("catalogo-apagou-tudo");
        var initialized = await CreateWorkspaceAsync(other);
        foreach (var main in (await ListAsync(other, initialized)).Where(c => c.ParentCategoryId is null))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await SendWithCsrfAsync(HttpMethod.Delete, $"{CategoriesUrl(initialized)}/{main.Id}?confirmSubcategories=true", other)).StatusCode);
        }

        await CreateWorkspaceAsync(other);
        Assert.Empty(await ListAsync(other, initialized));
        await using var verify = Db();
        Assert.NotNull((await verify.FinancialWorkspaces.AsNoTracking().SingleAsync(w => w.Id == initialized.Id)).DefaultCategoriesInitializedAt);
    }

    [Fact]
    public async Task ExplicitAction_PreviewsWithoutChanging_ReusesEquivalents_AndNeverRenamesReactivatesOrDuplicates()
    {
        var owner = await SignInAsync("catalogo-existente");
        var workspace = await CreateWorkspaceAsync(owner);
        await MakeLegacyEmptyBudgetAsync(workspace);
        var url = CategoriesUrl(workspace);
        var food = await CreateAsync(owner, workspace, "  ALIMENTAÇÃO ", "Expense");
        await CreateAsync(owner, workspace, "mercado   e FEIRA", "Expense", food.Id);
        var housing = await CreateAsync(owner, workspace, "Moradia", "Expense");
        await CreateAsync(owner, workspace, "Pets", "Expense");
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{url}/{housing.Id}", owner, new { name = "Moradia", isActive = false })).StatusCode);

        var preview = await ReadAsync<PreviewDto>(await SendAsync(HttpMethod.Get, $"{url}/defaults", owner));

        Assert.Equal((12, 30), (preview.CategoriesToCreate, preview.SubcategoriesToCreate));
        Assert.Equal("Exists", preview.Categories.Single(c => c.Name == "Alimentação").Action);
        Assert.Equal("ExistsInactive", preview.Categories.Single(c => c.Name == "Moradia").Action);
        Assert.All(preview.Categories.Single(c => c.Name == "Moradia").Subcategories!, s => Assert.Equal("SkippedParentInactive", s.Action));
        await using (var db = Db())
        {
            Assert.Equal(4, await db.FinancialCategories.CountAsync()); // a pré-visualização não grava nada
        }

        var applied = await ReadAsync<ApplyDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{url}/defaults", owner));
        Assert.Equal((12, 30), (applied.CategoriesCreated, applied.SubcategoriesCreated));
        var again = await ReadAsync<ApplyDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{url}/defaults", owner));
        Assert.Equal((0, 0), (again.CategoriesCreated, again.SubcategoriesCreated));

        var rows = await ListAsync(owner, workspace, includeInactive: true);
        Assert.Equal(4 + 12 + 30, rows.Count);
        Assert.Equal("ALIMENTAÇÃO", rows.Single(c => c.Id == food.Id).Name); // nada é renomeado: o nome do usuário vale
        Assert.False(rows.Single(c => c.Id == housing.Id).IsActive); // nada é reativado
        Assert.DoesNotContain(rows, c => c.ParentCategoryId == housing.Id); // nem recebe subcategorias já indisponíveis
        Assert.Contains(rows, c => c.Name == "Pets");
        Assert.Contains(rows, c => c.Name == "Refeições fora e delivery" && c.ParentCategoryId == food.Id);
        Assert.Equal(rows.Count, rows.Select(c => (c.ParentCategoryId, c.Type, c.Name.ToUpperInvariant())).Distinct().Count());
    }

    [Fact]
    public async Task IntegratedJourney_RealCatalog_UseDeleteReopenAndApplyAgain()
    {
        var owner = await SignInAsync("catalogo-jornada");
        var workspace = await CreateWorkspaceAsync(owner);
        var url = CategoriesUrl(workspace);
        var all = await ListAsync(owner, workspace);
        Assert.Equal(CatalogRows, all.Count);
        var market = all.Single(c => c.Name == "Mercado e feira");
        Assert.Equal(all.Single(c => c.Name == "Alimentação").Id, market.ParentCategoryId);

        // Usa uma subcategoria do catálogo num lançamento; ela passa a ter vínculo e não pode ser excluída.
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace.Id}/accounts", owner, new { name = "Conta", type = "Checking", financialScope = "PF" });
        var accountId = JsonDocument.Parse(await account.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace.Id}/transactions", owner,
            new { accountId, categoryId = market.Id, type = "Expense", description = "Feira", amount = "30.00", transactionDate = "2026-10-06" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendWithCsrfAsync(HttpMethod.Delete, $"{url}/{market.Id}", owner)).StatusCode);

        // Exclui uma sem vínculos, reabre o orçamento: não reaparece.
        var seguros = all.Single(c => c.Name == "Seguros");
        Assert.Equal(HttpStatusCode.NoContent, (await SendWithCsrfAsync(HttpMethod.Delete, $"{url}/{seguros.Id}", owner)).StatusCode);
        await CreateWorkspaceAsync(owner);
        Assert.DoesNotContain(await ListAsync(owner, workspace), c => c.Name == "Seguros");

        // A aplicação explícita devolve só o que falta (a categoria excluída volta por ação do usuário), registra a inicialização e é idempotente.
        var first = await ReadAsync<ApplyDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{url}/defaults", owner));
        var second = await ReadAsync<ApplyDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{url}/defaults", owner));
        Assert.Equal((0, 1, 0, 0), (first.CategoriesCreated, first.SubcategoriesCreated, second.CategoriesCreated, second.SubcategoriesCreated));
        Assert.Equal(CatalogRows, (await ListAsync(owner, workspace)).Count);
    }

    [Fact]
    public async Task ExplicitAction_RecordsTheInitializationOnce_InTheSameTransaction()
    {
        var owner = await SignInAsync("catalogo-registro");
        var workspace = await CreateWorkspaceAsync(owner);
        await MakeLegacyEmptyBudgetAsync(workspace);

        await SendWithCsrfAsync(HttpMethod.Post, $"{CategoriesUrl(workspace)}/defaults", owner);
        DateTimeOffset? firstStamp;
        await using (var db = Db())
        {
            var stored = await db.FinancialWorkspaces.AsNoTracking().SingleAsync();
            firstStamp = stored.DefaultCategoriesInitializedAt;
            Assert.NotNull(firstStamp);
            Assert.Equal(1, stored.DefaultCategoriesCatalogVersion);
        }

        await SendWithCsrfAsync(HttpMethod.Post, $"{CategoriesUrl(workspace)}/defaults", owner);
        await using var verify = Db();
        Assert.Equal(firstStamp, (await verify.FinancialWorkspaces.AsNoTracking().SingleAsync()).DefaultCategoriesInitializedAt);
        Assert.Equal(CatalogRows, await verify.FinancialCategories.CountAsync());
    }

    [Fact]
    public async Task ExplicitAction_ConcurrentApplications_DoNotDuplicate()
    {
        var owner = await SignInAsync("catalogo-aplicacao-concorrente");
        var workspace = await CreateWorkspaceAsync(owner);
        await MakeLegacyEmptyBudgetAsync(workspace);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
            await ReadAsync<ApplyDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{CategoriesUrl(workspace)}/defaults", owner))));

        Assert.Equal((14, 35), (results.Sum(r => r.CategoriesCreated), results.Sum(r => r.SubcategoriesCreated)));
        await using var db = Db();
        Assert.Equal(CatalogRows, await db.FinancialCategories.CountAsync());
    }

    [Fact]
    public async Task ExplicitAction_IsPerBudget_RequiresCsrf_AndHidesOtherBudgets()
    {
        var owner = await SignInAsync("catalogo-acao-a");
        var stranger = await SignInAsync("catalogo-acao-b");
        var workspace = await CreateWorkspaceAsync(owner);
        var other = await CreateWorkspaceAsync(stranger);
        await MakeLegacyEmptyBudgetAsync(workspace);
        await MakeLegacyEmptyBudgetAsync(other);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{CategoriesUrl(workspace)}/defaults", stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"{CategoriesUrl(workspace)}/defaults", stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendJsonAsync(HttpMethod.Post, $"{CategoriesUrl(workspace)}/defaults", owner, csrf: null, body: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"{CategoriesUrl(workspace)}/defaults")).StatusCode);
        await using (var db = Db())
        {
            Assert.Equal(0, await db.FinancialCategories.CountAsync());
        }

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Post, $"{CategoriesUrl(workspace)}/defaults", owner)).StatusCode);
        await using var verify = Db();
        Assert.Equal(CatalogRows, await verify.FinancialCategories.CountAsync(c => c.WorkspaceId == workspace.Id));
        Assert.Equal(0, await verify.FinancialCategories.CountAsync(c => c.WorkspaceId == other.Id));
    }

    // Reproduz um orçamento anterior ao catálogo: sem categorias e sem o registro de inicialização.
    private async Task MakeLegacyEmptyBudgetAsync(WorkspaceDto workspace)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM financial_categories WHERE workspace_id = {workspace.Id} AND parent_category_id IS NOT NULL");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM financial_categories WHERE workspace_id = {workspace.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE financial_workspaces SET default_categories_initialized_at = NULL, default_categories_catalog_version = NULL WHERE id = {workspace.Id}");
    }

    private async Task<CategoryDto> CreateAsync(string session, WorkspaceDto workspace, string name, string type, Guid? parent = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl(workspace), session,
            parent is null ? new { name, type } : new { name, parentCategoryId = parent });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<CategoryDto>(response);
    }

    private async Task<List<CategoryDto>> ListAsync(string session, WorkspaceDto workspace, bool includeInactive = false)
    {
        var response = await SendAsync(HttpMethod.Get, $"{CategoriesUrl(workspace)}{(includeInactive ? "?includeInactive=true" : "")}", session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<List<CategoryDto>>(response);
    }

    private sealed record CategoryDto(Guid Id, string Name, string Type, bool IsActive, Guid? ParentCategoryId);

    private sealed record PlanItemDto(string Name, string Action, List<PlanItemDto>? Subcategories);

    private sealed record PreviewDto(int CatalogVersion, int CategoriesToCreate, int SubcategoriesToCreate, List<PlanItemDto> Categories);

    private sealed record ApplyDto(int CategoriesCreated, int SubcategoriesCreated, PreviewDto Applied);
}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Reset HARD do orçamento: apaga todos os dados do orçamento do dono (inclusive evidências bancárias importadas) e recria do zero o catálogo padrão
/// (49 categorias) na mesma transação, com o registro de inicialização refeito. Preserva o orçamento, o dono e tudo de outros orçamentos.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Workspaces")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialResetRecreatesCatalogTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private const string Confirmation = "APAGAR ORÇAMENTO";

    protected override async Task BeforeApiStartsAsync()
    {
        Factory.UseStandardCategoryCatalog = true;
        await base.BeforeApiStartsAsync();
    }

    private static string Path(Guid w) => $"/api/financial-workspaces/{w}/financial-reset";

    private Task<HttpResponseMessage> ResetAsync(Guid w, string session) =>
        SendWithCsrfAsync(HttpMethod.Post, Path(w), session, new { confirmation = Confirmation });

    private async Task<string> FingerprintAsync(Guid w)
    {
        await using var db = Db();
        return await FinancialResetFixture.FingerprintAsync(db, w);
    }

    private async Task AssertExactlyTheCatalogAsync(Guid w)
    {
        await using var db = Db();
        var rows = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == w).ToListAsync();
        Assert.Equal(49, rows.Count);
        Assert.All(rows, c => Assert.True(c.IsActive));
        var mains = rows.Where(c => !c.IsSubcategory).ToList();
        Assert.Equal((10, 4), (mains.Count(c => c.Type == CategoryType.Expense), mains.Count(c => c.Type == CategoryType.Income)));
        Assert.Equal((28, 7), (rows.Count(c => c.IsSubcategory && c.Type == CategoryType.Expense), rows.Count(c => c.IsSubcategory && c.Type == CategoryType.Income)));
        Assert.Equal(["Impostos PF", "Impostos PJ", "Seguros"], rows.Where(c => c.ParentCategoryId == mains.Single(m => m.Name == "Impostos e seguros").Id).Select(c => c.Name).Order());
        var workspace = await db.FinancialWorkspaces.AsNoTracking().SingleAsync(x => x.Id == w);
        Assert.NotNull(workspace.DefaultCategoriesInitializedAt);
        Assert.Equal(1, workspace.DefaultCategoriesCatalogVersion);
        Assert.NotNull(workspace.LastFinancialResetAt);
    }

    [Fact]
    public async Task Reset_ErasesEverything_RecreatesExactlyTheCatalog_AndLeavesOtherBudgetsAndTheOwnerIntact()
    {
        var a = await SignInAsync("hard-a");
        var b = await SignInAsync("hard-b");
        var wa = await CreateWorkspaceAsync(a);
        var wb = await CreateWorkspaceAsync(b);
        await using (var seed = Db())
        {
            await FinancialResetFixture.SeedAsync(seed, wa.Id, wa.OwnerUserId);
            await FinancialResetFixture.SeedAsync(seed, wb.Id, wb.OwnerUserId);
        }

        // Personalizações antes do reset: renomeada, excluída e inativa. Nenhuma sobrevive.
        var list = await ReadAsync<List<CategoryDto>>(await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{wa.Id}/categories?includeInactive=true", a));
        var housing = list.Single(c => c.Name == "Moradia");
        var leisure = list.Single(c => c.Name == "Lazer e compras pessoais");
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{wa.Id}/categories/{housing.Id}", a, new { name = "Casa", isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{wa.Id}/categories/{leisure.Id}", a, new { name = "Lazer e compras pessoais", isActive = false })).StatusCode);
        var beforeB = await FingerprintAsync(wb.Id);

        var response = await ResetAsync(wa.Id, a);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync<JsonElement>(response);
        Assert.Equal("ResetCompleted", body.GetProperty("status").GetString());
        Assert.Equal(49, body.GetProperty("categoriesRecreated").GetInt32());
        await AssertExactlyTheCatalogAsync(wa.Id);
        await using (var db = Db())
        {
            Assert.DoesNotContain(await db.FinancialCategories.Where(c => c.WorkspaceId == wa.Id).Select(c => c.Name).ToListAsync(), n => n == "Casa" || n == "Category");
            var tables = await db.Database.SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.columns WHERE column_name = 'workspace_id' AND table_schema = 'public' AND table_name NOT IN ('financial_workspace_reset_audit', 'financial_workspace_reset_file_cleanup', 'financial_categories')").ToListAsync();
            foreach (var table in tables)
                {
                var sql = "SELECT count(*)::int AS \"Value\" FROM \"" + table + "\" WHERE workspace_id = {0}"; // nome de tabela vem do catálogo do banco
                Assert.Equal(0, await db.Database.SqlQueryRaw<int>(sql, wa.Id).SingleAsync());
            }
            Assert.True(await db.FinancialWorkspaces.AnyAsync(w => w.Id == wa.Id && w.OwnerUserId == wa.OwnerUserId)); // orçamento e dono preservados
            Assert.True(await db.Users.AnyAsync(u => u.Id == wa.OwnerUserId));
        }

        Assert.Equal(beforeB, await FingerprintAsync(wb.Id)); // outro orçamento intacto
        Assert.Equal(HttpStatusCode.NotFound, (await ResetAsync(wa.Id, b)).StatusCode); // quem não é dono não reseta
    }

    [Fact]
    public async Task EveryTableWithWorkspaceData_IsInTheResetInventory()
    {
        var a = await SignInAsync("hard-inventario");
        var w = await CreateWorkspaceAsync(a);
        var preview = await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, Path(w.Id) + "/preview", a));
        await using var db = Db();
        var tables = await db.Database.SqlQuery<string>($"SELECT DISTINCT table_name AS \"Value\" FROM information_schema.columns WHERE column_name = 'workspace_id' AND table_schema = 'public' AND table_name NOT IN ('financial_workspace_reset_audit', 'financial_workspace_reset_file_cleanup')").ToListAsync();
        Assert.Equal(tables.Count, preview.GetProperty("counts").EnumerateObject().Count());
        Assert.Contains("financial_bank_refunds", tables);
        Assert.Contains("pluggy_transfer_rejections", tables);
        Assert.Contains("overdraft_recovery_snapshots", tables);
    }

    [Fact]
    public async Task RepeatedReset_ReachesTheSameFinalState_AndAddingDefaultsAfterwardsDoesNotDuplicate()
    {
        var a = await SignInAsync("hard-repetido");
        var w = await CreateWorkspaceAsync(a);

        Assert.Equal(HttpStatusCode.OK, (await ResetAsync(w.Id, a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ResetAsync(w.Id, a)).StatusCode);

        await AssertExactlyTheCatalogAsync(w.Id);
        var applied = await ReadAsync<JsonElement>(await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/categories/defaults", a));
        Assert.Equal((0, 0), (applied.GetProperty("categoriesCreated").GetInt32(), applied.GetProperty("subcategoriesCreated").GetInt32()));
        await AssertExactlyTheCatalogAsync(w.Id);
    }

    [Fact]
    public async Task ConcurrentResets_SerializeSafely_AndLeaveASingleCatalog()
    {
        var a = await SignInAsync("hard-concorrente");
        var w = await CreateWorkspaceAsync(a);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ResetAsync(w.Id, a)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        await AssertExactlyTheCatalogAsync(w.Id);
    }

    private sealed record CategoryDto(Guid Id, string Name, string Type, bool IsActive, Guid? ParentCategoryId);
}

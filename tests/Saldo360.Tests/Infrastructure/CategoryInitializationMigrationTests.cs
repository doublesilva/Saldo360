using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Domain.Debts;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Infrastructure;

/// <summary>
/// Sequência de migrations das categorias padrão em banco descartável, com dados preexistentes: volta ao estado da migration anterior (AddBankRefunds),
/// cria um dado legado, inclusive uma parcela de dívida com categoria órfã (a coluna nunca teve FK), e reaplica. Nada é alterado nem apagado; a FK nova só
/// vale para gravações futuras e os orçamentos antigos ficam sem registro de inicialização.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "INFRA")]
[Trait("Area", "Infrastructure")]
[Trait("Area", "Categories")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CategoryInitializationMigrationTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private const string Previous = "20261031125016_AddBankRefunds";

    [Fact]
    public async Task ReapplyingOverLegacyData_KeepsEveryRow_EvenAnOrphanDebtCategory_AndOnlyAddsTheProtections()
    {
        var session = await SignInAsync("migracao-categorias");
        var workspace = await CreateWorkspaceAsync(session);
        var category = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace.Id}/categories", session, new { name = "Financiamento", type = "Expense" });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var debt = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace.Id}/debts", session,
            new { name = "Dívida", debtType = "Other", originalPrincipal = "1000.00", contractDate = "2026-01-01" });
        var debtId = System.Text.Json.JsonDocument.Parse(await debt.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var orphan = Guid.CreateVersion7();
        await using (var seed = Db())
        {
            seed.FinancialDebtInstallments.Add(FinancialDebtInstallment.Create(workspace.Id, debtId,
                new(1, new DateOnly(2026, 11, 10), 1000m, null, null, null, null, null), DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }

        using (var pool = new NpgsqlConnection(ConnectionString)) NpgsqlConnection.ClearPool(pool);
        await using (var down = Db())
        {
            await down.GetService<IMigrator>().MigrateAsync(Previous);
            Assert.DoesNotContain("20261031133330_AddDefaultCategoryInitialization", await down.Database.GetAppliedMigrationsAsync());
            // Estado legado: sem as colunas novas e sem FK, uma parcela aponta para uma categoria que não existe.
            await down.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_debt_installments SET category_id = {orphan}");
        }

        using (var pool = new NpgsqlConnection(ConnectionString)) NpgsqlConnection.ClearPool(pool);
        await using (var up = Db())
        {
            await up.Database.MigrateAsync();
            Assert.Empty(await up.Database.GetPendingMigrationsAsync());
        }

        using (var pool = new NpgsqlConnection(ConnectionString)) NpgsqlConnection.ClearPool(pool);
        await using var db = Db();
        Assert.Equal(1, await db.FinancialWorkspaces.CountAsync());
        Assert.Equal(1, await db.FinancialCategories.CountAsync());
        Assert.Equal(orphan, (await db.FinancialDebtInstallments.AsNoTracking().SingleAsync()).CategoryId); // dado legado intacto
        var stored = await db.FinancialWorkspaces.AsNoTracking().SingleAsync();
        Assert.Null(stored.DefaultCategoriesInitializedAt); // orçamento anterior: sem inicialização automática
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_constraint WHERE conname = 'fk_debt_installments_categories_same_workspace' AND NOT convalidated").SingleAsync());

        // A proteção vale daqui em diante: nova parcela com categoria inexistente é recusada.
        db.FinancialDebtInstallments.Add(FinancialDebtInstallment.Create(workspace.Id, debtId,
            new(2, new DateOnly(2026, 12, 10), 500m, null, null, null, null, Guid.CreateVersion7()), DateTimeOffset.UtcNow));
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)failure.InnerException!).SqlState);
    }
}

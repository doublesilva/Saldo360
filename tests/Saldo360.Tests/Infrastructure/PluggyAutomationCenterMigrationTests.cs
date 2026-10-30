using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Domain.Pluggy;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Infrastructure;

/// <summary>
/// R05: a migração da central de automação (R04) em banco descartável. O banco do teste já recebeu TODA a sequência de migrações (a aplicação inicia e os fluxos
/// dos demais testes funcionam sobre ela); aqui se exercita o caminho de reversão suportado — <c>Down</c> remove só as duas tabelas novas, <c>Up</c> as recria —
/// e se conferem as restrições que protegem a preferência de IA (consentimento) e a execução única.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "INFRA")]
[Trait("Area", "Infrastructure")]
[Trait("Area", "Pluggy")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PluggyAutomationCenterMigrationTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private const string Settings = "pluggy_automation_settings";
    private const string Runs = "pluggy_categorization_runs";

    private async Task<bool> ExistsAsync(DbContext db, string table) =>
        await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = {table}").SingleAsync() == 1;

    [Fact]
    public async Task DownRemovesOnlyTheTwoAutomationTablesAndUpRecreatesThemWithTheirConstraints()
    {
        await using (var current = Db())
        {
            Assert.True(await ExistsAsync(current, Settings));
            Assert.True(await ExistsAsync(current, Runs));
            Assert.Contains("20261030184418_AddPluggyAutomationCenter", await current.Database.GetAppliedMigrationsAsync());
        }

        using (var pool = new NpgsqlConnection(ConnectionString)) NpgsqlConnection.ClearPool(pool);
        await using (var context = Db())
        {
            var assembly = context.GetService<IMigrationsAssembly>();
            var type = assembly.Migrations.Single(x => x.Key.EndsWith("AddPluggyAutomationCenter", StringComparison.Ordinal)).Value;
            var migration = assembly.CreateMigration(type, context.Database.ProviderName!);
            var generator = context.GetService<IMigrationsSqlGenerator>();
            await using var tx = await context.Database.BeginTransactionAsync();

            foreach (var command in generator.Generate(migration.DownOperations)) await context.Database.ExecuteSqlRawAsync(command.CommandText);
            Assert.False(await ExistsAsync(context, Settings));
            Assert.False(await ExistsAsync(context, Runs));
            Assert.True(await ExistsAsync(context, "pluggy_sync_runs")); // a reversão não toca o que já existia antes
            Assert.True(await ExistsAsync(context, "pluggy_sync_states"));

            foreach (var command in generator.Generate(migration.UpOperations)) await context.Database.ExecuteSqlRawAsync(command.CommandText);
            Assert.True(await ExistsAsync(context, Settings));
            Assert.True(await ExistsAsync(context, Runs));
            await tx.CommitAsync();
        }
        using (var pool = new NpgsqlConnection(ConnectionString)) NpgsqlConnection.ClearPool(pool);

        // Depois de reaplicar, as restrições valem: consentimento ligado à preferência de IA, intervalo válido e uma só categorização em andamento por orçamento.
        var session = await SignInAsync("r05-migration");
        var workspace = await CreateWorkspaceAsync(session);
        await using var db = Db();
        var user = await db.FinancialWorkspaces.AsNoTracking().Where(w => w.Id == workspace.Id).Select(w => w.OwnerUserId).SingleAsync();
        var now = DateTimeOffset.UtcNow;

        var settings = PluggyAutomationSettings.Create(workspace.Id, user, now);
        db.PluggyAutomationSettings.Add(settings);
        await db.SaveChangesAsync();
        var consent = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pluggy_automation_settings SET categorize_after_sync = TRUE, categorize_consent_at = NULL WHERE workspace_id = {workspace.Id}"));
        var interval = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pluggy_automation_settings SET sync_interval_hours = 7 WHERE workspace_id = {workspace.Id}"));
        Assert.Equal(("23514", "23514"), (consent.SqlState, interval.SqlState));

        db.PluggyCategorizationRuns.Add(PluggyCategorizationRun.Start(workspace.Id, user, PluggyCategorizationTrigger.Manual, null, null, false, now));
        await db.SaveChangesAsync();
        await using var second = Db();
        second.PluggyCategorizationRuns.Add(PluggyCategorizationRun.Start(workspace.Id, user, PluggyCategorizationTrigger.AfterSync, null, null, true, now));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.Equal("23505", ((PostgresException)duplicate.InnerException!).SqlState); // uma só categorização em andamento por orçamento
    }
}

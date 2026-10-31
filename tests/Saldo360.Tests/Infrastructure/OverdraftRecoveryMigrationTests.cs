using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Infrastructure;

/// <summary>
/// Migration do cheque especial em banco descartável com dados preexistentes: volta ao estado anterior, confere que as contas existentes sobrevivem
/// e reaplica a sequência completa. O limite das contas existentes fica nulo (desconhecido, nunca zero) e nada é apagado.
/// Down: remove a tabela de metas e a coluna do limite, portanto DESTRÓI os limites informados e as metas registradas (não há como recuperá-los depois);
/// serve só para reverter um deploy recém-feito.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "INFRA")]
[Trait("Area", "Infrastructure")]
[Trait("Area", "Accounts")]
[Trait("Dependency", "PostgreSQL")]
public sealed class OverdraftRecoveryMigrationTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private const string Previous = "20261031140454_AddDebtInstallmentCategoryForeignKey";

    [Fact]
    public async Task PreviousSchemaWithExistingAccounts_ReappliesTheFullSequence_KeepingAccountsAndLeavingTheLimitNull()
    {
        var session = await SignInAsync("migracao-cheque");
        var workspace = await CreateWorkspaceAsync(session);
        var created = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace.Id}/accounts", session,
            new { name = "Itaú PF", type = "Checking", financialScope = "PF", openingBalance = "-100.00", openingBalanceDate = "2026-09-01", overdraftLimit = "500.00" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using (var pool = new NpgsqlConnection(ConnectionString)) NpgsqlConnection.ClearPool(pool);
        await using (var down = Db())
        {
            await down.GetService<IMigrator>().MigrateAsync(Previous);
            Assert.DoesNotContain("20261031165952_AddOverdraftRecovery", await down.Database.GetAppliedMigrationsAsync());
            Assert.Equal(0, await down.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'financial_accounts' AND column_name = 'overdraft_limit'").SingleAsync());
            Assert.Equal(1, await down.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM financial_accounts").SingleAsync()); // a conta existente sobrevive ao Down
        }

        using (var pool = new NpgsqlConnection(ConnectionString)) NpgsqlConnection.ClearPool(pool);
        await using (var up = Db())
        {
            await up.Database.MigrateAsync();
            Assert.Empty(await up.Database.GetPendingMigrationsAsync());
        }

        using (var pool = new NpgsqlConnection(ConnectionString)) NpgsqlConnection.ClearPool(pool);
        await using var db = Db();
        var account = await db.FinancialAccounts.AsNoTracking().SingleAsync();
        Assert.Equal(("Itaú PF", -100m), (account.Name, account.OpeningBalance));
        Assert.Null(account.OverdraftLimit); // o limite informado antes do Down se perdeu; o desconhecido volta como nulo, nunca como zero
        Assert.Equal(0, await db.OverdraftRecoverySnapshots.CountAsync());
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_overdraft_recovery_snapshots_workspace_id_account_id_compet'").SingleAsync());
    }
}

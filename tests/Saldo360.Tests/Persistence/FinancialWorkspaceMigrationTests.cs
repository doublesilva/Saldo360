using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Domain.Families;
using Saldo360.Domain.ServiceAccounts;
using Saldo360.Infrastructure.Families;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class FinancialWorkspaceMigrationTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulatedCutover_RequiresExplicitPlan_AndPreservesFinancialFacts(bool approve)
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var db = PostgresFixture.CreateDbContext(connection);
        await db.GetService<IMigrator>().MigrateAsync("20261020155313_AddUserServiceAccounts");
        var owner = await new UserDirectory(db, TimeProvider.System).ResolveAsync("https://auth.test.invalid", "migration-owner", "Owner", default);
        var family = Family.Create("Source", owner, DateTimeOffset.UtcNow);
        await new FamilyRepository(db).AddAsync(family, default);
        var service = ServiceAccount.Create(owner, ServiceType.Electricity, "Synthetic issuer", "Synthetic service", DateTimeOffset.UtcNow);
        var identifier = ServiceAccountIdentifier.Create(service, ServiceIdentifierType.UtilityCustomerNumber,
            [1, 2, 3], new string('a', 64), "***0000", DateTimeOffset.UtcNow);
        db.ServiceAccounts.Add(service);
        db.ServiceAccountIdentifiers.Add(identifier);
        await db.SaveChangesAsync();
        var account = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();
        var transaction = Guid.CreateVersion7();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO financial_accounts (id, family_id, name, type, scope, responsible_member_id, opening_balance, opening_balance_date, is_active, currency, created_at, created_by_user_id)
            VALUES ({account}, {family.Id}, 'Account', 'Checking', 'PF', {family.Members.Single().Id}, 1000.00, DATE '2026-10-01', TRUE, 'BRL', now(), {owner});
            INSERT INTO financial_categories (id, family_id, name, type, is_active, created_at, created_by_user_id)
            VALUES ({category}, {family.Id}, 'Income', 'Income', TRUE, now(), {owner});
            INSERT INTO financial_transactions (id, family_id, account_id, category_id, type, description, amount, transaction_date, currency, created_at, created_by_user_id, is_cancelled)
            VALUES ({transaction}, {family.Id}, {account}, {category}, 'Income', 'Income', 123.45, DATE '2026-10-02', 'BRL', now(), {owner}, FALSE);
            """);
        if (!approve)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("WORKSPACE_CUTOVER_PLAN_REQUIRED", error.MessageText);
            Assert.DoesNotContain("20261020222341_IntroduceFinancialWorkspaces", await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(123.45m, await db.Database.SqlQueryRaw<decimal>("SELECT amount AS \"Value\" FROM financial_transactions").SingleAsync());
            return;
        }
        var workspace = Guid.CreateVersion7();
        var plan = JsonSerializer.Serialize(new[] { new { family_id = family.Id, workspace_id = workspace, owner_user_id = owner } });
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('saldo360.workspace_cutover_plan', {plan}, false)");
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var stored = await db.FinancialAccounts.SingleAsync();
        Assert.Equal(account, stored.Id);
        Assert.Equal(workspace, stored.WorkspaceId);
        Assert.Equal(owner, stored.ResponsibleUserId);
        Assert.Equal(1000m, stored.OpeningBalance);
        Assert.Equal(new DateOnly(2026, 10, 1), stored.OpeningBalanceDate);
        var tx = await db.FinancialTransactions.SingleAsync();
        Assert.Equal(transaction, tx.Id);
        Assert.Equal(123.45m, tx.Amount);
        Assert.Equal(workspace, tx.WorkspaceId);
        Assert.Equal(1123.45m, stored.OpeningBalance + tx.Amount);
        Assert.Single(await db.Families.ToListAsync());
        Assert.Single(await db.FamilyMembers.ToListAsync());
        Assert.Equal(service.Id, (await db.ServiceAccounts.SingleAsync()).Id);
        var preservedIdentifier = await db.ServiceAccountIdentifiers.SingleAsync();
        Assert.Equal(owner, preservedIdentifier.UserId);
        Assert.Equal(identifier.LookupHash, preservedIdentifier.LookupHash);
        Assert.Equal(identifier.ProtectedNormalizedValue, preservedIdentifier.ProtectedNormalizedValue);
        var remaining = await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM information_schema.columns
            WHERE table_schema='public' AND column_name='family_id'
              AND (table_name LIKE 'financial_%' OR table_name='service_account_bindings')
            """).SingleAsync();
        Assert.Equal(0, remaining);
    }
}

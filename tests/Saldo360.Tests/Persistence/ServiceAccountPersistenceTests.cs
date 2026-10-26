using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Application.ServiceAccounts;
using Saldo360.Domain.ServiceAccounts;
using Saldo360.Domain.Users;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.ServiceAccounts;
using Saldo360.Tests.CreditCards;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

[Collection(PostgresCollection.Name)]
[Trait("Area", "Services")]
[Trait("Dependency", "PostgreSQL")]
public sealed class ServiceAccountPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string connectionString = postgres.NewDatabaseConnectionString();
    public Task InitializeAsync() => postgres.CreateDatabaseAsync(connectionString);
    public Task DisposeAsync() => Task.CompletedTask;
    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(connectionString);

    private async Task<Guid> AddUser(string subject)
    {
        await using var db = Db(); var user = ApplicationUser.Create("https://synthetic.invalid", subject, null, DateTimeOffset.UtcNow);
        db.Users.Add(user); await db.SaveChangesAsync(); return user.Id;
    }
    [Trait("Category", "INFRA")]
    [Fact]
    public async Task MigrationAppliesAndRevertsOnlyNewStructures()
    {
        await using var db = Db();
        var migrations = db.Database.GetMigrations().ToList();
        var serviceMigration = migrations.Single(m => m.EndsWith("AddUserServiceAccounts", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(migrations[migrations.IndexOf(serviceMigration) - 1]);
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('service_accounts') IS NULL AND to_regclass('financial_accounts') IS NOT NULL", connection);
        Assert.Equal(true, await command.ExecuteScalarAsync());
        await db.GetService<IMigrator>().MigrateAsync();
        Assert.Contains(serviceMigration, await db.Database.GetAppliedMigrationsAsync());
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task PostgreSqlEnforcesConcurrentUniquenessAndResolverIsolation()
    {
        var userA = await AddUser("owner-a"); var userB = await AddUser("owner-b");
        var a = ServiceAccount.Create(userA, ServiceType.Electricity, "Equatorial", "A", DateTimeOffset.UtcNow);
        var anotherA = ServiceAccount.Create(userA, ServiceType.Electricity, "Equatorial", "A2", DateTimeOffset.UtcNow);
        var b = ServiceAccount.Create(userB, ServiceType.Electricity, "Equatorial", "B", DateTimeOffset.UtcNow);
        await using (var db = Db()) { db.ServiceAccounts.AddRange(a, anotherA, b); await db.SaveChangesAsync(); }
        async Task<string> Add(Guid user, Guid id)
        {
            await using var db = Db();
            return await new ServiceAccountService(new ServiceAccountRepository(db), ServiceAccountResolverTests.Protection(), TimeProvider.System)
                .AddIdentifierAsync(user, id, ServiceIdentifierType.UtilityCustomerNumber, "98765432100", default);
        }
        var results = await Task.WhenAll(Add(userA, a.Id), Add(userA, anotherA.Id));
        Assert.Equal(1, results.Count(r => r == "Created")); Assert.Equal(1, results.Count(r => r == "Conflict"));
        Assert.Equal("Created", await Add(userB, b.Id));
        await using var check = Db(); var repo = new ServiceAccountRepository(check); var resolver = new ServiceAccountResolver(repo, ServiceAccountResolverTests.Protection());
        var identifier = new ExtractedServiceIdentifier(ServiceIdentifierType.UtilityCustomerNumber, "98765432100", "***2100", "High");
        Assert.Equal("Matched", (await resolver.ResolveAsync(userA, ServiceType.Electricity, [identifier], default)).State);
        Assert.Equal(b.Id, (await resolver.ResolveAsync(userB, ServiceType.Electricity, [identifier], default)).ServiceAccountId);
        Assert.Equal("NotFound", (await resolver.ResolveAsync(Guid.NewGuid(), ServiceType.Electricity, [identifier], default)).State);
        Assert.False(await check.FinancialTransactions.AnyAsync()); Assert.False(await check.FinancialPlannedTransactions.AnyAsync());
        Assert.False(await check.Families.AnyAsync()); Assert.False(await check.ServiceAccountBindings.AnyAsync());
        var stored = await check.ServiceAccountIdentifiers.FirstAsync();
        Assert.NotEmpty(stored.ProtectedNormalizedValue); Assert.DoesNotContain("98765432100", System.Text.Encoding.UTF8.GetString(stored.ProtectedNormalizedValue));
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task CompoundForeignKeyRejectsDifferentOwnerEvenWithKnownAccountId()
    {
        var a = await AddUser("owner-a"); var b = await AddUser("owner-b");
        var account = ServiceAccount.Create(a, ServiceType.Water, "DMAE", "A", DateTimeOffset.UtcNow);
        await using var db = Db(); db.ServiceAccounts.Add(account); await db.SaveChangesAsync();
        var fakeOwner = ServiceAccount.Create(b, ServiceType.Water, "DMAE", "B", DateTimeOffset.UtcNow);
        var identifier = ServiceAccountIdentifier.Create(fakeOwner, ServiceIdentifierType.CustomerCode, [1, 2], new string('a', 64), "***", DateTimeOffset.UtcNow);
        db.ServiceAccountIdentifiers.Add(identifier);
        db.Entry(identifier).Property(i => i.ServiceAccountId).CurrentValue = account.Id;
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
}

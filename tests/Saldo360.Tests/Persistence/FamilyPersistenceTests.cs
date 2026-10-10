using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Domain.Families;
using Saldo360.Domain.Users;
using Saldo360.Infrastructure.Families;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>
/// Regras garantidas pelo PostgreSQL real (constraints, collation, transação, concorrência).
/// O EF Core InMemory não aplica nenhuma delas, por isso estes testes usam o banco de verdade.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FamilyPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Issuer = "https://auth.test.invalid";

    private string _connectionString = null!;

    public async Task InitializeAsync() => _connectionString = await postgres.CreateDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Users_SameIssuerAndSubject_ViolateUniqueIndex()
    {
        await using var db = Db();
        db.Users.Add(NewUser(Issuer, "usuario"));
        await db.SaveChangesAsync();

        db.Users.Add(NewUser(Issuer, "usuario"));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Theory]
    [InlineData(Issuer, "Usuario", Issuer, "usuario")] // caixa diferente no subject
    [InlineData(Issuer, "usuario", "https://AUTH.test.invalid", "usuario")] // caixa diferente no issuer
    [InlineData(Issuer, "usuario", "https://outro.test.invalid", "usuario")] // mesmo subject, outro issuer
    [InlineData(Issuer, "usuario", Issuer, "usuario ")] // sem trim/normalização
    public async Task Users_IdentitiesDifferingInAnyWay_AreDistinct(string issuerA, string subjectA, string issuerB, string subjectB)
    {
        await using var db = Db();
        db.Users.AddRange(NewUser(issuerA, subjectA), NewUser(issuerB, subjectB));

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Users.CountAsync());
    }

    [Fact]
    public async Task FamilyMembers_SameFamilyAndUser_ViolateUniqueIndex()
    {
        await using var db = Db();
        var owner = NewUser(Issuer, "dono");
        db.Users.Add(owner);
        var family = Family.Create("Família Silva", owner.Id, DateTimeOffset.UtcNow);
        db.Families.Add(family);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO family_members (id, family_id, user_id, role, joined_at) VALUES ({Guid.CreateVersion7()}, {family.Id}, {owner.Id}, 'Member', now())"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task FamilyMembers_UnknownRole_ViolatesCheckConstraint()
    {
        await using var db = Db();
        var owner = NewUser(Issuer, "dono");
        db.Users.Add(owner);
        db.Families.Add(Family.Create("Família", owner.Id, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var family = await db.Families.SingleAsync();
        var other = NewUser(Issuer, "outro");
        db.Users.Add(other);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO family_members (id, family_id, user_id, role, joined_at) VALUES ({Guid.CreateVersion7()}, {family.Id}, {other.Id}, 'Admin', now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task FamilyStructure_IsNotLimitedToTwoPeople()
    {
        await using var db = Db();
        var users = Enumerable.Range(1, 5).Select(i => NewUser(Issuer, $"pessoa-{i}")).ToList();
        db.Users.AddRange(users);
        var family = Family.Create("Família grande", users[0].Id, DateTimeOffset.UtcNow);
        db.Families.Add(family);
        await db.SaveChangesAsync();

        foreach (var user in users.Skip(1))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO family_members (id, family_id, user_id, role, joined_at) VALUES ({Guid.CreateVersion7()}, {family.Id}, {user.Id}, 'Member', now())");
        }

        Assert.Equal(5, await db.FamilyMembers.CountAsync(m => m.FamilyId == family.Id));
    }

    [Fact]
    public async Task CreateFamily_WhenOwnerLinkFails_PersistsNothing()
    {
        // O vínculo Owner aponta para um usuário inexistente: o INSERT do membro viola a FK
        // depois do INSERT da família, na mesma transação.
        await using var db = Db();
        var family = Family.Create("Família órfã", Guid.CreateVersion7(), DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => new FamilyRepository(db).AddAsync(family, CancellationToken.None));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
        await using var verify = Db();
        Assert.Equal(0, await verify.Families.CountAsync());
        Assert.Equal(0, await verify.FamilyMembers.CountAsync());
    }

    [Fact]
    public async Task CreateFamily_PersistsFamilyWithExactlyOneOwner()
    {
        Guid userId;
        await using (var db = Db())
        {
            userId = await Directory(db).ResolveAsync(Issuer, "dono", "Dono", CancellationToken.None);
            await new FamilyRepository(db).AddAsync(Family.Create("  Família Souza  ", userId, DateTimeOffset.UtcNow), CancellationToken.None);
        }

        await using var verify = Db();
        var family = await verify.Families.SingleAsync();
        var member = await verify.FamilyMembers.SingleAsync();
        Assert.Equal("Família Souza", family.Name);
        Assert.Equal(family.Id, member.FamilyId);
        Assert.Equal(userId, member.UserId);
        Assert.Equal(FamilyRole.Owner, member.Role);
    }

    [Fact]
    public async Task ResolveUser_RepeatedCalls_ReturnSameUser_AndUpdateDisplayName()
    {
        await using var db = Db();
        var first = await Directory(db).ResolveAsync(Issuer, "repetido", "Nome antigo", CancellationToken.None);
        var second = await Directory(db).ResolveAsync(Issuer, "repetido", "Nome novo", CancellationToken.None);

        Assert.Equal(first, second);
        await using var verify = Db();
        var user = await verify.Users.SingleAsync();
        Assert.Equal("Nome novo", user.DisplayName);
    }

    [Fact]
    public async Task ResolveUser_ConcurrentFirstLogins_CreateASingleUser()
    {
        // Cada chamada usa seu próprio DbContext/conexão, como requisições paralelas.
        var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            await using var db = Db();
            return await Directory(db).ResolveAsync(Issuer, "concorrente", "Concorrente", CancellationToken.None);
        }));

        Assert.Single(ids.Distinct());
        await using var verify = Db();
        Assert.Equal(1, await verify.Users.CountAsync());
    }

    [Fact]
    public async Task ResolveUser_SameSubjectInTwoIssuers_CreatesTwoUsers()
    {
        await using var db = Db();
        var a = await Directory(db).ResolveAsync(Issuer, "mesmo-sub", null, CancellationToken.None);
        var b = await Directory(db).ResolveAsync("https://outro.test.invalid", "mesmo-sub", null, CancellationToken.None);

        Assert.NotEqual(a, b);
        Assert.Equal(2, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Migration_KeepsAuthenticationTables()
    {
        await using var db = Db();
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();

        Assert.Contains("user_sessions", tables);
        Assert.Contains("data_protection_keys", tables);
        Assert.Contains("application_users", tables);
        Assert.Contains("families", tables);
        Assert.Contains("family_members", tables);
    }

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private static UserDirectory Directory(Saldo360DbContext db) => new(db, TimeProvider.System);

    private static ApplicationUser NewUser(string issuer, string subject) =>
        ApplicationUser.Create(issuer, subject, null, DateTimeOffset.UtcNow);
}

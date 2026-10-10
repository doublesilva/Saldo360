using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Families;
using Saldo360.Infrastructure.Families;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>Constraints das contas financeiras no PostgreSQL real.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FinancialAccountPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = null!;
    private Guid _userA;
    private Family _familyA = null!;
    private Family _familyB = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        var users = new UserDirectory(db, TimeProvider.System);
        _userA = await users.ResolveAsync("https://auth.test.invalid", "a", "A", CancellationToken.None);
        var userB = await users.ResolveAsync("https://auth.test.invalid", "b", "B", CancellationToken.None);
        _familyA = Family.Create("Família A", _userA, DateTimeOffset.UtcNow);
        _familyB = Family.Create("Família B", userB, DateTimeOffset.UtcNow);
        var families = new FamilyRepository(db);
        await families.AddAsync(_familyA, CancellationToken.None);
        await families.AddAsync(_familyB, CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Guid MemberOfA => _familyA.Members.Single().Id;

    private Guid MemberOfB => _familyB.Members.Single().Id;

    [Fact]
    public async Task ResponsibleFromSameFamily_AndJointAccount_AreAccepted()
    {
        await using var db = Db();
        db.FinancialAccounts.AddRange(
            Account("Itaú PF", MemberOfA),
            Account("Dinheiro", responsibleMemberId: null));

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.FinancialAccounts.CountAsync());
    }

    [Fact]
    public async Task ResponsibleFromAnotherFamily_ViolatesCompositeForeignKey()
    {
        // Validação da aplicação contornada: o próprio banco recusa o vínculo de outra família.
        await using var db = Db();
        db.FinancialAccounts.Add(Account("Conta cruzada", MemberOfB));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgresError = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresError.SqlState);
        Assert.Equal("fk_financial_accounts_family_members_responsible", postgresError.ConstraintName);
    }

    [Fact]
    public async Task ResponsibleFromAnotherFamily_IsRejected_AlsoOnUpdate()
    {
        await using (var db = Db())
        {
            db.FinancialAccounts.Add(Account("Itaú PF", MemberOfA));
            await db.SaveChangesAsync();
        }

        await using var update = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => update.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE financial_accounts SET responsible_member_id = {MemberOfB}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Theory]
    [InlineData("type", "Credit")]
    [InlineData("type", "checking")]
    [InlineData("scope", "PX")]
    [InlineData("scope", "pf")]
    [InlineData("currency", "USD")]
    [InlineData("name", "   ")]
    public async Task InvalidValues_ViolateCheckConstraints(string column, string value)
    {
        await using (var db = Db())
        {
            db.FinancialAccounts.Add(Account("Itaú PF", MemberOfA));
            await db.SaveChangesAsync();
        }

        var sql = column switch
        {
            "type" => "UPDATE financial_accounts SET type = @value",
            "scope" => "UPDATE financial_accounts SET scope = @value",
            "currency" => "UPDATE financial_accounts SET currency = @value",
            _ => "UPDATE financial_accounts SET name = @value",
        };

        await using var update = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => update.Database.ExecuteSqlRawAsync(sql, new NpgsqlParameter("value", value)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task FamilyWithAccounts_CannotBeDeletedByCascade()
    {
        await using (var db = Db())
        {
            db.FinancialAccounts.Add(Account("Itaú PF", MemberOfA));
            await db.SaveChangesAsync();
        }

        await using var delete = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => delete.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM families WHERE id = {_familyA.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal(1, await delete.FinancialAccounts.CountAsync());
    }

    [Fact]
    public async Task Deactivation_PreservesTheRow()
    {
        Guid id;
        await using (var db = Db())
        {
            var account = Account("Itaú Empresas", MemberOfA, FinancialScope.PJ);
            db.FinancialAccounts.Add(account);
            await db.SaveChangesAsync();
            account.Update(Details("Itaú Empresas", MemberOfA, FinancialScope.PJ), isActive: false);
            await db.SaveChangesAsync();
            id = account.Id;
        }

        await using var verify = Db();
        var stored = await verify.FinancialAccounts.SingleAsync(a => a.Id == id);
        Assert.False(stored.IsActive);
        Assert.Equal("BRL", stored.Currency);
        Assert.Equal(_userA, stored.CreatedByUserId);
    }

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private FinancialAccount Account(string name, Guid? responsibleMemberId, FinancialScope scope = FinancialScope.PF) =>
        FinancialAccount.Create(_familyA.Id, Details(name, responsibleMemberId, scope), _userA, DateTimeOffset.UtcNow);

    private static AccountDetails Details(string name, Guid? responsibleMemberId, FinancialScope scope = FinancialScope.PF) =>
        AccountDetails.TryCreate(name, "Itaú", nameof(AccountType.Checking), scope.ToString(), responsibleMemberId, out _)!;
}

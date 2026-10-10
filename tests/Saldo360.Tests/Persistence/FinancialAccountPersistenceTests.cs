using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    private const string PreviousMigration = "20261010161724_AddFinancialAccounts";

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

    [Fact]
    public async Task ExistingAccount_StaysWithoutOpeningBalance_AfterMigration()
    {
        // Volta o banco ao esquema anterior, grava uma conta "antiga" e reaplica a migration do saldo inicial.
        var accountId = Guid.CreateVersion7();
        await using (var db = Db())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_accounts (id, family_id, name, institution, type, scope, responsible_member_id, is_active, currency, created_at, created_by_user_id)
                VALUES ({accountId}, {_familyA.Id}, 'Itaú PF', 'Itaú', 'Checking', 'PF', {MemberOfA}, TRUE, 'BRL', now(), {_userA})
                """);
            await db.Database.MigrateAsync();
        }

        await using var verify = Db();
        var stored = await verify.FinancialAccounts.SingleAsync(a => a.Id == accountId);
        Assert.Equal((null, null), (stored.OpeningBalance, stored.OpeningBalanceDate));
        Assert.Equal(("Itaú PF", "Itaú", AccountType.Checking, FinancialScope.PF, MemberOfA, true),
            (stored.Name, stored.Institution, stored.Type, stored.Scope, stored.ResponsibleMemberId, stored.IsActive));
    }

    [Fact]
    public async Task OpeningBalance_RoundTripsAsNumericAndDate()
    {
        Guid id;
        await using (var db = Db())
        {
            var account = Account("Itaú PF", MemberOfA, openingBalance: -1234.56m, openingBalanceDate: new DateOnly(2026, 10, 1));
            db.FinancialAccounts.Add(account);
            await db.SaveChangesAsync();
            id = account.Id;
        }

        await using var verify = Db();
        var stored = await verify.FinancialAccounts.SingleAsync(a => a.Id == id);
        Assert.Equal((-1234.56m, new DateOnly(2026, 10, 1)), (stored.OpeningBalance, stored.OpeningBalanceDate));
        var types = await verify.Database.SqlQueryRaw<string>("""
            SELECT string_agg(data_type, ',' ORDER BY column_name) AS "Value" FROM information_schema.columns
            WHERE table_name = 'financial_accounts' AND column_name IN ('opening_balance', 'opening_balance_date')
            """).SingleAsync();
        Assert.Equal("numeric,date", types);
    }

    [Theory]
    [InlineData("100.00", null)]
    [InlineData(null, "2026-10-01")]
    public async Task IncompleteOpeningBalance_ViolatesPairCheck(string? amount, string? date)
    {
        await using (var db = Db())
        {
            db.FinancialAccounts.Add(Account("Itaú PF", MemberOfA));
            await db.SaveChangesAsync();
        }

        await using var update = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => update.Database.ExecuteSqlRawAsync(
            "UPDATE financial_accounts SET opening_balance = @amount::numeric, opening_balance_date = @date::date",
            new NpgsqlParameter("amount", (object?)amount ?? DBNull.Value) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
            new NpgsqlParameter("date", (object?)date ?? DBNull.Value) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_financial_accounts_opening_balance_pair", exception.ConstraintName);
    }

    [Theory]
    [InlineData("10.005", "ck_financial_accounts_opening_balance_scale")]
    [InlineData("1.500", "ck_financial_accounts_opening_balance_scale")]
    [InlineData("10000000000000", "ck_financial_accounts_opening_balance_range")]
    [InlineData("-10000000000000", "ck_financial_accounts_opening_balance_range")]
    public async Task InvalidOpeningBalance_ViolatesCheck_InsteadOfRounding(string amount, string constraint)
    {
        await using (var db = Db())
        {
            db.FinancialAccounts.Add(Account("Itaú PF", MemberOfA));
            await db.SaveChangesAsync();
        }

        await using var update = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => update.Database.ExecuteSqlRawAsync(
            "UPDATE financial_accounts SET opening_balance = @amount::numeric, opening_balance_date = DATE '2026-10-01'",
            new NpgsqlParameter("amount", amount) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
        Assert.Null((await update.FinancialAccounts.SingleAsync()).OpeningBalance);
    }

    [Theory]
    [InlineData("1234.5", null, "openingBalanceDate")]
    [InlineData(null, "2026-10-01", "openingBalance")]
    [InlineData("0.001", "2026-10-01", "openingBalance")]
    [InlineData("1.500", "2026-10-01", "openingBalance")]
    public void Domain_RejectsIncompleteOrImpreciseOpeningBalance(string? amount, string? date, string field)
    {
        var details = AccountDetails.TryCreate(
            "Conta", null, nameof(AccountType.Cash), nameof(FinancialScope.PF), null,
            amount is null ? null : decimal.Parse(amount, CultureInfo.InvariantCulture),
            date is null ? null : DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            out var errors);

        Assert.Null(details);
        Assert.Equal([field], errors.Keys);
    }

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private FinancialAccount Account(
        string name, Guid? responsibleMemberId, FinancialScope scope = FinancialScope.PF,
        decimal? openingBalance = null, DateOnly? openingBalanceDate = null) =>
        FinancialAccount.Create(
            _familyA.Id, Details(name, responsibleMemberId, scope, openingBalance, openingBalanceDate), _userA, DateTimeOffset.UtcNow);

    private static AccountDetails Details(
        string name, Guid? responsibleMemberId, FinancialScope scope = FinancialScope.PF,
        decimal? openingBalance = null, DateOnly? openingBalanceDate = null) =>
        AccountDetails.TryCreate(
            name, "Itaú", nameof(AccountType.Checking), scope.ToString(), responsibleMemberId,
            openingBalance, openingBalanceDate, out _)!;
}

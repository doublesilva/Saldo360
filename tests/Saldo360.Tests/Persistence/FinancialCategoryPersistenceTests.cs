using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Families;
using Saldo360.Infrastructure.Categories;
using Saldo360.Infrastructure.Families;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>Constraints, unicidade case-insensitive e concorrência das categorias no PostgreSQL real.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FinancialCategoryPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string PreviousMigration = "20261010165551_AddOpeningBalanceToFinancialAccounts";
    private const string UniqueIndex = "ux_financial_categories_family_id_type_name";

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

    [Theory]
    [InlineData("mercado")]
    [InlineData("MERCADO")]
    [InlineData("MeRcAdO")]
    public async Task UniqueIndex_IgnoresCase(string duplicate)
    {
        await AddAsync(Category("Mercado"));

        var exception = await AssertUniqueViolationAsync(() => AddAsync(Category(duplicate)));

        Assert.Equal(UniqueIndex, exception.ConstraintName);
    }

    [Fact]
    public async Task UniqueIndex_IgnoresCase_ForAccentedLetters_ButKeepsAccentsDistinct()
    {
        await AddAsync(Category("Saúde"));

        await AssertUniqueViolationAsync(() => AddAsync(Category("SAÚDE")));
        await AddAsync(Category("Saude")); // sem acento: outro nome

        await using var db = Db();
        Assert.Equal(2, await db.FinancialCategories.CountAsync());
    }

    [Fact]
    public async Task UniqueIndex_IncludesInactiveCategories()
    {
        var inactive = Category("Academia");
        inactive.Update("Academia", isActive: false);
        await AddAsync(inactive);

        await AssertUniqueViolationAsync(() => AddAsync(Category("academia")));
    }

    [Fact]
    public async Task SameName_IsAccepted_InOtherTypeOrOtherFamily()
    {
        await AddAsync(Category("Outros", CategoryType.Expense));
        await AddAsync(Category("Outros", CategoryType.Income));
        await AddAsync(Category("Outros", CategoryType.Expense, _familyB.Id));

        await using var db = Db();
        Assert.Equal(3, await db.FinancialCategories.CountAsync());
    }

    [Fact]
    public async Task Repository_ConcurrentInsert_ReturnsFalse_InsteadOfDuplicating()
    {
        // Duas requisições passam pela verificação prévia ao mesmo tempo; o índice decide.
        await using var first = Db();
        await using var second = Db();
        var firstRepository = new FinancialCategoryRepository(first);
        var secondRepository = new FinancialCategoryRepository(second);
        Assert.False(await firstRepository.NameExistsAsync(_familyA.Id, CategoryType.Expense, "Farmácia", null, CancellationToken.None));
        Assert.False(await secondRepository.NameExistsAsync(_familyA.Id, CategoryType.Expense, "FARMÁCIA", null, CancellationToken.None));

        Assert.True(await firstRepository.TryAddAsync(Category("Farmácia"), CancellationToken.None));
        Assert.False(await secondRepository.TryAddAsync(Category("FARMÁCIA"), CancellationToken.None));

        await using var verify = Db();
        Assert.Equal("Farmácia", (await verify.FinancialCategories.SingleAsync()).Name);
    }

    [Fact]
    public async Task Repository_ConcurrentRename_ReturnsFalse_AndKeepsOriginalName()
    {
        var feira = Category("Feira");
        var sacolao = Category("Sacolão");
        await AddAsync(feira);
        await AddAsync(sacolao);

        await using var first = Db();
        await using var second = Db();
        var firstRepository = new FinancialCategoryRepository(first);
        var secondRepository = new FinancialCategoryRepository(second);
        var a = (await firstRepository.FindAsync(_familyA.Id, feira.Id, CancellationToken.None))!;
        var b = (await secondRepository.FindAsync(_familyA.Id, sacolao.Id, CancellationToken.None))!;
        a.Update("Hortifruti", isActive: true);
        b.Update("HORTIFRUTI", isActive: true);

        Assert.True(await firstRepository.TrySaveChangesAsync(CancellationToken.None));
        Assert.False(await secondRepository.TrySaveChangesAsync(CancellationToken.None));

        await using var verify = Db();
        Assert.Equal("Sacolão", (await verify.FinancialCategories.SingleAsync(c => c.Id == sacolao.Id)).Name);
    }

    [Theory]
    [InlineData("type", "Saving", PostgresErrorCodes.CheckViolation)]
    [InlineData("type", "expense", PostgresErrorCodes.CheckViolation)]
    [InlineData("name", "   ", PostgresErrorCodes.CheckViolation)]
    [InlineData("name", "", PostgresErrorCodes.CheckViolation)]
    [InlineData("name", " Mercado", PostgresErrorCodes.CheckViolation)]
    [InlineData("name", "Mercado ", PostgresErrorCodes.CheckViolation)]
    [InlineData("name", "x101", PostgresErrorCodes.StringDataRightTruncation)]
    public async Task InvalidValues_AreRejectedByTheDatabase(string column, string value, string sqlState)
    {
        await AddAsync(Category("Mercado"));
        if (value == "x101")
        {
            value = new string('x', 101);
        }

        var sql = column == "type"
            ? "UPDATE financial_categories SET type = @value"
            : "UPDATE financial_categories SET name = @value";

        await using var update = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => update.Database.ExecuteSqlRawAsync(sql, new NpgsqlParameter("value", value)));
        Assert.Equal(sqlState, exception.SqlState);
    }

    [Fact]
    public async Task FamilyWithCategories_CannotBeDeletedByCascade()
    {
        await AddAsync(Category("Mercado"));

        await using var delete = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => delete.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM families WHERE id = {_familyA.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task Migration_PreservesExistingAccountsAndOpeningBalances()
    {
        // Volta ao esquema anterior, grava conta com saldo inicial, reaplica a migration das categorias.
        var accountId = Guid.CreateVersion7();
        var memberId = _familyA.Members.Single().Id;
        await using (var db = Db())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_accounts (id, family_id, name, institution, type, scope, responsible_member_id, opening_balance, opening_balance_date, is_active, currency, created_at, created_by_user_id)
                VALUES ({accountId}, {_familyA.Id}, 'Itaú PF', 'Itaú', 'Checking', 'PF', {memberId}, -1234.56, DATE '2026-10-01', TRUE, 'BRL', now(), {_userA})
                """);
            await db.Database.MigrateAsync();
        }

        await using var verify = Db();
        var account = await verify.FinancialAccounts.SingleAsync(a => a.Id == accountId);
        Assert.Equal(("Itaú PF", -1234.56m, new DateOnly(2026, 10, 1), memberId),
            (account.Name, account.OpeningBalance, account.OpeningBalanceDate, account.ResponsibleMemberId));
        Assert.Equal(2, await verify.Users.CountAsync());
        Assert.Equal(2, await verify.Families.CountAsync());
        Assert.Equal(0, await verify.FinancialCategories.CountAsync()); // nenhuma categoria criada automaticamente
    }

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private FinancialCategory Category(string name, CategoryType type = CategoryType.Expense, Guid? familyId = null) =>
        FinancialCategory.Create(familyId ?? _familyA.Id, name, type, _userA, DateTimeOffset.UtcNow);

    private async Task AddAsync(FinancialCategory category)
    {
        await using var db = Db();
        db.FinancialCategories.Add(category);
        await db.SaveChangesAsync();
    }

    private static async Task<PostgresException> AssertUniqueViolationAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(action);
        var postgresError = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresError.SqlState);
        return postgresError;
    }
}

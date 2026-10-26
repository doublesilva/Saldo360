using Saldo360.Domain.Workspaces;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Families;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>Constraints relacionais e de valor dos lançamentos no PostgreSQL real.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Area", "Transactions")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialTransactionPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string PreviousMigration = "20261010171119_AddFinancialCategories";

    private string _connectionString = null!;
    private Guid _userA;
    private FinancialWorkspace _familyA = null!;
    private FinancialWorkspace _familyB = null!;
    private FinancialAccount _accountA = null!;
    private FinancialAccount _accountB = null!;
    private FinancialCategory _expenseA = null!;
    private FinancialCategory _incomeA = null!;
    private FinancialCategory _expenseB = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        var users = new UserDirectory(db, TimeProvider.System);
        _userA = await users.ResolveAsync("https://auth.test.invalid", "a", "A", CancellationToken.None);
        var userB = await users.ResolveAsync("https://auth.test.invalid", "b", "B", CancellationToken.None);
        _familyA = FinancialWorkspace.CreatePersonal(_userA, DateTimeOffset.UtcNow);
        _familyB = FinancialWorkspace.CreatePersonal(userB, DateTimeOffset.UtcNow);
        var families = new FinancialWorkspaceRepository(db);
        await families.AddAsync(_familyA, CancellationToken.None);
        await families.AddAsync(_familyB, CancellationToken.None);

        _accountA = Account(_familyA.Id, _userA);
        _accountB = Account(_familyB.Id, userB);
        _expenseA = FinancialCategory.Create(_familyA.Id, "Mercado", CategoryType.Expense, _userA, DateTimeOffset.UtcNow);
        _incomeA = FinancialCategory.Create(_familyA.Id, "Salário", CategoryType.Income, _userA, DateTimeOffset.UtcNow);
        _expenseB = FinancialCategory.Create(_familyB.Id, "Mercado", CategoryType.Expense, userB, DateTimeOffset.UtcNow);
        db.AddRange(_accountA, _accountB, _expenseA, _incomeA, _expenseB);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Trait("Category", "CORE")]
    [Fact]
    public async Task ValidTransactions_AreStoredExactly()
    {
        await AddAsync(Transaction(_accountA.Id, _expenseA.Id, CategoryType.Expense, 1234.56m));
        await AddAsync(Transaction(_accountA.Id, _incomeA.Id, CategoryType.Income, 0.01m));

        await using var db = Db();
        var texts = await db.Database.SqlQueryRaw<string>(
            "SELECT type || ':' || amount::text AS \"Value\" FROM financial_transactions ORDER BY amount").ToListAsync();
        Assert.Equal(["Income:0.01", "Expense:1234.56"], texts);
        var types = await db.Database.SqlQueryRaw<string>("""
            SELECT string_agg(data_type, ',' ORDER BY column_name) AS "Value" FROM information_schema.columns
            WHERE table_name = 'financial_transactions' AND column_name IN ('amount', 'transaction_date')
            """).SingleAsync();
        Assert.Equal("numeric,date", types);
    }

    [Trait("Category", "CORE")]
    [Fact]
    [Trait("Profile", "FAST")]
    public async Task AccountFromAnotherFamily_ViolatesCompositeForeignKey()
    {
        var exception = await AssertViolationAsync(
            () => AddAsync(Transaction(_accountB.Id, _expenseA.Id, CategoryType.Expense)), PostgresErrorCodes.ForeignKeyViolation);
        Assert.Equal("fk_financial_transactions_accounts_same_family", exception.ConstraintName);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task CategoryFromAnotherFamily_ViolatesCompositeForeignKey()
    {
        var exception = await AssertViolationAsync(
            () => AddAsync(Transaction(_accountA.Id, _expenseB.Id, CategoryType.Expense)), PostgresErrorCodes.ForeignKeyViolation);
        Assert.Equal("fk_financial_transactions_categories_same_family_type", exception.ConstraintName);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task CategoryOfOtherType_ViolatesCompositeForeignKey()
    {
        var exception = await AssertViolationAsync(
            () => AddAsync(Transaction(_accountA.Id, _expenseA.Id, CategoryType.Income)), PostgresErrorCodes.ForeignKeyViolation);
        Assert.Equal("fk_financial_transactions_categories_same_family_type", exception.ConstraintName);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task TypeChangeWithoutMatchingCategory_IsRejected_AlsoOnUpdate()
    {
        await AddAsync(Transaction(_accountA.Id, _expenseA.Id, CategoryType.Expense));

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlRawAsync("UPDATE financial_transactions SET type = 'Income'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("amount", "0", "ck_financial_transactions_amount_positive")]
    [InlineData("amount", "-10.00", "ck_financial_transactions_amount_positive")]
    [InlineData("amount", "10.005", "ck_financial_transactions_amount_scale")]
    [InlineData("amount", "1.500", "ck_financial_transactions_amount_scale")]
    [InlineData("amount", "10000000000000", "ck_financial_transactions_amount_range")]
    [InlineData("description", "   ", "ck_financial_transactions_description_not_blank")]
    [InlineData("description", " Feira", "ck_financial_transactions_description_trimmed")]
    [InlineData("type", "Transfer", "ck_financial_transactions_type")]
    [InlineData("currency", "USD", "ck_financial_transactions_currency")]
    [Trait("Profile", "FAST")]
    public async Task InvalidValues_ViolateCheckConstraints(string column, string value, string constraint)
    {
        await AddAsync(Transaction(_accountA.Id, _expenseA.Id, CategoryType.Expense));
        var sql = column switch
        {
            "amount" => "UPDATE financial_transactions SET amount = @value::numeric",
            "description" => "UPDATE financial_transactions SET description = @value",
            "type" => "UPDATE financial_transactions SET type = @value",
            _ => "UPDATE financial_transactions SET currency = @value",
        };

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            sql, new NpgsqlParameter("value", value) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }));
        Assert.Contains(exception.SqlState, new[] { PostgresErrorCodes.CheckViolation, PostgresErrorCodes.ForeignKeyViolation });
        if (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            Assert.Equal(constraint, exception.ConstraintName);
        }
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("UPDATE financial_transactions SET is_cancelled = TRUE", "ck_financial_transactions_cancelled_at")]
    [InlineData("UPDATE financial_transactions SET cancelled_at = now()", "ck_financial_transactions_cancelled_at")]
    [InlineData("UPDATE financial_transactions SET updated_at = now()", "ck_financial_transactions_updated_pair")]
    public async Task InconsistentAuditState_ViolatesCheckConstraints(string sql, string constraint)
    {
        await AddAsync(Transaction(_accountA.Id, _expenseA.Id, CategoryType.Expense));

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal((PostgresErrorCodes.CheckViolation, constraint), (exception.SqlState, exception.ConstraintName));
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("DELETE FROM financial_accounts")]
    [InlineData("DELETE FROM financial_categories")]
    [InlineData("DELETE FROM financial_workspaces")]
    public async Task NothingDeletesHistoryByCascade(string sql)
    {
        await AddAsync(Transaction(_accountA.Id, _expenseA.Id, CategoryType.Expense));

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal(1, await db.FinancialTransactions.CountAsync());
    }

    [Trait("Category", "INFRA")]
    [Fact]
    public async Task Migration_PreservesExistingAccountsCategoriesAndOpeningBalances()
    {
        var accountId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        var memberId = _familyA.OwnerUserId;
        await using (var db = Db())
        {
            await HistoricalFinancialSchema.PrepareAsync(db, PreviousMigration);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_accounts (id, family_id, name, institution, type, scope, responsible_member_id, opening_balance, opening_balance_date, is_active, currency, created_at, created_by_user_id)
                VALUES ({accountId}, {_familyA.Id}, 'Conta antiga', 'Itaú', 'Checking', 'PF', {memberId}, -1234.56, DATE '2026-10-01', FALSE, 'BRL', now(), {_userA})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_categories (id, family_id, name, type, is_active, created_at, created_by_user_id)
                VALUES ({categoryId}, {_familyA.Id}, 'Categoria antiga', 'Expense', FALSE, now(), {_userA})
                """);
            await db.Database.MigrateAsync();
        }

        await using var verify = Db();
        var account = await verify.FinancialAccounts.SingleAsync(a => a.Id == accountId);
        Assert.Equal(("Conta antiga", -1234.56m, new DateOnly(2026, 10, 1), false),
            (account.Name, account.OpeningBalance, account.OpeningBalanceDate, account.IsActive));
        var category = await verify.FinancialCategories.SingleAsync(c => c.Id == categoryId);
        Assert.Equal(("Categoria antiga", CategoryType.Expense, false), (category.Name, category.Type, category.IsActive));
        Assert.Equal(0, await verify.FinancialTransactions.CountAsync());
    }





    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private static FinancialAccount Account(Guid workspaceId, Guid userId) =>
        FinancialAccount.Create(
            workspaceId,
            AccountDetails.TryCreate("Conta", null, nameof(AccountType.Checking), nameof(FinancialScope.PF), null, null, null, out _)!,
            userId,
            DateTimeOffset.UtcNow);

    private FinancialTransaction Transaction(Guid accountId, Guid categoryId, CategoryType type, decimal amount = 10m) =>
        FinancialTransaction.Create(_familyA.Id, Details(accountId, categoryId, type, amount), _userA, DateTimeOffset.UtcNow);

    private static TransactionDetails Details(Guid accountId, Guid categoryId, CategoryType type, decimal amount) =>
        TransactionDetails.TryCreate(
            accountId, categoryId, type.ToString(), "Compra", amount, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), out _)!;

    private async Task AddAsync(FinancialTransaction transaction)
    {
        await using var db = Db();
        db.FinancialTransactions.Add(transaction);
        await db.SaveChangesAsync();
    }

    private static async Task<PostgresException> AssertViolationAsync(Func<Task> action, string sqlState)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(action);
        var postgresError = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(sqlState, postgresError.SqlState);
        return postgresError;
    }
}

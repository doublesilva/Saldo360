using Saldo360.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Families;
using Saldo360.Domain.Planning;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>Constraints relacionais e de valor das previsões no PostgreSQL real.</summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialPlannedTransactionPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string PreviousMigration = "20261011153211_AddFinancialTransfers";

    private string _connectionString = null!;
    private Guid _userA;
    private FinancialWorkspace _familyA = null!;
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
        var familyB = FinancialWorkspace.CreatePersonal(userB, DateTimeOffset.UtcNow);
        var families = new FinancialWorkspaceRepository(db);
        await families.AddAsync(_familyA, CancellationToken.None);
        await families.AddAsync(familyB, CancellationToken.None);

        _accountA = Account(_familyA.Id, _userA);
        _accountB = Account(familyB.Id, userB);
        _expenseA = FinancialCategory.Create(_familyA.Id, "Aluguel", CategoryType.Expense, _userA, DateTimeOffset.UtcNow);
        _incomeA = FinancialCategory.Create(_familyA.Id, "Salário", CategoryType.Income, _userA, DateTimeOffset.UtcNow);
        _expenseB = FinancialCategory.Create(familyB.Id, "Aluguel", CategoryType.Expense, userB, DateTimeOffset.UtcNow);
        db.AddRange(_accountA, _accountB, _expenseA, _incomeA, _expenseB);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Trait("Category", "CORE")]
    [Fact]
    public async Task ValidPlanned_IsStoredExactly_AsNumericAndDate_IncludingFutureDate()
    {
        await AddAsync(Planned(_accountA.Id, _expenseA.Id, CategoryType.Expense, 1234.56m, new DateOnly(2030, 12, 31)));

        await using var db = Db();
        var text = await db.Database.SqlQueryRaw<string>(
            "SELECT amount::text || ' ' || due_date::text AS \"Value\" FROM financial_planned_transactions").SingleAsync();
        Assert.Equal("1234.56 2030-12-31", text);
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("account-other-family", "fk_financial_planned_transactions_accounts_same_family")]
    [InlineData("category-other-family", "fk_financial_planned_transactions_categories_same_family_type")]
    [InlineData("category-other-type", "fk_financial_planned_transactions_categories_same_family_type")]
    public async Task InvalidReferences_ViolateCompositeForeignKeys(string scenario, string constraint)
    {
        var planned = scenario switch
        {
            "account-other-family" => Planned(_accountB.Id, _expenseA.Id, CategoryType.Expense),
            "category-other-family" => Planned(_accountA.Id, _expenseB.Id, CategoryType.Expense),
            _ => Planned(_accountA.Id, _expenseA.Id, CategoryType.Income),
        };

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(planned));

        var postgresError = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal((PostgresErrorCodes.ForeignKeyViolation, constraint), (postgresError.SqlState, postgresError.ConstraintName));
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("UPDATE financial_planned_transactions SET amount = 0", "ck_financial_planned_transactions_amount_positive")]
    [InlineData("UPDATE financial_planned_transactions SET amount = -1", "ck_financial_planned_transactions_amount_positive")]
    [InlineData("UPDATE financial_planned_transactions SET amount = 10.005", "ck_financial_planned_transactions_amount_scale")]
    [InlineData("UPDATE financial_planned_transactions SET amount = 1.500", "ck_financial_planned_transactions_amount_scale")]
    [InlineData("UPDATE financial_planned_transactions SET amount = 10000000000000", "ck_financial_planned_transactions_amount_range")]
    [InlineData("UPDATE financial_planned_transactions SET description = '  '", "ck_financial_planned_transactions_description_not_blank")]
    [InlineData("UPDATE financial_planned_transactions SET description = ' Aluguel'", "ck_financial_planned_transactions_description_trimmed")]
    [InlineData("UPDATE financial_planned_transactions SET type = 'Transfer'", "ck_financial_planned_transactions_type")]
    [InlineData("UPDATE financial_planned_transactions SET currency = 'USD'", "ck_financial_planned_transactions_currency")]
    [InlineData("UPDATE financial_planned_transactions SET updated_at = now()", "ck_financial_planned_transactions_updated_pair")]
    [InlineData("UPDATE financial_planned_transactions SET is_cancelled = TRUE", "ck_financial_planned_transactions_cancelled_at")]
    [InlineData("UPDATE financial_planned_transactions SET is_cancelled = TRUE, cancelled_at = now()", "ck_financial_planned_transactions_cancelled_pair")]
    public async Task InvalidValues_ViolateCheckConstraints(string sql, string constraint)
    {
        await AddAsync(Planned(_accountA.Id, _expenseA.Id, CategoryType.Expense));

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal((PostgresErrorCodes.CheckViolation, constraint), (exception.SqlState, exception.ConstraintName));
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("DELETE FROM financial_accounts")]
    [InlineData("DELETE FROM financial_categories")]
    [InlineData("DELETE FROM financial_workspaces")]
    public async Task NothingDeletesPlannedByCascade(string sql)
    {
        await AddAsync(Planned(_accountA.Id, _expenseA.Id, CategoryType.Expense));

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal(1, await db.FinancialPlannedTransactions.CountAsync());
    }

    [Trait("Category", "INFRA")]
    [Fact]
    public async Task Migration_PreservesExistingMovements()
    {
        var transactionId = Guid.CreateVersion7();
        var transferId = Guid.CreateVersion7();
        var otherAccount = Guid.CreateVersion7();
        await using (var db = Db())
        {
            await HistoricalFinancialSchema.PrepareAsync(db, PreviousMigration);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_accounts (id, family_id, name, type, scope, opening_balance, opening_balance_date, is_active, currency, created_at, created_by_user_id)
                VALUES ({otherAccount}, {_familyA.Id}, 'Outra', 'Checking', 'PJ', -1234.56, DATE '2026-09-01', TRUE, 'BRL', now(), {_userA})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_transactions (id, family_id, account_id, category_id, type, description, amount, transaction_date, currency, created_at, created_by_user_id, is_cancelled)
                VALUES ({transactionId}, {_familyA.Id}, {_accountA.Id}, {_expenseA.Id}, 'Expense', 'Compra', 12.34, DATE '2026-09-02', 'BRL', now(), {_userA}, FALSE)
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_transfers (id, family_id, source_account_id, destination_account_id, amount, transfer_date, description, currency, created_at, created_by_user_id, is_cancelled)
                VALUES ({transferId}, {_familyA.Id}, {otherAccount}, {_accountA.Id}, 500.00, DATE '2026-09-03', 'Pró-labore', 'BRL', now(), {_userA}, FALSE)
                """);
            await db.Database.MigrateAsync();
        }

        await using var verify = Db();
        Assert.Equal(12.34m, (await verify.FinancialTransactions.SingleAsync(t => t.Id == transactionId)).Amount);
        Assert.Equal(500.00m, (await verify.FinancialTransfers.SingleAsync(t => t.Id == transferId)).Amount);
        Assert.Equal(-1234.56m, (await verify.FinancialAccounts.SingleAsync(a => a.Id == otherAccount)).OpeningBalance);
        Assert.Equal(0, await verify.FinancialPlannedTransactions.CountAsync());
    }


    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private static FinancialAccount Account(Guid workspaceId, Guid userId) =>
        FinancialAccount.Create(
            workspaceId,
            AccountDetails.TryCreate("Conta", null, nameof(AccountType.Checking), nameof(FinancialScope.PF), null, null, null, out _)!,
            userId,
            DateTimeOffset.UtcNow);

    private FinancialPlannedTransaction Planned(Guid accountId, Guid categoryId, CategoryType type, decimal amount = 10m, DateOnly? dueDate = null) =>
        FinancialPlannedTransaction.Create(
            _familyA.Id, Details(accountId, categoryId, type, amount, dueDate ?? new DateOnly(2026, 12, 1)), _userA, DateTimeOffset.UtcNow);

    private static PlannedTransactionDetails Details(Guid accountId, Guid categoryId, CategoryType type, decimal amount, DateOnly dueDate) =>
        PlannedTransactionDetails.TryCreate(accountId, categoryId, type.ToString(), "Aluguel", amount, dueDate, out _)!;

    private async Task AddAsync(FinancialPlannedTransaction planned)
    {
        await using var db = Db();
        db.FinancialPlannedTransactions.Add(planned);
        await db.SaveChangesAsync();
    }
}

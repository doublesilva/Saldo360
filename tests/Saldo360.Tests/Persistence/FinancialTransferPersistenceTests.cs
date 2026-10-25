using Saldo360.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Families;
using Saldo360.Domain.Transfers;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>Constraints das transferências no PostgreSQL real.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FinancialTransferPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string PreviousMigration = "20261010174727_AddFinancialTransactions";

    private string _connectionString = null!;
    private Guid _userA;
    private FinancialWorkspace _familyA = null!;
    private FinancialAccount _pjA = null!;
    private FinancialAccount _pfA = null!;
    private FinancialAccount _accountB = null!;

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

        _pjA = Account(_familyA.Id, _userA, FinancialScope.PJ);
        _pfA = Account(_familyA.Id, _userA, FinancialScope.PF);
        _accountB = Account(familyB.Id, userB, FinancialScope.PF);
        db.AddRange(_pjA, _pfA, _accountB);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Trait("Category", "CORE")]
    [Fact]
    public async Task ValidTransfer_IsStoredExactly_AsNumericAndDate()
    {
        await AddAsync(Transfer(_pjA.Id, _pfA.Id, 1234.56m));

        await using var db = Db();
        var text = await db.Database.SqlQueryRaw<string>(
            "SELECT amount::text || ' ' || transfer_date::text AS \"Value\" FROM financial_transfers").SingleAsync();
        Assert.Equal("1234.56 2026-09-01", text);
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    public async Task AccountFromAnotherFamily_ViolatesCompositeForeignKey(string side)
    {
        var transfer = side == "source" ? Transfer(_accountB.Id, _pfA.Id) : Transfer(_pjA.Id, _accountB.Id);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(transfer));

        var postgresError = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresError.SqlState);
        Assert.Equal($"fk_financial_transfers_{side}_account_same_family", postgresError.ConstraintName);
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("UPDATE financial_transfers SET destination_account_id = source_account_id", "ck_financial_transfers_distinct_accounts")]
    [InlineData("UPDATE financial_transfers SET amount = 0", "ck_financial_transfers_amount_positive")]
    [InlineData("UPDATE financial_transfers SET amount = -1", "ck_financial_transfers_amount_positive")]
    [InlineData("UPDATE financial_transfers SET amount = 10.005", "ck_financial_transfers_amount_scale")]
    [InlineData("UPDATE financial_transfers SET amount = 1.500", "ck_financial_transfers_amount_scale")]
    [InlineData("UPDATE financial_transfers SET amount = 10000000000000", "ck_financial_transfers_amount_range")]
    [InlineData("UPDATE financial_transfers SET description = '  '", "ck_financial_transfers_description_not_blank")]
    [InlineData("UPDATE financial_transfers SET description = 'Pró-labore '", "ck_financial_transfers_description_trimmed")]
    [InlineData("UPDATE financial_transfers SET currency = 'USD'", "ck_financial_transfers_currency")]
    [InlineData("UPDATE financial_transfers SET is_cancelled = TRUE", "ck_financial_transfers_cancelled_at")]
    [InlineData("UPDATE financial_transfers SET is_cancelled = TRUE, cancelled_at = now()", "ck_financial_transfers_cancelled_pair")]
    public async Task InvalidValues_ViolateCheckConstraints(string sql, string constraint)
    {
        await AddAsync(Transfer(_pjA.Id, _pfA.Id));

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal((PostgresErrorCodes.CheckViolation, constraint), (exception.SqlState, exception.ConstraintName));
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("DELETE FROM financial_accounts")]
    [InlineData("DELETE FROM financial_workspaces")]
    public async Task NothingDeletesTransfersByCascade(string sql)
    {
        await AddAsync(Transfer(_pjA.Id, _pfA.Id));

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal(1, await db.FinancialTransfers.CountAsync());
    }

    [Trait("Category", "INFRA")]
    [Fact]
    public async Task Migration_PreservesExistingAccountsAndTransactions()
    {
        var accountId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        var transactionId = Guid.CreateVersion7();
        await using (var db = Db())
        {
            await HistoricalFinancialSchema.PrepareAsync(db, PreviousMigration);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_accounts (id, family_id, name, type, scope, opening_balance, opening_balance_date, is_active, currency, created_at, created_by_user_id)
                VALUES ({accountId}, {_familyA.Id}, 'Conta antiga', 'Checking', 'PJ', 2000.00, DATE '2026-09-01', TRUE, 'BRL', now(), {_userA})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_categories (id, family_id, name, type, is_active, created_at, created_by_user_id)
                VALUES ({categoryId}, {_familyA.Id}, 'Mercado', 'Expense', TRUE, now(), {_userA})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_transactions (id, family_id, account_id, category_id, type, description, amount, transaction_date, currency, created_at, created_by_user_id, is_cancelled)
                VALUES ({transactionId}, {_familyA.Id}, {accountId}, {categoryId}, 'Expense', 'Compra', 12.34, DATE '2026-09-02', 'BRL', now(), {_userA}, FALSE)
                """);
            await db.Database.MigrateAsync();
        }

        await using var verify = Db();
        var account = await verify.FinancialAccounts.SingleAsync(a => a.Id == accountId);
        var transaction = await verify.FinancialTransactions.SingleAsync(t => t.Id == transactionId);
        Assert.Equal((2000.00m, new DateOnly(2026, 9, 1)), (account.OpeningBalance, account.OpeningBalanceDate));
        Assert.Equal((12.34m, accountId), (transaction.Amount, transaction.AccountId));
        Assert.Equal(0, await verify.FinancialTransfers.CountAsync());
    }



    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private static FinancialAccount Account(Guid workspaceId, Guid userId, FinancialScope scope) =>
        FinancialAccount.Create(
            workspaceId,
            AccountDetails.TryCreate($"Conta {scope}", null, nameof(AccountType.Checking), scope.ToString(), null, null, null, out _)!,
            userId,
            DateTimeOffset.UtcNow);

    private FinancialTransfer Transfer(Guid source, Guid destination, decimal amount = 10m) =>
        FinancialTransfer.Create(
            _familyA.Id,
            TransferDetails.TryCreate(source, destination, amount, new DateOnly(2026, 9, 1), "Pró-labore", new DateOnly(2026, 10, 1), out _)!,
            _userA,
            DateTimeOffset.UtcNow);

    private async Task AddAsync(FinancialTransfer transfer)
    {
        await using var db = Db();
        db.FinancialTransfers.Add(transfer);
        await db.SaveChangesAsync();
    }
}

using Saldo360.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Families;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>Vínculo previsão ↔ lançamento efetivo no PostgreSQL real: unicidade ativa, mesma família, sem cascatas.</summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PlannedRealizationPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string PreviousMigration = "20261012152640_AddFinancialPlannedTransactions";
    private const string ActiveIndex = "ux_financial_transactions_active_realization";

    private string _connectionString = null!;
    private Guid _userA;
    private Guid _userB;
    private FinancialWorkspace _familyA = null!;
    private FinancialWorkspace _familyB = null!;
    private FinancialAccount _accountA = null!;
    private FinancialCategory _expenseA = null!;
    private FinancialAccount _accountB = null!;
    private FinancialCategory _expenseB = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        var users = new UserDirectory(db, TimeProvider.System);
        _userA = await users.ResolveAsync("https://auth.test.invalid", "a", "A", CancellationToken.None);
        _userB = await users.ResolveAsync("https://auth.test.invalid", "b", "B", CancellationToken.None);
        _familyA = FinancialWorkspace.CreatePersonal(_userA, DateTimeOffset.UtcNow);
        _familyB = FinancialWorkspace.CreatePersonal(_userB, DateTimeOffset.UtcNow);
        var families = new FinancialWorkspaceRepository(db);
        await families.AddAsync(_familyA, CancellationToken.None);
        await families.AddAsync(_familyB, CancellationToken.None);

        _accountA = Account(_familyA.Id, _userA);
        _accountB = Account(_familyB.Id, _userB);
        _expenseA = FinancialCategory.Create(_familyA.Id, "Aluguel", CategoryType.Expense, _userA, DateTimeOffset.UtcNow);
        _expenseB = FinancialCategory.Create(_familyB.Id, "Aluguel", CategoryType.Expense, _userB, DateTimeOffset.UtcNow);
        db.AddRange(_accountA, _accountB, _expenseA, _expenseB);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Trait("Category", "CORE")]
    [Fact]
    public async Task SecondActiveRealization_ViolatesThePartialUniqueIndex()
    {
        var planned = await AddPlannedAsync();
        await AddAsync(Realization(planned));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(Realization(planned)));

        var postgresError = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal((PostgresErrorCodes.UniqueViolation, ActiveIndex), (postgresError.SqlState, postgresError.ConstraintName));
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task CancelledRealizations_StayLinked_AndDoNotBlockANewOne()
    {
        var planned = await AddPlannedAsync();
        var first = Realization(planned);
        first.Cancel(_userA, DateTimeOffset.UtcNow);
        var second = Realization(planned);
        second.Cancel(_userA, DateTimeOffset.UtcNow);
        await AddAsync(first);
        await AddAsync(second);
        await AddAsync(Realization(planned));

        await using var db = Db();
        Assert.Equal((3, 1), (
            await db.FinancialTransactions.CountAsync(t => t.PlannedTransactionId == planned.Id),
            await db.FinancialTransactions.CountAsync(t => t.PlannedTransactionId == planned.Id && !t.IsCancelled)));
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task ReactivatingAnOldRealization_WhileAnotherIsActive_IsRejected()
    {
        var planned = await AddPlannedAsync();
        var old = Realization(planned);
        old.Cancel(_userA, DateTimeOffset.UtcNow);
        await AddAsync(old);
        await AddAsync(Realization(planned));

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE financial_transactions SET is_cancelled = FALSE, cancelled_at = NULL, cancelled_by_user_id = NULL WHERE id = {old.Id}"));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, ActiveIndex), (exception.SqlState, exception.ConstraintName));
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task LinkToPlannedOfAnotherFamily_ViolatesCompositeForeignKey()
    {
        var foreignPlanned = FinancialPlannedTransaction.Create(_familyB.Id,
            PlannedTransactionDetails.TryCreate(_accountB.Id, _expenseB.Id, "Expense", "Aluguel", 10m, new DateOnly(2026, 9, 5), out _)!,
            _userB, DateTimeOffset.UtcNow);
        await using (var db = Db())
        {
            db.FinancialPlannedTransactions.Add(foreignPlanned);
            await db.SaveChangesAsync();
        }

        // Lançamento da família A apontando para previsão da família B.
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(FinancialTransaction.CreateRealization(
            _familyA.Id, Details(), foreignPlanned.Id, _userA, DateTimeOffset.UtcNow)));

        var postgresError = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal((PostgresErrorCodes.ForeignKeyViolation, "fk_financial_transactions_planned_same_family"),
            (postgresError.SqlState, postgresError.ConstraintName));
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task PlannedWithRealizations_CannotBeDeletedByCascade()
    {
        var planned = await AddPlannedAsync();
        await AddAsync(Realization(planned));

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM financial_planned_transactions WHERE id = {planned.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal(1, await db.FinancialTransactions.CountAsync());
    }

    [Trait("Category", "INFRA")]
    [Fact]
    public async Task Migration_KeepsExistingTransactionsUnlinked()
    {
        var transactionId = Guid.CreateVersion7();
        var plannedId = Guid.CreateVersion7();
        await using (var db = Db())
        {
            await HistoricalFinancialSchema.PrepareAsync(db, PreviousMigration);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_transactions (id, family_id, account_id, category_id, type, description, amount, transaction_date, currency, created_at, created_by_user_id, is_cancelled)
                VALUES ({transactionId}, {_familyA.Id}, {_accountA.Id}, {_expenseA.Id}, 'Expense', 'Compra', 12.34, DATE '2026-09-02', 'BRL', now(), {_userA}, FALSE)
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_planned_transactions (id, family_id, account_id, category_id, type, description, amount, due_date, currency, created_at, created_by_user_id, is_cancelled)
                VALUES ({plannedId}, {_familyA.Id}, {_accountA.Id}, {_expenseA.Id}, 'Expense', 'Aluguel', 200.00, DATE '2026-09-05', 'BRL', now(), {_userA}, FALSE)
                """);
            await db.Database.MigrateAsync();
        }

        await using var verify = Db();
        var transaction = await verify.FinancialTransactions.SingleAsync(t => t.Id == transactionId);
        Assert.Equal((12.34m, (Guid?)null), (transaction.Amount, transaction.PlannedTransactionId));
        Assert.Equal(200.00m, (await verify.FinancialPlannedTransactions.SingleAsync(p => p.Id == plannedId)).Amount);
    }


    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private static FinancialAccount Account(Guid workspaceId, Guid userId) =>
        FinancialAccount.Create(
            workspaceId,
            AccountDetails.TryCreate("Conta", null, nameof(AccountType.Checking), nameof(FinancialScope.PF), null, null, null, out _)!,
            userId,
            DateTimeOffset.UtcNow);

    private async Task<FinancialPlannedTransaction> AddPlannedAsync()
    {
        var planned = FinancialPlannedTransaction.Create(_familyA.Id,
            PlannedTransactionDetails.TryCreate(_accountA.Id, _expenseA.Id, "Expense", "Aluguel", 200m, new DateOnly(2026, 9, 5), out _)!,
            _userA, DateTimeOffset.UtcNow);
        await using var db = Db();
        db.FinancialPlannedTransactions.Add(planned);
        await db.SaveChangesAsync();
        return planned;
    }

    private TransactionDetails Details() =>
        TransactionDetails.TryCreate(_accountA.Id, _expenseA.Id, "Expense", "Aluguel", 210m, new DateOnly(2026, 9, 10), new DateOnly(2030, 1, 1), out _)!;

    private FinancialTransaction Realization(FinancialPlannedTransaction planned) =>
        FinancialTransaction.CreateRealization(_familyA.Id, Details(), planned.Id, _userA, DateTimeOffset.UtcNow);

    private async Task AddAsync(FinancialTransaction transaction)
    {
        await using var db = Db();
        db.FinancialTransactions.Add(transaction);
        await db.SaveChangesAsync();
    }
}

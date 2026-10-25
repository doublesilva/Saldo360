using Saldo360.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Families;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Persistence.Configurations;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class FinancialBudgetPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string PreviousMigration = "20261012215544_AddPlannedTransactionRealization";
    private string _connection = null!;
    private Guid _user;
    private FinancialWorkspace _familyA = null!;
    private FinancialWorkspace _familyB = null!;
    private FinancialCategory _expenseA = null!;
    private FinancialCategory _expenseB = null!;

    public async Task InitializeAsync()
    {
        _connection = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        var users = new UserDirectory(db, TimeProvider.System);
        _user = await users.ResolveAsync("https://auth.test.invalid", "budget-persistence-a", "A", CancellationToken.None);
        var userB = await users.ResolveAsync("https://auth.test.invalid", "budget-persistence-b", "B", CancellationToken.None);
        _familyA = FinancialWorkspace.CreatePersonal(_user, DateTimeOffset.UtcNow);
        _familyB = FinancialWorkspace.CreatePersonal(userB, DateTimeOffset.UtcNow);
        var families = new FinancialWorkspaceRepository(db);
        await families.AddAsync(_familyA, CancellationToken.None);
        await families.AddAsync(_familyB, CancellationToken.None);
        _expenseA = FinancialCategory.Create(_familyA.Id, "Moradia", CategoryType.Expense, _user, DateTimeOffset.UtcNow);
        _expenseB = FinancialCategory.Create(_familyB.Id, "Outra", CategoryType.Expense, userB, DateTimeOffset.UtcNow);
        db.FinancialCategories.AddRange(_expenseA, _expenseB);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Trait("Category", "CORE")]
    [Fact]
    public async Task DatabaseEnforcesOneBudgetPerFamilyAndCompetenceIncludingArchived()
    {
        var first = Budget(_familyA.Id);
        first.Archive(_user, DateTimeOffset.UtcNow);
        await AddBudgetAsync(first);
        var exception = await AssertUniqueAsync(() => AddBudgetAsync(Budget(_familyA.Id)));
        Assert.Equal(FinancialBudgetConfiguration.UniqueCompetenceIndex, exception.ConstraintName);
        await AddBudgetAsync(Budget(_familyB.Id));
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task DatabaseEnforcesUniqueGroupingAndSameFamilyExpenseCategory()
    {
        var budget = Budget(_familyA.Id);
        await AddBudgetAsync(budget);
        await AddTargetAsync(Target(budget, _expenseA.Id));
        var duplicate = await AssertUniqueAsync(() => AddTargetAsync(Target(budget, _expenseA.Id)));
        Assert.Equal(FinancialBudgetTargetConfiguration.UniqueGroupingIndex, duplicate.ConstraintName);

        var foreign = await AssertPostgresAsync(() => AddTargetAsync(Target(budget, _expenseB.Id)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);

        var income = FinancialCategory.Create(_familyA.Id, "Receita", CategoryType.Income, _user, DateTimeOffset.UtcNow);
        await using (var db = Db()) { db.FinancialCategories.Add(income); await db.SaveChangesAsync(); }
        var wrongType = await AssertPostgresAsync(() => AddTargetAsync(Target(budget, income.Id)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, wrongType.SqlState);
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("-0.01", "23514")]
    [InlineData("1.001", "23514")]
    [InlineData("10000000000000.00", "23514")]
    public async Task DatabaseRejectsInvalidMonetaryValues(string value, string sqlState)
    {
        var budget = Budget(_familyA.Id);
        await AddBudgetAsync(budget);
        var target = Target(budget, _expenseA.Id);
        await AddTargetAsync(target);
        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE financial_budget_targets SET planned_amount = @value WHERE id = @id",
            new NpgsqlParameter("value", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
            new NpgsqlParameter("id", target.Id)));
        Assert.Equal(sqlState, exception.SqlState);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task HistoricalReferencesAreRestrictedAndArchiveDoesNotDeleteRows()
    {
        var budget = Budget(_familyA.Id);
        await AddBudgetAsync(budget);
        var target = Target(budget, _expenseA.Id);
        target.Archive(_user, DateTimeOffset.UtcNow);
        await AddTargetAsync(target);

        await using var db = Db();
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM financial_categories WHERE id = {_expenseA.Id}"))).SqlState);
        await using var verify = Db();
        Assert.Equal(1, await verify.FinancialBudgets.CountAsync());
        Assert.Equal(1, await verify.FinancialBudgetTargets.CountAsync());
    }

    [Trait("Category", "INFRA")]
    [Fact]
    public async Task MigrationPreservesExistingFamiliesAndCategories()
    {
        await using (var db = Db())
        {
            await HistoricalFinancialSchema.PrepareAsync(db, PreviousMigration);
            await db.Database.MigrateAsync();
        }

        await using var verify = Db();
        Assert.True(await verify.Families.AnyAsync(f => f.Id == _familyA.Id));
        Assert.True(await verify.FinancialCategories.AnyAsync(c => c.Id == _expenseA.Id));
        Assert.Empty(await verify.FinancialBudgets.ToListAsync());
        Assert.Empty(await verify.FinancialBudgetTargets.ToListAsync());
    }

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connection);
    private FinancialBudget Budget(Guid workspaceId) => FinancialBudget.Create(
        workspaceId, new DateOnly(2026, 10, 1), null, _user, DateTimeOffset.UtcNow);
    private FinancialBudgetTarget Target(FinancialBudget budget, Guid categoryId) => FinancialBudgetTarget.Create(
        budget.WorkspaceId, budget.Id, "Meta", 100m, categoryId, _user, DateTimeOffset.UtcNow);

    private async Task AddBudgetAsync(FinancialBudget budget)
    {
        await using var db = Db(); db.FinancialBudgets.Add(budget); await db.SaveChangesAsync();
    }

    private async Task AddTargetAsync(FinancialBudgetTarget target)
    {
        await using var db = Db(); db.FinancialBudgetTargets.Add(target); await db.SaveChangesAsync();
    }

    private static async Task<PostgresException> AssertUniqueAsync(Func<Task> action)
    {
        var error = await AssertPostgresAsync(action);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        return error;
    }

    private static async Task<PostgresException> AssertPostgresAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(action);
        return Assert.IsType<PostgresException>(exception.InnerException);
    }
}

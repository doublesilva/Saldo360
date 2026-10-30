using Saldo360.Domain.Workspaces;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Families;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Budgets;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Budgets")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialBudgetExecutionPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateOnly Competence = new(2026, 10, 1);
    private string _connection = null!;
    private Guid _user;
    private FinancialWorkspace _family = null!;
    private FinancialAccount _account = null!;
    private FinancialCategory _category = null!;
    private FinancialBudget _budget = null!;
    private FinancialBudgetTarget _target = null!;

    public async Task InitializeAsync()
    {
        _connection = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        _user = await new UserDirectory(db, TimeProvider.System)
            .ResolveAsync("https://auth.test.invalid", "budget-execution", "A", CancellationToken.None);
        _family = FinancialWorkspace.CreatePersonal(_user, DateTimeOffset.UtcNow);
        await new FinancialWorkspaceRepository(db).AddAsync(_family, CancellationToken.None);
        _account = FinancialAccount.Create(_family.Id,
            AccountDetails.TryCreate("Conta", null, nameof(AccountType.Checking), nameof(FinancialScope.PF), null,
                0m, Competence, out _ )!, _user, DateTimeOffset.UtcNow);
        _category = FinancialCategory.Create(_family.Id, "Moradia", CategoryType.Expense, _user, DateTimeOffset.UtcNow);
        _budget = FinancialBudget.Create(_family.Id, Competence, null, _user, DateTimeOffset.UtcNow);
        _target = FinancialBudgetTarget.Create(_family.Id, _budget.Id, "Moradia", 1000m, _category.Id, _user, DateTimeOffset.UtcNow);
        db.AddRange(_account, _category, _budget, _target);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task QueryUsesReadOnlyRepeatableReadAndDatabaseAggregationsWithoutNPlusOne()
    {
        await using (var db = Db())
        {
            for (var i = 0; i < 12; i++)
                db.Add(Planned(0.10m, Competence.AddDays(i)));
            for (var i = 0; i < 8; i++)
                db.Add(Transaction(0.20m, Competence.AddDays(i)));
            await db.SaveChangesAsync();
        }

        var capture = new Capture();
        await using var context = PostgresFixture.CreateDbContext(_connection, capture);
        var result = await new FinancialBudgetExecutionRepository(context)
            .GetAsync(_family.Id, _budget.Id, CancellationToken.None);

        Assert.Equal(IsolationLevel.RepeatableRead, Assert.Single(capture.IsolationLevels));
        Assert.Contains(capture.Commands, command => command.Contains("SET TRANSACTION READ ONLY", StringComparison.OrdinalIgnoreCase));
        // R03: uma leitura por origem (despesas, previsões, dívidas, cartões e ajustes) mais metas, subcategorias e cartões, todas em lote sobre a mesma lista de
        // registros. O total de consultas é constante: não cresce com o número de lançamentos (sem N+1), e o resumo e a composição usam essa mesma lista.
        int Selects(Capture c) => c.Commands.Count(command => command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        var baseline = Selects(capture);
        Assert.Equal(11, baseline);
        Assert.Empty(context.ChangeTracker.Entries());
        await using (var db = Db())
        {
            for (var i = 0; i < 40; i++) db.Add(Transaction(0.20m, Competence.AddDays(i % 20)));
            await db.SaveChangesAsync();
        }
        var again = new Capture();
        await using var second = PostgresFixture.CreateDbContext(_connection, again);
        await new FinancialBudgetExecutionRepository(second).GetAsync(_family.Id, _budget.Id, CancellationToken.None);
        Assert.Equal(baseline, Selects(again));
        var target = Assert.Single(result!.Targets);
        Assert.Equal((1.60m, 1.20m), (target.Realized, target.Committed));
    }

    [Fact]
    [Trait("Profile", "FAST")]
    public async Task ConcurrentRealizationCannotDisappearBetweenRealizedAndCommittedAggregates()
    {
        var planned = Planned(200m, Competence.AddDays(4));
        await using (var db = Db())
        {
            db.Add(planned);
            await db.SaveChangesAsync();
        }

        var interceptor = new RealizeAfterRealizedQuery(async () =>
        {
            await using var writer = Db();
            writer.Add(Realization(planned, 210m));
            await writer.SaveChangesAsync();
        });
        await using (var context = PostgresFixture.CreateDbContext(_connection, interceptor))
        {
            var during = await new FinancialBudgetExecutionRepository(context)
                .GetAsync(_family.Id, _budget.Id, CancellationToken.None);
            var target = Assert.Single(during!.Targets);
            Assert.Equal((0m, 200m), (target.Realized, target.Committed));
        }

        await using var afterContext = Db();
        var after = await new FinancialBudgetExecutionRepository(afterContext)
            .GetAsync(_family.Id, _budget.Id, CancellationToken.None);
        var afterTarget = Assert.Single(after!.Targets);
        Assert.Equal((210m, 0m), (afterTarget.Realized, afterTarget.Committed));
    }

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connection);

    private FinancialPlannedTransaction Planned(decimal amount, DateOnly date) => FinancialPlannedTransaction.Create(
        _family.Id,
        PlannedTransactionDetails.TryCreate(_account.Id, _category.Id, "Expense", "Prevista", amount, date, out _)!,
        _user, DateTimeOffset.UtcNow);

    private FinancialTransaction Transaction(decimal amount, DateOnly date) => FinancialTransaction.Create(
        _family.Id,
        TransactionDetails.TryCreate(_account.Id, _category.Id, "Expense", "Efetiva", amount, date,
            new DateOnly(2030, 1, 1), out _)!,
        _user, DateTimeOffset.UtcNow);

    private FinancialTransaction Realization(FinancialPlannedTransaction planned, decimal amount) =>
        FinancialTransaction.CreateRealization(_family.Id,
            planned.ToRealizationDetails(amount, Competence.AddDays(5), new DateOnly(2030, 1, 1), out _)!,
            planned.Id, _user, DateTimeOffset.UtcNow);

    private sealed class Capture : IDbCommandInterceptor, IDbTransactionInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<IsolationLevel> IsolationLevels { get; } = [];

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            IsolationLevels.Add(eventData.IsolationLevel);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RealizeAfterRealizedQuery(Func<Task> realize) : DbCommandInterceptor
    {
        private bool _done;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!_done
                && command.CommandText.Contains("financial_transactions", StringComparison.Ordinal)
                && !command.CommandText.Contains("financial_planned_transactions", StringComparison.Ordinal))
            {
                _done = true;
                await realize();
            }
            return result;
        }
    }
}

using Saldo360.Domain.Workspaces;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Families;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Accounts;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>Projeção no PostgreSQL real: agregação das previsões pendentes e leitura num único snapshot.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
public sealed class AccountProjectionPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateOnly Reference = new(2026, 9, 1);
    private static readonly DateOnly BaseDate = new(2026, 10, 12);

    private string _connectionString = null!;
    private Guid _user;
    private FinancialWorkspace _family = null!;
    private FinancialAccount _account = null!;
    private FinancialAccount _other = null!;
    private FinancialCategory _income = null!;
    private FinancialCategory _expense = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        _user = await new UserDirectory(db, TimeProvider.System).ResolveAsync("https://auth.test.invalid", "a", "A", CancellationToken.None);
        _family = FinancialWorkspace.CreatePersonal(_user, DateTimeOffset.UtcNow);
        await new FinancialWorkspaceRepository(db).AddAsync(_family, CancellationToken.None);
        _account = Account("Itaú");
        _other = Account("Nubank");
        _income = FinancialCategory.Create(_family.Id, "Salário", CategoryType.Income, _user, DateTimeOffset.UtcNow);
        _expense = FinancialCategory.Create(_family.Id, "Aluguel", CategoryType.Expense, _user, DateTimeOffset.UtcNow);
        db.AddRange(_account, _other, _income, _expense);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PendingSums_SplitOverdue_ExcludeCancelledRealizedLaterAndOtherAccounts()
    {
        var realized = Planned(_expense, 200m, BaseDate.AddDays(3));
        var cancelled = Planned(_expense, 999m, BaseDate.AddDays(1));
        cancelled.Cancel(_user, DateTimeOffset.UtcNow);
        await using (var db = Db())
        {
            db.FinancialPlannedTransactions.AddRange(
                Planned(_income, 0.10m, BaseDate.AddDays(-1)), // vencida
                Planned(_income, 0.20m, BaseDate), // hoje: período
                Planned(_expense, 5m, Reference.AddDays(-30)), // vencida, antes da referência
                Planned(_income, 7m, BaseDate.AddDays(10)), // até a data-alvo
                Planned(_income, 1000m, BaseDate.AddDays(11)), // depois da data-alvo
                Planned(_expense, 50m, BaseDate, _other), // outra conta
                realized,
                cancelled);
            await db.SaveChangesAsync();
            db.FinancialTransactions.Add(Realization(realized, 210m));
            await db.SaveChangesAsync();
        }

        await using var context = Db();
        var totals = await new FinancialAccountRepository(context)
            .SumPendingPlannedAsync(_family.Id, _account.Id, BaseDate, BaseDate.AddDays(10), CancellationToken.None);

        Assert.Equal((0.10m, 5m, 7.20m, 0m), (totals.OverdueIncome, totals.OverdueExpense, totals.PlannedIncome, totals.PlannedExpense));
    }

    [Fact]
    public async Task CancellingTheRealization_MakesThePlannedPendingAgain()
    {
        var planned = Planned(_expense, 200m, BaseDate.AddDays(3));
        var realization = Realization(planned, 210m);
        await using (var db = Db())
        {
            db.FinancialPlannedTransactions.Add(planned);
            await db.SaveChangesAsync();
            db.FinancialTransactions.Add(realization);
            await db.SaveChangesAsync();
        }

        await using var context = Db();
        var repository = new FinancialAccountRepository(context);
        var before = await repository.SumPendingPlannedAsync(_family.Id, _account.Id, BaseDate, BaseDate.AddDays(5), CancellationToken.None);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE financial_transactions SET is_cancelled = TRUE, cancelled_at = now(), cancelled_by_user_id = {_user} WHERE id = {realization.Id}");
        var after = await repository.SumPendingPlannedAsync(_family.Id, _account.Id, BaseDate, BaseDate.AddDays(5), CancellationToken.None);

        Assert.Equal((0m, 200m), (before.PlannedExpense, after.PlannedExpense));
    }

    [Fact]
    public async Task ConsistentRead_SeesOneSnapshot_EvenIfARealizationCommitsInTheMiddle()
    {
        var planned = Planned(_expense, 200m, BaseDate.AddDays(3));
        await using (var db = Db())
        {
            db.FinancialPlannedTransactions.Add(planned);
            await db.SaveChangesAsync();
        }

        await using var context = Db();
        var repository = new FinancialAccountRepository(context);

        var (pendingBefore, movements, pendingAfter) = await repository.ReadConsistentAsync(async () =>
        {
            var first = await repository.SumPendingPlannedAsync(_family.Id, _account.Id, BaseDate, BaseDate.AddDays(5), CancellationToken.None);

            // Outra conexão realiza a previsão e confirma no meio da leitura.
            await using (var writer = Db())
            {
                writer.FinancialTransactions.Add(Realization(planned, 210m));
                await writer.SaveChangesAsync();
            }

            var sums = await repository.SumMovementsAsync(_family.Id, _account.Id, Reference, BaseDate.AddDays(5), CancellationToken.None);
            var second = await repository.SumPendingPlannedAsync(_family.Id, _account.Id, BaseDate, BaseDate.AddDays(5), CancellationToken.None);
            return (first, sums, second);
        }, CancellationToken.None);

        // Mesmo snapshot do início: a previsão continua pendente e o lançamento efetivo ainda não existe.
        Assert.Equal((200m, 0m, 200m), (pendingBefore.PlannedExpense, movements.Expense, pendingAfter.PlannedExpense));

        // Depois da leitura, o estado novo aparece inteiro: lançamento contado, previsão fora.
        var now = await repository.ReadConsistentAsync(async () => (
            await repository.SumMovementsAsync(_family.Id, _account.Id, Reference, BaseDate.AddDays(5), CancellationToken.None),
            await repository.SumPendingPlannedAsync(_family.Id, _account.Id, BaseDate, BaseDate.AddDays(5), CancellationToken.None)),
            CancellationToken.None);
        Assert.Equal((210m, 0m), (now.Item1.Expense, now.Item2.PlannedExpense));
    }

    [Fact]
    public async Task ConsistentRead_UsesReadOnlyRepeatableRead_AndAggregatesInTheDatabase()
    {
        await using (var db = Db())
        {
            for (var i = 0; i < 15; i++)
            {
                db.FinancialPlannedTransactions.Add(Planned(i % 2 == 0 ? _income : _expense, 1.10m, BaseDate.AddDays(i % 4 - 1)));
            }

            await db.SaveChangesAsync();
        }

        var capture = new Capture();
        await using var context = PostgresFixture.CreateDbContext(_connectionString, capture);
        var repository = new FinancialAccountRepository(context);

        var totals = await repository.ReadConsistentAsync(
            () => repository.SumPendingPlannedAsync(_family.Id, _account.Id, BaseDate, BaseDate.AddDays(5), CancellationToken.None),
            CancellationToken.None);

        Assert.Equal(IsolationLevel.RepeatableRead, Assert.Single(capture.IsolationLevels));
        Assert.Contains(capture.Commands, sql => sql.Contains("READ ONLY", StringComparison.OrdinalIgnoreCase));
        var sql = Assert.Single(capture.Commands, c => c.Contains("financial_planned_transactions", StringComparison.Ordinal));
        Assert.Contains("sum(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("description", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(8 * 1.10m + 7 * 1.10m, totals.OverdueIncome + totals.OverdueExpense + totals.PlannedIncome + totals.PlannedExpense);
    }

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private FinancialAccount Account(string name) => FinancialAccount.Create(
        _family.Id,
        AccountDetails.TryCreate(name, null, nameof(AccountType.Checking), nameof(FinancialScope.PF), null, 1000m, Reference, out _)!,
        _user,
        DateTimeOffset.UtcNow);

    private FinancialPlannedTransaction Planned(FinancialCategory category, decimal amount, DateOnly dueDate, FinancialAccount? account = null) =>
        FinancialPlannedTransaction.Create(
            _family.Id,
            PlannedTransactionDetails.TryCreate((account ?? _account).Id, category.Id, category.Type.ToString(), "Previsão", amount, dueDate, out _)!,
            _user,
            DateTimeOffset.UtcNow);

    private FinancialTransaction Realization(FinancialPlannedTransaction planned, decimal amount) =>
        FinancialTransaction.CreateRealization(
            _family.Id,
            planned.ToRealizationDetails(amount, BaseDate, new DateOnly(2030, 1, 1), out _)!,
            planned.Id,
            _user,
            DateTimeOffset.UtcNow);

    /// <summary>Guarda o SQL dos comandos e o nível de isolamento das transações abertas.</summary>
    private sealed class Capture : IDbCommandInterceptor, IDbTransactionInterceptor
    {
        public List<string> Commands { get; } = [];

        public List<IsolationLevel> IsolationLevels { get; } = [];

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
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
}

using Saldo360.Domain.Workspaces;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Families;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Transfers;
using Saldo360.Infrastructure.Accounts;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>Agregação do saldo no PostgreSQL: uma consulta com SUM, sem carregar lançamentos, em numeric exato.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
public sealed class AccountBalancePersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateOnly Reference = new(2026, 9, 1);

    private string _connectionString = null!;
    private Guid _user;
    private FinancialWorkspace _family = null!;
    private FinancialAccount _account = null!;
    private FinancialAccount _otherAccount = null!;
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
        _otherAccount = Account("Nubank");
        _income = FinancialCategory.Create(_family.Id, "Salário", CategoryType.Income, _user, DateTimeOffset.UtcNow);
        _expense = FinancialCategory.Create(_family.Id, "Mercado", CategoryType.Expense, _user, DateTimeOffset.UtcNow);
        db.AddRange(_account, _otherAccount, _income, _expense);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sum_RunsAsOneAggregateQueryPerTable_WithoutLoadingMovements()
    {
        await using (var db = Db())
        {
            for (var i = 0; i < 20; i++)
            {
                db.FinancialTransactions.Add(Transaction(_account, i % 2 == 0 ? _income : _expense, 1.25m, Reference));
                db.FinancialTransfers.Add(Transfer(i % 2 == 0 ? _otherAccount : _account, i % 2 == 0 ? _account : _otherAccount, 2.50m, Reference));
            }

            await db.SaveChangesAsync();
        }

        var capture = new CommandCapture();
        await using var context = PostgresFixture.CreateDbContext(_connectionString, capture);

        var totals = await new FinancialAccountRepository(context)
            .SumMovementsAsync(_family.Id, _account.Id, Reference, Reference, CancellationToken.None);

        Assert.Equal((12.50m, 12.50m, 25.00m, 25.00m), (totals.Income, totals.Expense, totals.TransferIn, totals.TransferOut));
        Assert.Equal(2, capture.Commands.Count);
        Assert.Contains(capture.Commands, sql => sql.Contains("financial_transactions", StringComparison.Ordinal));
        Assert.Contains(capture.Commands, sql => sql.Contains("financial_transfers", StringComparison.Ordinal));
        Assert.All(capture.Commands, sql =>
        {
            Assert.Contains("sum(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("description", sql, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task TransferSums_UseThisAccountsPeriod_IgnoreCancelled_AndAreExact()
    {
        await using (var db = Db())
        {
            var cancelled = Transfer(_otherAccount, _account, 1000m, Reference);
            cancelled.Cancel(_user, DateTimeOffset.UtcNow);
            db.FinancialTransfers.AddRange(
                Transfer(_otherAccount, _account, 0.10m, Reference),
                Transfer(_otherAccount, _account, 0.20m, Reference.AddDays(1)),
                Transfer(_account, _otherAccount, 0.05m, Reference.AddDays(1)),
                Transfer(_otherAccount, _account, 7m, Reference.AddDays(-1)), // antes do período desta conta
                Transfer(_account, _otherAccount, 9m, Reference.AddDays(2)), // depois do período
                cancelled);
            await db.SaveChangesAsync();
        }

        await using var context = Db();
        var totals = await new FinancialAccountRepository(context)
            .SumMovementsAsync(_family.Id, _account.Id, Reference, Reference.AddDays(1), CancellationToken.None);

        Assert.Equal(("0.30", "0.05", "0.00", "0.00"),
            (Money.Format(totals.TransferIn), Money.Format(totals.TransferOut), Money.Format(totals.Income), Money.Format(totals.Expense)));
    }

    [Fact]
    public async Task Sum_IsExact_FiltersPeriodAccountAndCancelled_AndIsZeroWhenEmpty()
    {
        await using (var db = Db())
        {
            var cancelled = Transaction(_account, _expense, 1000m, Reference);
            cancelled.Cancel(_user, DateTimeOffset.UtcNow);
            db.FinancialTransactions.AddRange(
                Transaction(_account, _income, 0.10m, Reference),
                Transaction(_account, _income, 0.20m, Reference.AddDays(1)),
                Transaction(_account, _expense, 0.05m, Reference.AddDays(1)),
                Transaction(_account, _income, 7m, Reference.AddDays(-1)), // antes do período
                Transaction(_account, _income, 9m, Reference.AddDays(2)), // depois do período
                Transaction(_otherAccount, _income, 50m, Reference), // outra conta
                cancelled);
            await db.SaveChangesAsync();
        }

        await using var context = Db();
        var repository = new FinancialAccountRepository(context);

        var totals = await repository.SumMovementsAsync(_family.Id, _account.Id, Reference, Reference.AddDays(1), CancellationToken.None);
        var empty = await repository.SumMovementsAsync(_family.Id, _account.Id, Reference.AddDays(10), Reference.AddDays(20), CancellationToken.None);
        var otherFamily = await repository.SumMovementsAsync(Guid.CreateVersion7(), _account.Id, Reference, Reference.AddDays(1), CancellationToken.None);

        Assert.Equal(("0.30", "0.05"), (Money.Format(totals.Income), Money.Format(totals.Expense)));
        Assert.Equal((0m, 0m), (empty.Income, empty.Expense));
        Assert.Equal((0m, 0m), (otherFamily.Income, otherFamily.Expense));
    }



    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private FinancialAccount Account(string name) => FinancialAccount.Create(
        _family.Id,
        AccountDetails.TryCreate(name, null, nameof(AccountType.Checking), nameof(FinancialScope.PF), null, 0m, Reference, out _)!,
        _user,
        DateTimeOffset.UtcNow);

    private FinancialTransaction Transaction(FinancialAccount account, FinancialCategory category, decimal amount, DateOnly date) =>
        FinancialTransaction.Create(
            _family.Id,
            TransactionDetails.TryCreate(account.Id, category.Id, category.Type.ToString(), "Lançamento", amount, date, new DateOnly(2030, 1, 1), out _)!,
            _user,
            DateTimeOffset.UtcNow);

    private FinancialTransfer Transfer(FinancialAccount source, FinancialAccount destination, decimal amount, DateOnly date) =>
        FinancialTransfer.Create(
            _family.Id,
            TransferDetails.TryCreate(source.Id, destination.Id, amount, date, "Transferência", new DateOnly(2030, 1, 1), out _)!,
            _user,
            DateTimeOffset.UtcNow);

    /// <summary>Guarda o SQL de cada comando de leitura executado.</summary>
    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

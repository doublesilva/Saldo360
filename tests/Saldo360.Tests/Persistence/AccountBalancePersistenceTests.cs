using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Families;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Accounts;
using Saldo360.Infrastructure.Families;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>Agregação do saldo no PostgreSQL: uma consulta com SUM, sem carregar lançamentos, em numeric exato.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountBalancePersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateOnly Reference = new(2026, 9, 1);

    private string _connectionString = null!;
    private Guid _user;
    private Family _family = null!;
    private FinancialAccount _account = null!;
    private FinancialAccount _otherAccount = null!;
    private FinancialCategory _income = null!;
    private FinancialCategory _expense = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        _user = await new UserDirectory(db, TimeProvider.System).ResolveAsync("https://auth.test.invalid", "a", "A", CancellationToken.None);
        _family = Family.Create("Família", _user, DateTimeOffset.UtcNow);
        await new FamilyRepository(db).AddAsync(_family, CancellationToken.None);
        _account = Account("Itaú");
        _otherAccount = Account("Nubank");
        _income = FinancialCategory.Create(_family.Id, "Salário", CategoryType.Income, _user, DateTimeOffset.UtcNow);
        _expense = FinancialCategory.Create(_family.Id, "Mercado", CategoryType.Expense, _user, DateTimeOffset.UtcNow);
        db.AddRange(_account, _otherAccount, _income, _expense);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sum_RunsAsSingleAggregateQuery_WithoutLoadingTransactions()
    {
        await using (var db = Db())
        {
            for (var i = 0; i < 20; i++)
            {
                db.FinancialTransactions.Add(Transaction(_account, i % 2 == 0 ? _income : _expense, 1.25m, Reference));
            }

            await db.SaveChangesAsync();
        }

        var capture = new CommandCapture();
        await using var context = PostgresFixture.CreateDbContext(_connectionString, capture);

        var totals = await new FinancialAccountRepository(context)
            .SumTransactionsAsync(_family.Id, _account.Id, Reference, Reference, CancellationToken.None);

        Assert.Equal((12.50m, 12.50m), (totals.Income, totals.Expense));
        var sql = Assert.Single(capture.Commands);
        Assert.Contains("sum(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("description", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.ChangeTracker.Entries<FinancialTransaction>());
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

        var totals = await repository.SumTransactionsAsync(_family.Id, _account.Id, Reference, Reference.AddDays(1), CancellationToken.None);
        var empty = await repository.SumTransactionsAsync(_family.Id, _account.Id, Reference.AddDays(10), Reference.AddDays(20), CancellationToken.None);
        var otherFamily = await repository.SumTransactionsAsync(Guid.CreateVersion7(), _account.Id, Reference, Reference.AddDays(1), CancellationToken.None);

        Assert.Equal(("0.30", "0.05"), (Money.Format(totals.Income), Money.Format(totals.Expense)));
        Assert.Equal((0m, 0m), (empty.Income, empty.Expense));
        Assert.Equal((0m, 0m), (otherFamily.Income, otherFamily.Expense));
    }

    [Theory]
    [InlineData("1300", "1300.00")]
    [InlineData("-0.3", "-0.30")]
    [InlineData("29999999999999.97", "29999999999999.97")]
    public void MoneyFormat_IsExactWithTwoDecimals(string value, string expected)
    {
        Assert.Equal(expected, Money.Format(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void MoneyFormat_RefusesToRoundFractionalCents()
    {
        Assert.Throws<ArgumentException>(() => Money.Format(0.005m));
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

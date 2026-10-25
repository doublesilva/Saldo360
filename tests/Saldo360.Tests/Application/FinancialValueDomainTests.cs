using System.Globalization;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Transfers;

namespace Saldo360.Tests.Application;

// These contracts are pure domain behavior: no API host, PostgreSQL fixture or database clone.

[Trait("Category", "CORE")]
public sealed class MoneyDomainTests
{
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


}

[Trait("Category", "CORE")]
public sealed class FinancialAccountDomainTests
{
    [Theory]
    [InlineData("1234.5", null, "openingBalanceDate")]
    [InlineData(null, "2026-10-01", "openingBalance")]
    [InlineData("0.001", "2026-10-01", "openingBalance")]
    [InlineData("1.500", "2026-10-01", "openingBalance")]
    public void Domain_RejectsIncompleteOrImpreciseOpeningBalance(string? amount, string? date, string field)
    {
        var details = AccountDetails.TryCreate(
            "Conta", null, nameof(AccountType.Cash), nameof(FinancialScope.PF), null,
            amount is null ? null : decimal.Parse(amount, CultureInfo.InvariantCulture),
            date is null ? null : DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            out var errors);

        Assert.Null(details);
        Assert.Equal([field], errors.Keys);
    }


}

[Trait("Category", "CORE")]
public sealed class FinancialTransactionDomainTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid CategoryId = Guid.NewGuid();

    [Theory]
    [InlineData("2026-10-11T01:00:00Z", "2026-10-10")] // 22:00 de 10/10 em Brasília
    [InlineData("2026-10-11T03:00:00Z", "2026-10-11")] // meia-noite em Brasília
    [InlineData("2026-10-10T23:59:59-03:00", "2026-10-10")]
    public void BusinessCalendar_UsesSaoPauloCivilDate(string instant, string expected)
    {
        var today = BusinessCalendar.Today(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture));

        Assert.Equal(DateOnly.ParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture), today);
    }

    [Theory]
    [InlineData("0", "amount")]
    [InlineData("-5", "amount")]
    [InlineData("1.001", "amount")]
    [InlineData("10000000000000.00", "amount")]
    public void Domain_RejectsInvalidAmounts(string amount, string field)
    {
        var details = TransactionDetails.TryCreate(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Expense", "Compra",
            decimal.Parse(amount, CultureInfo.InvariantCulture), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), out var errors);

        Assert.Null(details);
        Assert.Equal([field], errors.Keys);
    }

    [Fact]
    public void Domain_RejectsFutureDate_AndAcceptsTodayAndPast()
    {
        var today = new DateOnly(2026, 10, 10);
        DateOnly[] dates = [today.AddDays(1), today, new DateOnly(2000, 1, 1)];

        var results = dates.Select(date => TransactionDetails.TryCreate(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Income", "X", 1m, date, today, out _) is not null);

        Assert.Equal([false, true, true], results);
    }

    [Fact]
    public void CancelledTransaction_CannotBeUpdated_AndCancelIsIdempotent()
    {
        var transaction = Transaction(AccountId, CategoryId, CategoryType.Expense);
        var now = DateTimeOffset.UtcNow;

        Assert.True(transaction.Cancel(UserId, now));
        Assert.False(transaction.Cancel(UserId, now.AddMinutes(5)));
        Assert.Equal(now, transaction.CancelledAt);
        Assert.Throws<InvalidOperationException>(() => transaction.Update(Details(AccountId, CategoryId, CategoryType.Expense, 1m), UserId, now));
    }


    private FinancialTransaction Transaction(Guid accountId, Guid categoryId, CategoryType type) =>
        FinancialTransaction.Create(WorkspaceId, Details(accountId, categoryId, type, 10m), UserId, DateTimeOffset.UtcNow);
    private static TransactionDetails Details(Guid accountId, Guid categoryId, CategoryType type, decimal amount) =>
        TransactionDetails.TryCreate(accountId, categoryId, type.ToString(), "Compra", amount, new(2026, 10, 1), new(2026, 10, 10), out _)!;
}

[Trait("Category", "CORE")]
public sealed class FinancialTransferDomainTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid SourceId = Guid.NewGuid();
    private static readonly Guid DestinationId = Guid.NewGuid();

    [Theory]
    [InlineData("0", "amount")]
    [InlineData("-1", "amount")]
    [InlineData("0.001", "amount")]
    public void Domain_RejectsInvalidAmounts(string amount, string field)
    {
        var details = TransferDetails.TryCreate(
            Guid.CreateVersion7(), Guid.CreateVersion7(),
            decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            new DateOnly(2026, 9, 1), "X", new DateOnly(2026, 10, 1), out var errors);

        Assert.Null(details);
        Assert.Equal([field], errors.Keys);
    }

    [Fact]
    public void Domain_RejectsSameAccountAndFutureDate_AndCancelIsIdempotent()
    {
        var id = Guid.CreateVersion7();
        var today = new DateOnly(2026, 10, 1);

        Assert.Null(TransferDetails.TryCreate(id, id, 1m, today, "X", today, out var same));
        Assert.Equal(["destinationAccountId"], same.Keys);
        Assert.Null(TransferDetails.TryCreate(id, Guid.CreateVersion7(), 1m, today.AddDays(1), "X", today, out var future));
        Assert.Equal(["transferDate"], future.Keys);

        var transfer = Transfer(SourceId, DestinationId);
        var now = DateTimeOffset.UtcNow;
        Assert.True(transfer.Cancel(UserId, now));
        Assert.False(transfer.Cancel(UserId, now.AddMinutes(1)));
        Assert.Equal(now, transfer.CancelledAt);
    }


    private FinancialTransfer Transfer(Guid source, Guid destination) =>
        FinancialTransfer.Create(WorkspaceId, TransferDetails.TryCreate(source, destination, 10m, new(2026, 9, 1), "Pró-labore", new(2026, 10, 1), out _)!, UserId, DateTimeOffset.UtcNow);
}

[Trait("Category", "CORE")]
public sealed class FinancialPlannedTransactionDomainTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid CategoryId = Guid.NewGuid();

    [Fact]
    public void Domain_AcceptsAnyDueDate_RejectsInvalidAmounts_AndCancelledCannotBeUpdated()
    {
        foreach (var date in new[] { new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31) })
        {
            Assert.NotNull(PlannedTransactionDetails.TryCreate(Guid.CreateVersion7(), Guid.CreateVersion7(), "Expense", "X", 1m, date, out _));
        }

        foreach (var amount in new[] { 0m, -1m, 0.001m })
        {
            Assert.Null(PlannedTransactionDetails.TryCreate(Guid.CreateVersion7(), Guid.CreateVersion7(), "Expense", "X", amount, new DateOnly(2026, 1, 1), out var errors));
            Assert.Equal(["amount"], errors.Keys);
        }

        var planned = Planned(AccountId, CategoryId, CategoryType.Expense);
        var now = DateTimeOffset.UtcNow;
        Assert.True(planned.Cancel(UserId, now));
        Assert.False(planned.Cancel(UserId, now.AddMinutes(1)));
        Assert.Equal(now, planned.CancelledAt);
        Assert.Throws<InvalidOperationException>(() => planned.Update(Details(AccountId, CategoryId, CategoryType.Expense, 2m, new DateOnly(2026, 1, 1)), UserId, now));
    }


    private FinancialPlannedTransaction Planned(Guid accountId, Guid categoryId, CategoryType type) =>
        FinancialPlannedTransaction.Create(WorkspaceId, Details(accountId, categoryId, type, 10m, new(2026, 12, 1)), UserId, DateTimeOffset.UtcNow);
    private static PlannedTransactionDetails Details(Guid accountId, Guid categoryId, CategoryType type, decimal amount, DateOnly dueDate) =>
        PlannedTransactionDetails.TryCreate(accountId, categoryId, type.ToString(), "Aluguel", amount, dueDate, out _)!;
}

[Trait("Category", "CORE")]
public sealed class PlannedRealizationDomainTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid CategoryId = Guid.NewGuid();

    [Fact]
    public void Domain_LinkedTransactionCannotBeUpdated_AndRealizationUsesThePlannedData()
    {
        var planned = FinancialPlannedTransaction.Create(WorkspaceId,
            PlannedTransactionDetails.TryCreate(AccountId, CategoryId, "Expense", "Aluguel", 200m, new DateOnly(2030, 1, 1), out _)!,
            UserId, DateTimeOffset.UtcNow);

        var details = planned.ToRealizationDetails(210m, new DateOnly(2026, 9, 10), new DateOnly(2026, 10, 1), out _)!;
        var future = planned.ToRealizationDetails(210m, new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1), out var errors);

        Assert.Equal((AccountId, CategoryId, CategoryType.Expense, "Aluguel", 210m), (details.AccountId, details.CategoryId, details.Type, details.Description, details.Amount));
        Assert.Null(future);
        Assert.Equal(["transactionDate"], errors.Keys);
        Assert.Equal(200m, planned.Amount); // previsto preservado

        var linked = FinancialTransaction.CreateRealization(WorkspaceId, details, planned.Id, UserId, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => linked.Update(details, UserId, DateTimeOffset.UtcNow));
    }


}

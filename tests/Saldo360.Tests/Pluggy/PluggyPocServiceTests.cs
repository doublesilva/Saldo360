using System.Text.Json;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.Pluggy;

[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggyPocServiceTests
{
    private const string ItemA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string ItemB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string BankA = "a0000000-0000-4000-8000-000000000001";
    private const string CardA = "a0000000-0000-4000-8000-000000000002";
    private const string BankB = "b0000000-0000-4000-8000-000000000001";

    private static readonly Guid WorkspaceA = Guid.NewGuid();
    private static readonly Guid WorkspaceB = Guid.NewGuid();
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public FakePluggyClient Client { get; } = new();
        public InMemoryPluggyLinks Links { get; } = new();
        public PluggyPocService Service { get; }

        public Fixture(bool enabled = true)
        {
            Service = new(new FakeWorkspaces((WorkspaceA, UserA), (WorkspaceB, UserB)), Links, Client, new PluggyOptions { Enabled = enabled, MaxTransactionPages = 5 }, new FixedTime());
            Client.Items[ItemA] = FakePluggyClient.Item(ItemA);
            Client.Items[ItemB] = FakePluggyClient.Item(ItemB, "Nubank");
            Client.AccountsByItem[ItemA] = [FakePluggyClient.Account(BankA, ItemA), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
            Client.AccountsByItem[ItemB] = [FakePluggyClient.Account(BankB, ItemB)];
        }

        public async Task<Guid> LinkAsync(Guid workspace, Guid user, string item)
        {
            var result = await Service.AddConnectionAsync(workspace, user, item, "Rótulo", default);
            Assert.Equal(PluggyAccess.Allowed, result.Access);
            return result.Value!.Id;
        }
    }

    [Fact]
    public async Task DisabledLaboratoryBehavesAsNonExistentForEveryOperation()
    {
        var f = new Fixture(enabled: false);
        Assert.Equal(PluggyAccess.NotFound, (await f.Service.ListConnectionsAsync(WorkspaceA, UserA, default)).Access);
        Assert.Equal(PluggyAccess.NotFound, (await f.Service.AddConnectionAsync(WorkspaceA, UserA, ItemA, null, default)).Access);
        Assert.Equal(PluggyAccess.NotFound, (await f.Service.ListAccountsAsync(WorkspaceA, UserA, Guid.NewGuid(), default)).Access);
        Assert.Equal(PluggyAccess.NotFound, (await f.Service.DiagnoseAsync(WorkspaceA, UserA, Guid.NewGuid(), default)).Access);
        Assert.Empty(f.Client.Calls);
        Assert.Empty(f.Links.Links);
    }

    [Fact]
    public async Task UserWhoDoesNotOwnTheWorkspaceIsTreatedAsUnknown()
    {
        var f = new Fixture();
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.Calls.Clear();

        Assert.Equal(PluggyAccess.NotFound, (await f.Service.ListConnectionsAsync(WorkspaceA, UserB, default)).Access);
        Assert.Equal(PluggyAccess.NotFound, (await f.Service.AddConnectionAsync(WorkspaceA, UserB, ItemB, null, default)).Access);
        Assert.Equal(PluggyAccess.NotFound, (await f.Service.ListAccountsAsync(WorkspaceA, UserB, connection, default)).Access);
        Assert.Equal(PluggyAccess.NotFound, (await f.Service.ListTransactionsAsync(WorkspaceA, UserB, BankA, null, null, null, default)).Access);
        Assert.Empty(f.Client.Calls);
    }

    [Fact]
    public async Task ConnectionsAreIsolatedBetweenWorkspaces()
    {
        var f = new Fixture();
        var connectionA = await f.LinkAsync(WorkspaceA, UserA, ItemA);
        var connectionB = await f.LinkAsync(WorkspaceB, UserB, ItemB);

        Assert.Equal(connectionA, Assert.Single((await f.Service.ListConnectionsAsync(WorkspaceA, UserA, default)).Value!).Id);
        Assert.Equal(connectionB, Assert.Single((await f.Service.ListConnectionsAsync(WorkspaceB, UserB, default)).Value!).Id);
        // Id interno de outro orçamento nunca resolve para um Item.
        Assert.Equal(PluggyAccess.NotFound, (await f.Service.ListAccountsAsync(WorkspaceB, UserB, connectionA, default)).Access);
        Assert.Equal(PluggyAccess.NotFound, (await f.Service.DiagnoseAsync(WorkspaceB, UserB, connectionA, default)).Access);
    }

    [Fact]
    public async Task AccountOfAnotherWorkspacesItemCannotBeReadByTransactionsOrBills()
    {
        var f = new Fixture();
        await f.LinkAsync(WorkspaceA, UserA, ItemA);
        await f.LinkAsync(WorkspaceB, UserB, ItemB);
        f.Client.TransactionsByAccount[BankA] = [FakePluggyClient.Transaction("t1", BankA, "2026-10-01", -10m)];
        f.Client.Calls.Clear();

        Assert.Equal(PluggyAccess.NotFound, (await f.Service.ListTransactionsAsync(WorkspaceB, UserB, BankA, null, null, null, default)).Access);
        Assert.Equal(PluggyAccess.NotFound, (await f.Service.ListBillsAsync(WorkspaceB, UserB, CardA, 1, 50, default)).Access);
        Assert.DoesNotContain(f.Client.Calls, c => c.StartsWith("transactions:", StringComparison.Ordinal) || c.StartsWith("bills:", StringComparison.Ordinal));
        Assert.Equal(PluggyAccess.Allowed, (await f.Service.ListTransactionsAsync(WorkspaceA, UserA, BankA, null, null, null, default)).Access);
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("")]
    public async Task InvalidAccountIdentifierNeverReachesPluggy(string accountId)
    {
        var f = new Fixture();
        await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.Calls.Clear();

        Assert.Equal(PluggyAccess.NotFound, (await f.Service.ListTransactionsAsync(WorkspaceA, UserA, accountId, null, null, null, default)).Access);
        Assert.Empty(f.Client.Calls);
    }

    [Fact]
    public async Task ItemAlreadyLinkedElsewhereCannotBeLinkedAgain()
    {
        var f = new Fixture();
        await f.LinkAsync(WorkspaceA, UserA, ItemA);

        var again = await f.Service.AddConnectionAsync(WorkspaceB, UserB, ItemA.ToUpperInvariant(), null, default);
        Assert.Equal(PluggyAccess.Conflict, again.Access);
        Assert.Single(f.Links.Links);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData(null)]
    [InlineData("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaaX")]
    public async Task MalformedItemIdIsRejectedWithoutCallingPluggy(string? itemId)
    {
        var f = new Fixture();
        var result = await f.Service.AddConnectionAsync(WorkspaceA, UserA, itemId, null, default);
        Assert.Equal(PluggyAccess.Invalid, result.Access);
        Assert.Empty(f.Client.Calls);
    }

    [Fact]
    public async Task UnknownItemIsNotLinked()
    {
        var f = new Fixture();
        var result = await f.Service.AddConnectionAsync(WorkspaceA, UserA, "cccccccc-cccc-4ccc-8ccc-cccccccccccc", null, default);
        Assert.Equal(PluggyAccess.Invalid, result.Access);
        Assert.Empty(f.Links.Links);
    }

    [Fact]
    public async Task IntegrationFailureWhileLinkingIsReportedAndNothingIsStored()
    {
        var f = new Fixture();
        f.Client.Failures[$"item:{ItemA}"] = new PluggyException(PluggyFailureKind.RateLimited, 429);
        var result = await f.Service.AddConnectionAsync(WorkspaceA, UserA, ItemA, null, default);
        Assert.Equal(PluggyAccess.IntegrationFailure, result.Access);
        Assert.Equal(PluggyFailureKind.RateLimited, result.Failure);
        Assert.Empty(f.Links.Links);
    }

    [Fact]
    public async Task ConnectionListKeepsHealthyItemsWhenOneFails()
    {
        var f = new Fixture();
        await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.Items["dddddddd-dddd-4ddd-8ddd-dddddddddddd"] = FakePluggyClient.Item("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
        await f.LinkAsync(WorkspaceA, UserA, "dddddddd-dddd-4ddd-8ddd-dddddddddddd");
        f.Client.Failures["item:dddddddd-dddd-4ddd-8ddd-dddddddddddd"] = new PluggyException(PluggyFailureKind.Timeout);

        var views = (await f.Service.ListConnectionsAsync(WorkspaceA, UserA, default)).Value!;
        Assert.Equal(PluggyConnectionState.Healthy, views[0].State);
        Assert.Equal("Timeout", views[1].IntegrationError);
        Assert.Equal(PluggyConnectionState.Unknown, views[1].State);
    }

    [Fact]
    public async Task ConnectionViewShowsInstitutionStateAndNeverTheFullItemId()
    {
        var f = new Fixture();
        f.Client.Items[ItemA] = FakePluggyClient.Item(ItemA, status: "LOGIN_ERROR", consentExpiresAt: Now.AddDays(-1));
        await f.LinkAsync(WorkspaceA, UserA, ItemA);

        var view = Assert.Single((await f.Service.ListConnectionsAsync(WorkspaceA, UserA, default)).Value!);
        Assert.Equal("Itaú", view.Institution);
        Assert.Equal(PluggyConnectionState.ConsentExpired, view.State);
        Assert.NotNull(view.LastUpdatedAt);
        Assert.DoesNotContain(ItemA, JsonSerializer.Serialize(view));
    }

    [Theory]
    [InlineData("UPDATED", null, PluggyConnectionState.Healthy)]
    [InlineData("UPDATING", null, PluggyConnectionState.Updating)]
    [InlineData("LOGIN_ERROR", null, PluggyConnectionState.ActionRequired)]
    [InlineData("WAITING_USER_INPUT", null, PluggyConnectionState.ActionRequired)]
    [InlineData("OUTDATED", null, PluggyConnectionState.ActionRequired)]
    [InlineData("UPDATED", -1, PluggyConnectionState.ConsentExpired)]
    [InlineData("UPDATED", 30, PluggyConnectionState.Healthy)]
    [InlineData("SOMETHING_ELSE", null, PluggyConnectionState.Error)]
    [InlineData(null, null, PluggyConnectionState.Unknown)]
    public void ConnectionStateIsDerivedFromStatusAndConsent(string? status, int? consentDays, PluggyConnectionState expected)
    {
        var item = FakePluggyClient.Item(ItemA, status: status!, consentExpiresAt: consentDays is { } d ? Now.AddDays(d) : null) with { Status = status };
        Assert.Equal(expected, PluggyPocService.StateOf(item, Now));
    }

    [Fact]
    public async Task AccountNumbersAreMaskedAndMoneyKeepsDecimalPrecision()
    {
        var f = new Fixture();
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);

        var accounts = (await f.Service.ListAccountsAsync(WorkspaceA, UserA, connection, default)).Value!;
        var bank = accounts.Single(a => a.Id == BankA);
        var card = accounts.Single(a => a.Id == CardA);
        Assert.Equal("••••6789", bank.MaskedNumber);
        Assert.Equal("••••3456", bank.MaskedBranchAccount);
        Assert.Equal("••••2222", card.MaskedNumber);
        Assert.Equal("1500.75", bank.Balance);
        Assert.Equal("5000", card.CreditLimit);
        var json = JsonSerializer.Serialize(accounts);
        Assert.DoesNotContain("12345-6789", json);
        Assert.DoesNotContain("5555444433332222", json);
        Assert.DoesNotContain("0001/12345-6", json);
    }

    [Fact]
    public async Task TransactionsRejectInvertedPeriodAndPassCursorThrough()
    {
        var f = new Fixture();
        await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.TransactionsByAccount[BankA] = Enumerable.Range(1, 5).Select(i => FakePluggyClient.Transaction($"t{i}", BankA, $"2026-10-{i:00}", -i * 1.1m)).ToList();

        Assert.Equal(PluggyAccess.Invalid, (await f.Service.ListTransactionsAsync(WorkspaceA, UserA, BankA, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 1), null, default)).Access);
        var first = (await f.Service.ListTransactionsAsync(WorkspaceA, UserA, BankA, null, null, null, default)).Value!;
        Assert.Equal(new[] { "t1", "t2" }, first.Items.Select(t => t.Id));
        Assert.Equal("2", first.NextCursor);
        var last = (await f.Service.ListTransactionsAsync(WorkspaceA, UserA, BankA, null, null, "4", default)).Value!;
        Assert.Equal("t5", Assert.Single(last.Items).Id);
        Assert.Null(last.NextCursor);
        Assert.Equal("-1.1", first.Items[0].Amount);
        var filtered = (await f.Service.ListTransactionsAsync(WorkspaceA, UserA, BankA, new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 5), null, default)).Value!;
        Assert.Equal(new[] { "t4", "t5" }, filtered.Items.Select(t => t.Id));
    }

    [Fact]
    public async Task EmptyAccountsAndTransactionsAreValidEmptyResults()
    {
        var f = new Fixture();
        f.Client.AccountsByItem[ItemA] = [];
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);
        Assert.Empty((await f.Service.ListAccountsAsync(WorkspaceA, UserA, connection, default)).Value!);

        f.Client.AccountsByItem[ItemA] = [FakePluggyClient.Account(BankA, ItemA)];
        var transactions = (await f.Service.ListTransactionsAsync(WorkspaceA, UserA, BankA, null, null, null, default)).Value!;
        Assert.Empty(transactions.Items);
        Assert.Null(transactions.NextCursor);
    }

    [Fact]
    public async Task IntegrationFailuresAreSurfacedWithTheirKind()
    {
        var f = new Fixture();
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.Failures[$"accounts:{ItemA}"] = new PluggyException(PluggyFailureKind.Unavailable, 503);
        f.Client.Failures[$"transactions:{BankA}"] = new PluggyException(PluggyFailureKind.RateLimited, 429);

        var accounts = await f.Service.ListAccountsAsync(WorkspaceA, UserA, connection, default);
        Assert.Equal((PluggyAccess.IntegrationFailure, PluggyFailureKind.Unavailable), (accounts.Access, accounts.Failure!.Value));
        f.Client.Failures.Remove($"accounts:{ItemA}");
        var transactions = await f.Service.ListTransactionsAsync(WorkspaceA, UserA, BankA, null, null, null, default);
        Assert.Equal(PluggyFailureKind.RateLimited, transactions.Failure);
    }

    [Fact]
    public async Task BillsExposeDueDatesAmountsAndCharges()
    {
        var f = new Fixture();
        await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.BillsByAccount[CardA] =
        [
            new("bill-1", DateTimeOffset.Parse("2026-10-17T00:00:00Z"), 1500.25m, "BRL", 225.04m, true, [new("IOF", 3.45m, "BRL", null)],
                DateTimeOffset.Parse("2026-10-10T00:00:00Z"), [new(1500.25m, "BRL", DateTimeOffset.Parse("2026-10-16T00:00:00Z"), "PIX", "TOTAL")]),
            new("bill-2", DateTimeOffset.Parse("2026-11-17T00:00:00Z"), null, null, null, null, [], null, []),
        ];

        var bills = (await f.Service.ListBillsAsync(WorkspaceA, UserA, CardA, 1, 50, default)).Value!;
        Assert.Equal(2, bills.Items.Count);
        Assert.Equal("1500.25", bills.Items[0].TotalAmount);
        Assert.Equal("3.45", bills.Items[0].FinanceCharges.Single().Amount);
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T00:00:00Z"), bills.Items[0].ClosingDate);
        Assert.Equal("1500.25", bills.Items[0].Payments.Single().Amount);
        Assert.Null(bills.Items[1].ClosingDate);
        Assert.Empty(bills.Items[1].Payments);
        Assert.Null(bills.Items[1].TotalAmount);
    }

    // ---- Diagnóstico ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DiagnosticsReportsAvailableEmptyAndNotSupportedPerResource()
    {
        var f = new Fixture();
        f.Client.Items[ItemA] = FakePluggyClient.Item(ItemA, products: ["ACCOUNTS", "TRANSACTIONS"]);
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.TransactionsByAccount[BankA] = Enumerable.Range(1, 5).Select(i => FakePluggyClient.Transaction($"t{i}", BankA, $"2026-0{i}-10", -i)).ToList();
        f.Client.TransactionsByAccount[CardA] = [FakePluggyClient.Transaction("c1", CardA, "2026-09-01", -300m, "PENDING", new(2, 10, 3000m, null, "bill-1", "2222", null, null))];
        f.Client.BillsByAccount[CardA] = [new("bill-1", DateTimeOffset.Parse("2026-10-17T00:00:00Z"), 100m, "BRL", null, null, [], null, [])];

        var report = (await f.Service.DiagnoseAsync(WorkspaceA, UserA, connection, default)).Value!;

        Assert.Equal("Itaú", report.Institution);
        Assert.Equal(PluggyResourceState.Available, Resource(report.ItemResources, "Contas").State);
        Assert.Equal(PluggyResourceState.NotSupported, Resource(report.ItemResources, "Empréstimos").State);
        Assert.Equal(PluggyResourceState.NotSupported, Resource(report.ItemResources, "Investimentos").State);
        Assert.DoesNotContain(f.Client.Calls, c => c.StartsWith("loans:", StringComparison.Ordinal) || c.StartsWith("investments:", StringComparison.Ordinal));

        var bank = report.Accounts.Single(a => a.AccountId == BankA).Resources;
        var tx = Resource(bank, "Transações");
        Assert.Equal((PluggyResourceState.Available, 5), (tx.State, tx.Count));
        Assert.Equal(DateTimeOffset.Parse("2026-01-10T12:00:00Z"), tx.EarliestDate);
        Assert.Equal(DateTimeOffset.Parse("2026-05-10T12:00:00Z"), tx.LatestDate);
        Assert.Contains("amount", tx.FieldsReturned);
        Assert.Contains("operationType", tx.FieldsMissing);
        Assert.Contains("creditCardMetadata", tx.FieldsMissing);
        Assert.Equal(PluggyResourceState.NotSupported, Resource(bank, "Faturas").State);
        Assert.Equal(PluggyResourceState.NotSupported, Resource(bank, "Parcelas").State);
        Assert.Equal(PluggyResourceState.NotSupported, Resource(bank, "Cartões").State);
        Assert.Equal(PluggyResourceState.Available, Resource(bank, "Saldo").State);
        Assert.Equal(PluggyResourceState.Available, Resource(bank, "Limites").State);

        var card = report.Accounts.Single(a => a.AccountId == CardA).Resources;
        Assert.Equal(PluggyResourceState.Available, Resource(card, "Cartões").State);
        Assert.Equal(PluggyResourceState.Available, Resource(card, "Faturas").State);
        var installments = Resource(card, "Parcelas");
        Assert.Equal((PluggyResourceState.Available, 1), (installments.State, installments.Count));
        Assert.Contains("creditCardMetadata.billId", installments.FieldsReturned);
        Assert.Contains(tx.Limitations, l => l.Contains("pendentes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnosticsDistinguishesEmptyDataFromUnavailableProductAndIntegrationError()
    {
        var f = new Fixture();
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.TransactionsByAccount[BankA] = [];
        f.Client.Failures[$"bills:{CardA}"] = new PluggyException(PluggyFailureKind.NotFound, 404);
        f.Client.Failures[$"transactions:{CardA}"] = new PluggyException(PluggyFailureKind.Unavailable, 503);

        var report = (await f.Service.DiagnoseAsync(WorkspaceA, UserA, connection, default)).Value!;
        var bank = report.Accounts.Single(a => a.AccountId == BankA).Resources;
        var card = report.Accounts.Single(a => a.AccountId == CardA).Resources;

        Assert.Equal(PluggyResourceState.Empty, Resource(bank, "Transações").State);
        Assert.Equal(PluggyResourceState.Error, Resource(card, "Transações").State);
        Assert.Equal("Unavailable", Resource(card, "Transações").ErrorKind);
        Assert.Equal(PluggyResourceState.Unavailable, Resource(card, "Faturas").State);
        // Parcelas derivam das transações: herdam o erro em vez de parecerem "sem parcelas".
        Assert.Equal(PluggyResourceState.Error, Resource(card, "Parcelas").State);
    }

    [Fact]
    public async Task DiagnosticsReportsExpiredAuthorizationInsteadOfUnavailableProduct()
    {
        var f = new Fixture();
        f.Client.Items[ItemA] = FakePluggyClient.Item(ItemA, status: "LOGIN_ERROR", consentExpiresAt: Now.AddDays(-2));
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.Failures[$"transactions:{BankA}"] = new PluggyException(PluggyFailureKind.Forbidden, 403);

        var report = (await f.Service.DiagnoseAsync(WorkspaceA, UserA, connection, default)).Value!;
        Assert.Equal(PluggyConnectionState.ConsentExpired, report.State);
        Assert.Equal(PluggyResourceState.ConsentExpired, Resource(report.Accounts.Single(a => a.AccountId == BankA).Resources, "Transações").State);
    }

    [Fact]
    public async Task DiagnosticsStopsHammeringAfterRateLimit()
    {
        var f = new Fixture();
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.Failures[$"transactions:{BankA}"] = new PluggyException(PluggyFailureKind.RateLimited, 429);
        f.Client.Calls.Clear();

        var report = (await f.Service.DiagnoseAsync(WorkspaceA, UserA, connection, default)).Value!;
        Assert.Equal("RateLimited", Resource(report.Accounts.Single(a => a.AccountId == BankA).Resources, "Transações").ErrorKind);
        Assert.Equal("RateLimited", Resource(report.Accounts.Single(a => a.AccountId == CardA).Resources, "Transações").ErrorKind);
        Assert.Equal(1, f.Client.Calls.Count(c => c.StartsWith("transactions:", StringComparison.Ordinal)));
        Assert.DoesNotContain(f.Client.Calls, c => c.StartsWith("bills:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnosticsQueriesLoansOnlyWhenConnectorDeclaresTheProduct()
    {
        var f = new Fixture();
        f.Client.Items[ItemA] = FakePluggyClient.Item(ItemA, products: ["ACCOUNTS", "LOANS"]);
        f.Client.Loans.Add(new("loan-1", "LOAN", "Crédito pessoal", "PERSONAL", "BRL", 10000m, null, null, 24, 6, 18, 0, 8200.5m));
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);

        var report = (await f.Service.DiagnoseAsync(WorkspaceA, UserA, connection, default)).Value!;
        var loans = Resource(report.ItemResources, "Empréstimos");
        Assert.Equal((PluggyResourceState.Available, 1), (loans.State, loans.Count));
        Assert.Contains("outstandingBalance", loans.FieldsReturned);
        Assert.Contains("contractAmount", loans.FieldsReturned);
        Assert.Equal(PluggyResourceState.NotSupported, Resource(report.ItemResources, "Investimentos").State);
    }

    [Fact]
    public async Task DiagnosticsFlagsDuplicatesTruncationAndFutureDatedRecords()
    {
        var f = new Fixture();
        f.Client.TransactionPageSize = 1;
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.TransactionsByAccount[BankA] = Enumerable.Range(1, 8).Select(i => FakePluggyClient.Transaction(i <= 2 ? "same" : $"t{i}", BankA, i <= 4 ? "2026-10-01" : "2026-12-01", -10m)).ToList();

        var report = (await f.Service.DiagnoseAsync(WorkspaceA, UserA, connection, default)).Value!;
        var tx = Resource(report.Accounts.Single(a => a.AccountId == BankA).Resources, "Transações");
        Assert.Equal(5, tx.Count); // MaxTransactionPages = 5, página de 1 registro
        Assert.Contains(tx.Limitations, l => l.Contains("incompleto", StringComparison.Ordinal));
        Assert.Contains(tx.Limitations, l => l.StartsWith("1 IDs repetidos", StringComparison.Ordinal));
        Assert.Contains(tx.Limitations, l => l.Contains("data futura", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnosticsNeverSerializesRawAccountNumbersOrItemId()
    {
        var f = new Fixture();
        var connection = await f.LinkAsync(WorkspaceA, UserA, ItemA);
        f.Client.TransactionsByAccount[BankA] = [FakePluggyClient.Transaction("t1", BankA, "2026-10-01", -10m)];

        var json = JsonSerializer.Serialize((await f.Service.DiagnoseAsync(WorkspaceA, UserA, connection, default)).Value);
        Assert.DoesNotContain("12345-6789", json);
        Assert.DoesNotContain(ItemA, json);
    }

    private static PluggyResourceCoverage Resource(IEnumerable<PluggyResourceCoverage> resources, string name) => resources.Single(r => r.Resource == name);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

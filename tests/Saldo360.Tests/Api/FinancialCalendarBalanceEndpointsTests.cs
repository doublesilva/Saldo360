using System.Net;
using System.Text.Json;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialCalendarBalanceEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private string _member = null!;
    private string _outsider = null!;
    private FamilyDto _family = null!;
    private Guid _income;
    private Guid _expense;

    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("dona");
        _member = await SignInAsync("membro");
        _outsider = await SignInAsync("estranho");
        var created = await CreateFamilyAsync(_owner);
        _family = await JoinByInvitationAsync(_owner, created.Id, _member);
        _income = await CreateCategoryAsync("Salário", "Income");
        _expense = await CreateCategoryAsync("Aluguel", "Expense");
    }

    [Fact]
    public async Task CrossesToday_WithHistoricalRealizedAndFutureProjection_WithoutDuplicatingRealization()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú", "1000.00", Today.AddDays(-10));
        await CreateTransactionAsync(account, _income, "Income", "100.10", Today.AddDays(-2), "Receita histórica");
        await CreateTransactionAsync(account, _expense, "Expense", "20.05", Today.AddDays(-1), "Despesa histórica");
        await CreateTransactionAsync(account, _income, "Income", "50.20", Today, "Receita de hoje");
        await CreatePlannedAsync(account, _expense, "Expense", "30.05", Today.AddDays(-3), "Vencida");
        await CreatePlannedAsync(account, _expense, "Expense", "40.05", Today, "Pendente hoje");
        await CreatePlannedAsync(account, _income, "Income", "70.25", Today.AddDays(2), "Receita futura");
        var realizedPlanned = await CreatePlannedAsync(
            account, _expense, "Expense", "999.00", Today.AddDays(2), "Já realizada");
        await RealizeAsync(realizedPlanned, "60.10", Today);

        var balances = await BalancesAsync(account, Today.AddDays(-2), Today.AddDays(2));

        Assert.Equal("1000.00", balances.OpeningBalance);
        Assert.Equal(5, balances.Days.Count);
        AssertDay(balances.Days[0], Today.AddDays(-2), "Realized", "100.10", "0.00", "1100.10", false);
        AssertDay(balances.Days[1], Today.AddDays(-1), "Realized", "0.00", "20.05", "1080.05", false);
        AssertDay(balances.Days[2], Today, "Projected", "50.20", "100.15", "1000.05", true);
        AssertDay(balances.Days[3], Today.AddDays(1), "Projected", "0.00", "0.00", "1000.05", true);
        AssertDay(balances.Days[4], Today.AddDays(2), "Projected", "70.25", "0.00", "1070.30", true);
        Assert.Equal(new RiskDto("1000.05", null, "1070.30"), balances.Risk);
        Assert.All(balances.Days, day => Assert.InRange(day.ChartPosition!.Value, 0, 1000));

        using var projection = await ProjectionAsync(account, Today.AddDays(2));
        Assert.Equal(
            projection.RootElement.GetProperty("projectedBalance").GetString(),
            balances.Risk.EndingProjectedBalance);
    }

    [Fact]
    public async Task CancellingRealization_ReopensForecastInItsOwnFutureDate()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú", "1000.00", Today.AddDays(-10));
        var planned = await CreatePlannedAsync(account, _expense, "Expense", "1200.10", Today.AddDays(1), "Aluguel");
        var realization = await RealizeAsync(planned, "1210.20", Today);
        await CancelTransactionAsync(realization);

        var balances = await BalancesAsync(account, Today, Today.AddDays(1));

        Assert.Equal("1000.00", balances.Days[0].ClosingBalance);
        Assert.Equal(("0.00", "1200.10", "-200.10"),
            (balances.Days[1].Income, balances.Days[1].Expense, balances.Days[1].ClosingBalance));
        Assert.True(balances.Days[1].IsNegative);
        Assert.Equal(new RiskDto("-200.10", Today.AddDays(1), "-200.10"), balances.Risk);
    }

    [Fact]
    public async Task IndividualTransfersAffectEachAccount_ButConsolidatedTransferIsNeutral()
    {
        await ArrangeAsync();
        var source = await CreateAccountAsync("Origem", "1000.00", Today.AddDays(-5));
        var destination = await CreateAccountAsync("Destino", "100.00", Today.AddDays(-5));
        await CreateTransferAsync(source, destination, "300.00", Today);

        var sourceBalances = await BalancesAsync(source, Today, Today);
        var destinationBalances = await BalancesAsync(destination, Today, Today);
        var consolidated = await BalancesAsync(null, Today, Today);

        Assert.Equal(("0.00", "300.00", "700.00"),
            (sourceBalances.Days[0].TransferIn, sourceBalances.Days[0].TransferOut, sourceBalances.Days[0].ClosingBalance));
        Assert.Equal(("300.00", "0.00", "400.00"),
            (destinationBalances.Days[0].TransferIn, destinationBalances.Days[0].TransferOut, destinationBalances.Days[0].ClosingBalance));
        Assert.Equal("1100.00", consolidated.OpeningBalance);
        Assert.Equal(("0.00", "0.00", "1100.00"),
            (consolidated.Days[0].TransferIn, consolidated.Days[0].TransferOut, consolidated.Days[0].ClosingBalance));
    }

    [Fact]
    public async Task ConsolidatedMatchesIndividualBalancesWhenOpeningReferencesDiffer()
    {
        await ArrangeAsync();
        // The source reference already includes its older outgoing transfer; destination does not.
        var source = await CreateAccountAsync("Origem", "700.00", Today.AddDays(-2));
        var destination = await CreateAccountAsync("Destino", "100.00", Today.AddDays(-10));
        await CreateTransferAsync(source, destination, "300.00", Today.AddDays(-5));
        var sourceBalance = await BalancesAsync(source, Today, Today.AddDays(1));
        var destinationBalance = await BalancesAsync(destination, Today, Today.AddDays(1));
        var consolidated = await BalancesAsync(null, Today, Today.AddDays(1));
        Assert.Equal("700.00", sourceBalance.Days[^1].ClosingBalance);
        Assert.Equal("400.00", destinationBalance.Days[^1].ClosingBalance);
        Assert.Equal("1100.00", consolidated.OpeningBalance);
        Assert.Equal("1100.00", consolidated.Days[^1].ClosingBalance);
        Assert.All(consolidated.Days, day => Assert.Equal(("0.00", "0.00", "0.00", "0.00"), (day.Income, day.Expense, day.TransferIn, day.TransferOut)));
        using var sourceProjection = await ProjectionAsync(source, Today.AddDays(1));
        using var destinationProjection = await ProjectionAsync(destination, Today.AddDays(1));
        Assert.Equal("700.00", sourceProjection.RootElement.GetProperty("projectedBalance").GetString());
        Assert.Equal("400.00", destinationProjection.RootElement.GetProperty("projectedBalance").GetString());
    }

    [Fact]
    public async Task MissingOrFutureOpeningBalance_ReturnsNullMoneyWithExistingReasons()
    {
        await ArrangeAsync();
        await CreateAccountAsync("Configurada", "100.00", Today.AddDays(-1));
        var missing = await CreateAccountAsync("Sem saldo", null, null);
        var future = await CreateAccountAsync("Referência futura", "50.00", Today.AddDays(1));

        var missingSingle = await BalancesAsync(missing, Today, Today.AddDays(1));
        var futureSingle = await BalancesAsync(future, Today, Today.AddDays(1));
        var consolidated = await BalancesAsync(null, Today, Today.AddDays(1));

        AssertUnavailable(missingSingle, "OpeningBalanceNotConfigured");
        AssertUnavailable(futureSingle, "BeforeOpeningBalanceDate");
        AssertUnavailable(consolidated, "OpeningBalanceNotConfigured");
    }

    [Fact]
    public async Task ValidatesDatesAndEnforcesFamilyAndAccountIsolation()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú", "100.00", Today.AddDays(-1));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Get, $"/api/families/{_family.Id}/financial-calendar/balances", _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Get,
                $"/api/families/{_family.Id}/financial-calendar/balances?from=14/10/2026&through=2026-10-15", _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Get, Url(null, Today, Today.AddDays(-1)), _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, Url(account, Today, Today), _outsider)).StatusCode);

        var outsiderFamily = await CreateFamilyAsync(_outsider, "Outra família");
        var outsiderAccountResponse = await SendWithCsrfAsync(
            HttpMethod.Post,
            $"/api/families/{outsiderFamily.Id}/accounts",
            _outsider,
            new
            {
                name = "Outra conta",
                type = "Checking",
                financialScope = "PF",
                openingBalance = "1.00",
                openingBalanceDate = Today,
            });
        var outsiderAccount = (await ReadAsync<IdDto>(outsiderAccountResponse)).Id;
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, Url(outsiderAccount, Today, Today), _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(Url(account, Today, Today))).StatusCode);
    }

    [Fact]
    public async Task SnapshotNeverCountsRealizationAndForecastTogetherDuringConcurrentChanges()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú", "1000.00", Today.AddDays(-10));
        var planned = await CreatePlannedAsync(account, _expense, "Expense", "200.00", Today.AddDays(1), "Aluguel");
        var through = Today.AddDays(2);

        var writer = Task.Run(async () =>
        {
            for (var index = 0; index < 4; index++)
            {
                var realization = await RealizeAsync(planned, "210.00", Today);
                await CancelTransactionAsync(realization);
            }
        });

        var seen = new HashSet<string>();
        while (!writer.IsCompleted)
        {
            var reads = await Task.WhenAll(Enumerable.Range(0, 3)
                .Select(_ => BalancesAsync(account, Today, through)));
            foreach (var value in reads.Select(item => item.Risk.EndingProjectedBalance!))
            {
                seen.Add(value);
            }
        }

        await writer;
        Assert.Subset(new HashSet<string> { "800.00", "790.00" }, seen);
        Assert.Equal("800.00", (await BalancesAsync(account, Today, through)).Risk.EndingProjectedBalance);
    }

    private static void AssertDay(
        DayDto day,
        DateOnly date,
        string kind,
        string income,
        string expense,
        string closing,
        bool overdue) =>
        Assert.Equal((date, kind, income, expense, closing, overdue),
            (day.Date, day.Kind, day.Income, day.Expense, day.ClosingBalance, day.HasOverduePending));

    private static void AssertUnavailable(BalancesDto balances, string reason)
    {
        Assert.Null(balances.OpeningBalance);
        Assert.Equal(reason, balances.UnavailableReason);
        Assert.All(balances.Days, day =>
        {
            Assert.Null(day.Income);
            Assert.Null(day.Expense);
            Assert.Null(day.TransferIn);
            Assert.Null(day.TransferOut);
            Assert.Null(day.ClosingBalance);
            Assert.Equal(reason, day.UnavailableReason);
        });
        Assert.Equal(new RiskDto(null, null, null), balances.Risk);
    }

    private string Url(Guid? account, DateOnly from, DateOnly through) =>
        $"/api/families/{_family.Id}/financial-calendar/balances?from={from:yyyy-MM-dd}&through={through:yyyy-MM-dd}"
        + (account is { } id ? $"&accountId={id}" : string.Empty);

    private async Task<BalancesDto> BalancesAsync(Guid? account, DateOnly from, DateOnly through)
    {
        var response = await SendAsync(HttpMethod.Get, Url(account, from, through), _member);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<BalancesDto>(response);
    }

    private async Task<JsonDocument> ProjectionAsync(Guid account, DateOnly through)
    {
        var response = await SendAsync(HttpMethod.Get,
            $"/api/families/{_family.Id}/accounts/{account}/projection?through={through:yyyy-MM-dd}", _member);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<Guid> CreateAccountAsync(string name, string? openingBalance, DateOnly? openingBalanceDate)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/accounts", _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateCategoryAsync(string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/categories", _owner,
            new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task CreateTransactionAsync(
        Guid accountId, Guid categoryId, string type, string amount, DateOnly date, string description)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/transactions", _owner,
            new { accountId, categoryId, type, amount, transactionDate = date, description });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<Guid> CreatePlannedAsync(
        Guid accountId, Guid categoryId, string type, string amount, DateOnly dueDate, string description)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/planned-transactions", _owner,
            new { accountId, categoryId, type, amount, dueDate, description });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> RealizeAsync(Guid plannedId, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{_family.Id}/planned-transactions/{plannedId}/realize", _member,
            new { amount, transactionDate = date });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task CancelTransactionAsync(Guid transactionId)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{_family.Id}/transactions/{transactionId}/cancel", _member);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task CreateTransferAsync(Guid source, Guid destination, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/transfers", _owner,
            new
            {
                sourceAccountId = source,
                destinationAccountId = destination,
                amount,
                transferDate = date,
                description = "Transferência",
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private sealed record IdDto(Guid Id);
    private sealed record DayDto(
        DateOnly Date,
        string Kind,
        string? Income,
        string? Expense,
        string? TransferIn,
        string? TransferOut,
        string? ClosingBalance,
        bool HasOverduePending,
        bool IsNegative,
        int? ChartPosition,
        string? UnavailableReason);
    private sealed record RiskDto(
        string? MinimumProjectedBalance,
        DateOnly? FirstNegativeProjectedDate,
        string? EndingProjectedBalance);
    private sealed record BalancesDto(
        DateOnly From,
        DateOnly Through,
        Guid? AccountId,
        string? OpeningBalance,
        string? UnavailableReason,
        List<DayDto> Days,
        RiskDto Risk);
}

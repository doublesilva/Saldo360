using System.Net;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialCalendarMonthlyProjectionEndpointsTests(PostgresFixture postgres)
    : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private string _member = null!;
    private string _outsider = null!;
    private FamilyDto _family = null!;
    private Guid _income;
    private Guid _expense;

    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("dona-projecao-mensal");
        _member = await SignInAsync("membro-projecao-mensal");
        _outsider = await SignInAsync("estranho-projecao-mensal");
        var created = await CreateFamilyAsync(_owner);
        _family = await JoinByInvitationAsync(_owner, created.Id, _member);
        _income = await CreateCategoryAsync("Receitas mensais", "Income");
        _expense = await CreateCategoryAsync("Despesas mensais", "Expense");
    }

    [Fact]
    public async Task DefaultsToSixMonths_AndAcceptsVariableCountWithEmptyCompetencies()
    {
        await ArrangeAsync();
        await CreateAccountAsync("Conta", "100.00", ThisMonth.AddMonths(-1));

        var defaultResult = await ProjectionAsync(null, ThisMonth, null);
        Assert.Equal($"{ThisMonth:yyyy-MM}", defaultResult.From);
        Assert.Equal(6, defaultResult.Months.Count);
        Assert.Equal(Enumerable.Range(0, 6).Select(index => $"{ThisMonth.AddMonths(index):yyyy-MM}"),
            defaultResult.Months.Select(month => month.Month));
        Assert.All(defaultResult.Months, month =>
        {
            Assert.Equal(("0.00", "0.00", "0.00", "0.00"),
                (month.RealizedIncome, month.RealizedExpense, month.PendingIncome, month.PendingExpense));
            Assert.Equal("100.00", month.ClosingBalance);
        });

        Assert.Equal(2, (await ProjectionAsync(null, ThisMonth, 2)).Months.Count);
    }

    [Fact]
    public async Task AggregatesExactEvents_OverdueAndReopenedForecastWithoutDuplication()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Conta", "1000.00", ThisMonth.AddMonths(-1));
        await CreateTransactionAsync(account, _income, "Income", "100.10", Today, "Receita");
        await CreateTransactionAsync(account, _expense, "Expense", "20.05", Today, "Despesa");
        await CreatePlannedAsync(account, _income, "Income", "10.00", Today.AddDays(-1), "Vencida");
        await CreatePlannedAsync(account, _expense, "Expense", "30.00", Today.AddDays(1), "Futura");
        var planned = await CreatePlannedAsync(account, _expense, "Expense", "999.99", Today.AddDays(2), "Realizada");
        var realization = await RealizeAsync(planned, "40.00", Today);

        var current = (await ProjectionAsync(account, ThisMonth, 1)).Months.Single();
        Assert.Equal(("100.10", "60.05", "10.00", "30.00", "10.00", "0.00"),
            (current.RealizedIncome, current.RealizedExpense, current.PendingIncome, current.PendingExpense,
                current.OverdueIncome, current.OverdueExpense));
        Assert.Equal("1020.05", current.ClosingBalance);

        await CancelTransactionAsync(realization);
        var reopened = (await ProjectionAsync(account, ThisMonth, 1)).Months.Single();
        Assert.Equal("20.05", reopened.RealizedExpense);
        Assert.Equal("1029.99", reopened.PendingExpense);
        Assert.Equal("60.06", reopened.ClosingBalance);
    }

    [Fact]
    public async Task PreviousOverdueAffectsFutureOnceButDoesNotBecomeMonthlyMovement()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Conta", "500.00", ThisMonth.AddMonths(-2));
        await CreatePlannedAsync(account, _expense, "Expense", "75.25", Today.AddDays(-1), "Vencida anterior");
        var nextMonth = ThisMonth.AddMonths(1);

        var month = (await ProjectionAsync(account, nextMonth, 1)).Months.Single();

        Assert.Equal(("0.00", "0.00", "0.00", "0.00"),
            (month.PendingIncome, month.PendingExpense, month.OverdueIncome, month.OverdueExpense));
        Assert.Equal("424.75", month.OpeningBalance);
        Assert.Equal("424.75", month.ClosingBalance);
    }

    [Fact]
    public async Task TransfersAreIndividualAndNeutralWhenConsolidated_AndClosingMatchesDailyProjection()
    {
        await ArrangeAsync();
        var source = await CreateAccountAsync("Origem", "1000.00", ThisMonth.AddMonths(-1));
        var destination = await CreateAccountAsync("Destino", "100.00", ThisMonth.AddMonths(-1));
        await CreateTransferAsync(source, destination, "300.00", Today);

        var individual = (await ProjectionAsync(source, ThisMonth, 1)).Months.Single();
        var consolidated = (await ProjectionAsync(null, ThisMonth, 1)).Months.Single();
        Assert.Equal("700.00", individual.ClosingBalance);
        Assert.Equal("1100.00", consolidated.ClosingBalance);
        Assert.Equal(("0.00", "0.00", "0.00", "0.00"),
            (consolidated.RealizedIncome, consolidated.RealizedExpense,
                consolidated.PendingIncome, consolidated.PendingExpense));

        var balances = await ReadAsync<BalancesDto>(await SendAsync(HttpMethod.Get,
            $"/api/families/{_family.Id}/financial-calendar/balances?from={ThisMonth:yyyy-MM-dd}&through={ThisMonth.AddMonths(1).AddDays(-1):yyyy-MM-dd}&accountId={source}",
            _member));
        Assert.Equal(balances.Days[^1].ClosingBalance, individual.ClosingBalance);
    }

    [Fact]
    public async Task RejectsInvalidInput_EnforcesIsolation_AndNeverReturnsPartialConsolidatedBalance()
    {
        await ArrangeAsync();
        var configured = await CreateAccountAsync("Configurada", "100.00", ThisMonth.AddMonths(-1));
        await CreateAccountAsync("Sem saldo", null, null);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Get, $"/api/families/{_family.Id}/financial-calendar/monthly-projection?from=2026-1", _member)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Get, Url(null, ThisMonth, 13), _member)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, Url(configured, ThisMonth, 1), _outsider)).StatusCode);

        var consolidated = (await ProjectionAsync(null, ThisMonth, 1)).Months.Single();
        Assert.Equal("OpeningBalanceNotConfigured", consolidated.UnavailableReason);
        Assert.Null(consolidated.OpeningBalance);
        Assert.Null(consolidated.ClosingBalance);
        Assert.Null(consolidated.MinimumProjectedBalance);

        var filtered = (await ProjectionAsync(configured, ThisMonth, 1)).Months.Single();
        Assert.Null(filtered.UnavailableReason);
        Assert.Equal("100.00", filtered.ClosingBalance);
    }

    [Fact]
    public async Task HistoricalCompetencyUsesRealizedClosingWithoutFutureRiskClassification()
    {
        await ArrangeAsync();
        var previousMonth = ThisMonth.AddMonths(-1);
        var account = await CreateAccountAsync("Histórica", "100.00", previousMonth);
        await CreateTransactionAsync(account, _expense, "Expense", "25.25", previousMonth.AddDays(2), "Histórica");

        var month = (await ProjectionAsync(account, previousMonth, 1)).Months.Single();

        Assert.Equal("25.25", month.RealizedExpense);
        Assert.Equal("74.75", month.ClosingBalance);
        Assert.Null(month.MinimumProjectedBalance);
        Assert.Null(month.FirstNegativeProjectedDate);
    }

    [Fact]
    public async Task RepeatableReadNeverCombinesRealizationWithItsForecast()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Concorrente", "1000.00", ThisMonth.AddMonths(-1));
        var planned = await CreatePlannedAsync(account, _expense, "Expense", "200.00", Today.AddDays(1), "Aluguel");

        var writer = Task.Run(async () =>
        {
            for (var index = 0; index < 3; index++)
            {
                var transaction = await RealizeAsync(planned, "210.00", Today);
                await CancelTransactionAsync(transaction);
            }
        });

        var observed = new HashSet<(string Realized, string Pending, string? Closing)>();
        while (!writer.IsCompleted)
        {
            var reads = await Task.WhenAll(Enumerable.Range(0, 2)
                .Select(_ => ProjectionAsync(account, ThisMonth, 1)));
            foreach (var value in reads.Select(result => result.Months.Single()))
            {
                observed.Add((value.RealizedExpense, value.PendingExpense, value.ClosingBalance));
            }
        }

        await writer;
        var final = (await ProjectionAsync(account, ThisMonth, 1)).Months.Single();
        observed.Add((final.RealizedExpense, final.PendingExpense, final.ClosingBalance));
        Assert.All(observed, value => Assert.Contains(value, new[]
        {
            ("0.00", "200.00", (string?)"800.00"),
            ("210.00", "0.00", (string?)"790.00"),
        }));
    }

    private string Url(Guid? account, DateOnly from, int? months) =>
        $"/api/families/{_family.Id}/financial-calendar/monthly-projection?from={from:yyyy-MM}"
        + (months is { } count ? $"&months={count}" : string.Empty)
        + (account is { } id ? $"&accountId={id}" : string.Empty);

    private async Task<ProjectionDto> ProjectionAsync(Guid? account, DateOnly from, int? months)
    {
        var response = await SendAsync(HttpMethod.Get, Url(account, from, months), _member);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<ProjectionDto>(response);
    }

    private async Task<Guid> CreateAccountAsync(string name, string? balance, DateOnly? date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/accounts", _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance = balance, openingBalanceDate = date });
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateCategoryAsync(string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/categories", _owner,
            new { name, type });
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task CreateTransactionAsync(Guid accountId, Guid categoryId, string type, string amount, DateOnly date, string description) =>
        _ = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/transactions", _owner,
            new { accountId, categoryId, type, amount, transactionDate = date, description });

    private async Task<Guid> CreatePlannedAsync(Guid accountId, Guid categoryId, string type, string amount, DateOnly date, string description)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/planned-transactions", _owner,
            new { accountId, categoryId, type, amount, dueDate = date, description });
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> RealizeAsync(Guid plannedId, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{_family.Id}/planned-transactions/{plannedId}/realize", _member,
            new { amount, transactionDate = date });
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task CancelTransactionAsync(Guid id) =>
        _ = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/transactions/{id}/cancel", _member);

    private async Task CreateTransferAsync(Guid source, Guid destination, string amount, DateOnly date) =>
        _ = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/transfers", _owner,
            new { sourceAccountId = source, destinationAccountId = destination, amount, transferDate = date, description = "Transferência" });

    private sealed record IdDto(Guid Id);
    private sealed record ProjectionDto(string From, List<MonthDto> Months);
    private sealed record MonthDto(
        string Month,
        string RealizedIncome,
        string RealizedExpense,
        string PendingIncome,
        string PendingExpense,
        string OverdueIncome,
        string OverdueExpense,
        string? OpeningBalance,
        string? ClosingBalance,
        string? MinimumProjectedBalance,
        DateOnly? FirstNegativeProjectedDate,
        string? UnavailableReason);
    private sealed record DayDto(string? ClosingBalance);
    private sealed record BalancesDto(List<DayDto> Days);
}

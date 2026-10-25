using System.Net;
using System.Text.Json;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Saldo realizado por conta sobre PostgreSQL real. Fórmula: saldo inicial (início da data de referência)
/// + receitas − despesas não canceladas com referência ≤ data ≤ asOf (fechamento).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
public sealed class AccountBalanceEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static readonly DateOnly Reference = new(2026, 9, 1);

    private string _owner = null!;
    private string _ownerSecondSession = null!;
    private string _outsider = null!;
    private WorkspaceDto _family = null!;
    private WorkspaceDto _otherFamily = null!;
    private Guid _salario;
    private Guid _mercado;
    private Guid _foreignCategory;

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("dona");
        _ownerSecondSession = _owner;
        _outsider = await SignInAsync("estranho");
        var created = await CreateWorkspaceAsync(_owner, "Família Silva");
        _family = created;
        _otherFamily = await CreateWorkspaceAsync(_outsider, "Família Estranha");
        _salario = await CreateCategoryAsync(_owner, _family.Id, "Salário", "Income");
        _mercado = await CreateCategoryAsync(_owner, _family.Id, "Mercado", "Expense");
        _foreignCategory = await CreateCategoryAsync(_outsider, _otherFamily.Id, "Mercado", "Expense");
    }

    [Fact]
    public async Task Roteiro_1000_Plus500_Minus200_Is1300_AsExactStrings()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú PF", "1000.00", Reference);
        await AddAsync(account, "Income", "500.00", Reference);
        await AddAsync(account, "Expense", "200.00", Reference);

        var response = await SendAsync(HttpMethod.Get, BalanceUrl(account, Reference.AddDays(5)), _ownerSecondSession);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(account.ToString(), root.GetProperty("accountId").GetString());
        Assert.Equal("2026-09-06", root.GetProperty("asOf").GetString());
        Assert.Equal("1000.00", root.GetProperty("openingBalance").GetString());
        Assert.Equal("2026-09-01", root.GetProperty("openingBalanceDate").GetString());
        Assert.Equal("500.00", root.GetProperty("totalIncome").GetString());
        Assert.Equal("200.00", root.GetProperty("totalExpense").GetString());
        Assert.Equal("1300.00", root.GetProperty("balance").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("unavailableReason").ValueKind);
    }

    [Fact]
    public async Task MovementsOnTheReferenceDay_Count_HistoryBefore_DoesNot()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú PF", "1000.00", Reference);
        await AddAsync(account, "Expense", "999.99", Reference.AddDays(-1)); // já incluído no saldo inicial
        await AddAsync(account, "Income", "10.00", Reference.AddDays(-30));
        await AddAsync(account, "Income", "500.00", Reference);
        await AddAsync(account, "Expense", "200.00", Reference);

        var balance = await GetBalanceAsync(account, Reference);

        Assert.Equal(("500.00", "200.00", "1300.00"), (balance.TotalIncome, balance.TotalExpense, balance.Balance));
    }

    [Fact]
    public async Task PastQuery_ExcludesLaterMovements_AndClosesTheQueriedDay()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú PF", "100.00", Reference);
        await AddAsync(account, "Income", "50.00", Reference.AddDays(2));
        await AddAsync(account, "Expense", "30.00", Reference.AddDays(3));

        Assert.Equal("100.00", (await GetBalanceAsync(account, Reference.AddDays(1))).Balance);
        Assert.Equal("150.00", (await GetBalanceAsync(account, Reference.AddDays(2))).Balance);
        Assert.Equal("120.00", (await GetBalanceAsync(account, Reference.AddDays(3))).Balance);
    }

    [Fact]
    public async Task CancelledTransactions_AreExcluded()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú PF", "100.00", Reference);
        await AddAsync(account, "Income", "50.00", Reference);
        var wrong = await AddAsync(account, "Expense", "70.00", Reference);
        Assert.Equal("80.00", (await GetBalanceAsync(account, Reference)).Balance);

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Post, $"{TransactionsUrl}/{wrong}/cancel", _owner)).StatusCode);

        var balance = await GetBalanceAsync(account, Reference);
        Assert.Equal(("50.00", "0.00", "150.00"), (balance.TotalIncome, balance.TotalExpense, balance.Balance));
    }

    [Fact]
    public async Task EditsAreReflected_AmountTypeDateAndAccount()
    {
        await ArrangeAsync();
        var itau = await CreateAccountAsync("Itaú PF", "100.00", Reference);
        var nubank = await CreateAccountAsync("Nubank", "0.00", Reference);
        var transaction = await AddAsync(itau, "Expense", "40.00", Reference);
        Assert.Equal("60.00", (await GetBalanceAsync(itau, Reference)).Balance);

        await EditAsync(transaction, itau, "Expense", "45.50", Reference);
        Assert.Equal("54.50", (await GetBalanceAsync(itau, Reference)).Balance);

        await EditAsync(transaction, itau, "Income", "45.50", Reference);
        Assert.Equal("145.50", (await GetBalanceAsync(itau, Reference)).Balance);

        await EditAsync(transaction, itau, "Income", "45.50", Reference.AddDays(-1)); // virou histórico
        Assert.Equal("100.00", (await GetBalanceAsync(itau, Reference)).Balance);

        await EditAsync(transaction, nubank, "Income", "45.50", Reference); // mudou de conta
        Assert.Equal("100.00", (await GetBalanceAsync(itau, Reference)).Balance);
        Assert.Equal("45.50", (await GetBalanceAsync(nubank, Reference)).Balance);
    }

    [Fact]
    public async Task OpeningBalanceChange_IsReflected_IncludingReferenceDate()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú PF", "100.00", Reference);
        await AddAsync(account, "Income", "50.00", Reference.AddDays(1));

        await UpdateAccountAsync(account, "Itaú PF", "200.00", Reference.AddDays(2));

        var balance = await GetBalanceAsync(account, Reference.AddDays(2));
        Assert.Equal(("200.00", "0.00", "200.00"), (balance.OpeningBalance, balance.TotalIncome, balance.Balance));
    }

    [Fact]
    public async Task WithoutOpeningBalance_IsUnavailable_NotZero()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Dinheiro", null, null);
        await AddAsync(account, "Income", "50.00", Reference);

        var response = await SendAsync(HttpMethod.Get, BalanceUrl(account), _owner);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("OpeningBalanceNotConfigured", root.GetProperty("unavailableReason").GetString());
        foreach (var field in new[] { "openingBalance", "openingBalanceDate", "totalIncome", "totalExpense", "balance" })
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty(field).ValueKind);
        }
    }

    [Fact]
    public async Task QueryBeforeReferenceDate_IsUnavailable_ButShowsTheOpeningBalance()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú PF", "1000.00", Reference);
        await AddAsync(account, "Expense", "10.00", Reference.AddDays(-5));

        var balance = await GetBalanceAsync(account, Reference.AddDays(-1));

        Assert.Equal("BeforeOpeningBalanceDate", balance.UnavailableReason);
        Assert.Equal(("1000.00", (DateOnly?)Reference), (balance.OpeningBalance, balance.OpeningBalanceDate));
        Assert.Equal(((string?)null, (string?)null, (string?)null), (balance.TotalIncome, balance.TotalExpense, balance.Balance));
    }

    [Theory]
    [InlineData("0.00", "0.00", "0.00", "0.00")]
    [InlineData("0.00", "25.10", "30.00", "-4.90")]
    [InlineData("-500.00", "100.00", "0.00", "-400.00")]
    [InlineData("-0.01", "0.00", "0.00", "-0.01")]
    public async Task ZeroAndNegativeOpeningBalances(string opening, string income, string expense, string expected)
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Conta", opening, Reference);
        if (income != "0.00")
        {
            await AddAsync(account, "Income", income, Reference);
        }

        if (expense != "0.00")
        {
            await AddAsync(account, "Expense", expense, Reference);
        }

        var balance = await GetBalanceAsync(account, Reference);

        Assert.Equal((opening, expected), (balance.OpeningBalance, balance.Balance));
        Assert.Null(balance.UnavailableReason);
    }

    [Fact]
    public async Task ExactCents_ZeroPointTenPlusZeroPointTwenty_IsZeroPointThirty()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Conta", "0.00", Reference);
        await AddAsync(account, "Income", "0.10", Reference);
        await AddAsync(account, "Income", "0.20", Reference);
        await AddAsync(account, "Expense", "0.30", Reference.AddDays(1));

        Assert.Equal(("0.30", "0.30"), ((await GetBalanceAsync(account, Reference)).TotalIncome, (await GetBalanceAsync(account, Reference)).Balance));
        Assert.Equal("0.00", (await GetBalanceAsync(account, Reference.AddDays(1))).Balance);
    }

    [Fact]
    public async Task SumsAboveTheSingleTransactionLimit_KeepFullPrecision()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Conta", "9999999999999.99", Reference);
        for (var i = 0; i < 3; i++)
        {
            await AddAsync(account, "Income", "9999999999999.99", Reference);
        }

        await AddAsync(account, "Expense", "0.01", Reference);

        var balance = await GetBalanceAsync(account, Reference);

        Assert.Equal(("29999999999999.97", "0.01", "39999999999999.95"), (balance.TotalIncome, balance.TotalExpense, balance.Balance));
    }

    [Fact]
    public async Task OtherAccountsAndFamilies_DoNotInterfere()
    {
        await ArrangeAsync();
        var itau = await CreateAccountAsync("Itaú PF", "100.00", Reference);
        var nubank = await CreateAccountAsync("Nubank", "100.00", Reference);
        await AddAsync(itau, "Income", "1.00", Reference);
        await AddAsync(nubank, "Income", "1000.00", Reference);
        var foreign = await CreateAccountAsync("Conta Estranha", "100.00", Reference, _outsider, _otherFamily.Id);
        await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_otherFamily.Id}/transactions", _outsider,
            new { accountId = foreign, categoryId = _foreignCategory, type = "Expense", description = "X", amount = "50.00", transactionDate = Reference });

        Assert.Equal("101.00", (await GetBalanceAsync(itau, Reference)).Balance);
        Assert.Equal("1100.00", (await GetBalanceAsync(nubank, Reference)).Balance);
        Assert.Equal("50.00", (await GetBalanceAsync(foreign, Reference, _outsider, _otherFamily.Id)).Balance);
    }

    [Fact]
    public async Task Outsider_And_ForeignOrUnknownAccounts_Get404()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú PF", "100.00", Reference);
        var foreign = await CreateAccountAsync("Conta Estranha", "100.00", Reference, _outsider, _otherFamily.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, BalanceUrl(account), _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{_otherFamily.Id}/accounts/{account}/balance", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, BalanceUrl(foreign), _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, BalanceUrl(Guid.CreateVersion7()), _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(BalanceUrl(account))).StatusCode);
    }

    [Fact]
    public async Task InactiveAccount_CanStillBeQueried()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Conta antiga", "100.00", Reference);
        await AddAsync(account, "Expense", "30.00", Reference);
        await UpdateAccountAsync(account, "Conta antiga", "100.00", Reference, isActive: false);

        Assert.Equal("70.00", (await GetBalanceAsync(account, Reference)).Balance);
    }

    [Fact]
    public async Task DefaultDate_IsTodayInSaoPaulo_FutureOrInvalidDateIs400()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("Itaú PF", "100.00", Reference);
        var today = BusinessCalendar.Today(DateTimeOffset.UtcNow);

        Assert.Equal(today, (await GetBalanceAsync(account, null)).AsOf);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, BalanceUrl(account, today.AddDays(1)), _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{BalanceUrl(account)}?asOf=2026-13-01", _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{BalanceUrl(account)}?asOf=01/09/2026", _owner)).StatusCode);
    }

    private string TransactionsUrl => $"/api/financial-workspaces/{_family.Id}/transactions";

    private string BalanceUrl(Guid account, DateOnly? asOf = null, Guid? workspaceId = null) =>
        $"/api/financial-workspaces/{workspaceId ?? _family.Id}/accounts/{account}/balance" + (asOf is { } d ? $"?asOf={d:yyyy-MM-dd}" : string.Empty);

    private async Task<BalanceDto> GetBalanceAsync(Guid account, DateOnly? asOf, string? session = null, Guid? workspaceId = null)
    {
        var response = await SendAsync(HttpMethod.Get, BalanceUrl(account, asOf, workspaceId), session ?? _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<BalanceDto>(response);
    }

    private async Task<Guid> CreateAccountAsync(
        string name, string? openingBalance, DateOnly? openingBalanceDate, string? session = null, Guid? workspaceId = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspaceId ?? _family.Id}/accounts", session ?? _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task UpdateAccountAsync(Guid account, string name, string openingBalance, DateOnly openingBalanceDate, bool isActive = true)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{_family.Id}/accounts/{account}", _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate, isActive });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> CreateCategoryAsync(string session, Guid workspaceId, string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspaceId}/categories", session, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> AddAsync(Guid account, string type, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, TransactionsUrl, _ownerSecondSession, TransactionBody(account, type, amount, date));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task EditAsync(Guid transaction, Guid account, string type, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Put, $"{TransactionsUrl}/{transaction}", _owner, TransactionBody(account, type, amount, date));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private object TransactionBody(Guid account, string type, string amount, DateOnly date) => new
    {
        accountId = account,
        categoryId = type == "Income" ? _salario : _mercado,
        type,
        description = "Lançamento",
        amount,
        transactionDate = date,
    };

    private sealed record IdDto(Guid Id);

    private sealed record BalanceDto(
        Guid AccountId, DateOnly AsOf, string? OpeningBalance, DateOnly? OpeningBalanceDate,
        string? TotalIncome, string? TotalExpense, string? Balance, string? UnavailableReason);
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Transferências sobre PostgreSQL real: CRUD, regras e efeito nos saldos das duas contas. Dois membros (via
/// convite) e uma família estranha com conta própria.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FinancialTransferEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static readonly DateOnly Reference = new(2026, 9, 1);

    private string _owner = null!;
    private string _ownerSecondSession = null!;
    private string _outsider = null!;
    private WorkspaceDto _family = null!;
    private WorkspaceDto _otherFamily = null!;
    private Guid _pj;
    private Guid _pf;
    private Guid _foreignAccount;

    private string TransfersUrl => $"/api/financial-workspaces/{_family.Id}/transfers";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("dona");
        _ownerSecondSession = _owner;
        _outsider = await SignInAsync("estranho");
        var created = await CreateWorkspaceAsync(_owner, "Família Silva");
        _family = created;
        _otherFamily = await CreateWorkspaceAsync(_outsider, "Família Estranha");
        _pj = await CreateAccountAsync("Itaú Empresas", "PJ", "2000.00", Reference);
        _pf = await CreateAccountAsync("Itaú PF", "PF", "100.00", Reference);
        _foreignAccount = await CreateAccountAsync("Conta Estranha", "PF", "0.00", Reference, _outsider, _otherFamily.Id);
    }

    [Fact]
    public async Task Acceptance_PJ2000_PF100_Transfer500_Gives1500And600_WithoutTouchingIncomeOrExpense()
    {
        await ArrangeAsync();

        var response = await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _ownerSecondSession, Body(_pj, _pf, "500.00", Reference.AddDays(3)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transfer = await ReadAsync<TransferDto>(response);
        Assert.Equal($"{TransfersUrl}/{transfer.Id}", response.Headers.Location!.ToString());
        Assert.Equal(("500.00", "Itaú Empresas", "PJ", "Itaú PF", "PF"),
            (transfer.Amount, transfer.SourceAccount.Name, transfer.SourceAccount.FinancialScope, transfer.DestinationAccount.Name, transfer.DestinationAccount.FinancialScope));

        var pj = await BalanceAsync(_pj);
        var pf = await BalanceAsync(_pf);
        Assert.Equal(("1500.00", "0.00", "0.00", "0.00", "500.00"), (pj.Balance, pj.TotalIncome, pj.TotalExpense, pj.TotalTransferIn, pj.TotalTransferOut));
        Assert.Equal(("600.00", "0.00", "0.00", "500.00", "0.00"), (pf.Balance, pf.TotalIncome, pf.TotalExpense, pf.TotalTransferIn, pf.TotalTransferOut));

        await using var db = Db();
        Assert.Equal(0, await db.FinancialTransactions.CountAsync()); // não vira receita/despesa
        Assert.Equal(1, await db.FinancialTransfers.CountAsync());
    }

    [Fact]
    public async Task Cancel_RestoresBothBalances_IsIdempotent_AndPreservesTheRow()
    {
        await ArrangeAsync();
        var transfer = await CreateAsync(_pj, _pf, "500.00", Reference);
        var url = $"{TransfersUrl}/{transfer}";

        var first = await SendWithCsrfAsync(HttpMethod.Post, $"{url}/cancel", _owner);
        var second = await SendWithCsrfAsync(HttpMethod.Post, $"{url}/cancel", _ownerSecondSession);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        var cancelled = await ReadAsync<TransferDto>(first);
        Assert.True(cancelled.IsCancelled);
        Assert.Equal(cancelled.CancelledAt, (await ReadAsync<TransferDto>(second)).CancelledAt);
        Assert.Equal(("2000.00", "100.00"), ((await BalanceAsync(_pj)).Balance, (await BalanceAsync(_pf)).Balance));

        Assert.Empty((await ListAsync(_owner)).Items);
        Assert.True(Assert.Single((await ListAsync(_owner, "includeCancelled=true")).Items).IsCancelled);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, url, _owner)).StatusCode);
        await using var db = Db();
        var stored = await db.FinancialTransfers.SingleAsync();
        Assert.Equal((true, await UserIdAsync("dona")), (stored.IsCancelled, stored.CancelledByUserId));
    }

    [Fact]
    public async Task DifferentReferenceDates_DoNotDuplicateWhatIsAlreadyInAnOpeningBalance()
    {
        await ArrangeAsync();
        // PJ referência 01/09, PF referência 10/09. Transferência em 05/09: já está no saldo inicial do PF,
        // mas ainda precisa sair do PJ.
        var pf = await CreateAccountAsync("Nubank PF", "PF", "600.00", Reference.AddDays(9));
        await CreateAsync(_pj, pf, "500.00", Reference.AddDays(4));
        await CreateAsync(pf, _pj, "50.00", Reference.AddDays(10)); // depois das duas referências: entra nas duas

        var pjBalance = await BalanceAsync(_pj);
        var pfBalance = await BalanceAsync(pf);

        Assert.Equal(("500.00", "50.00", "1550.00"), (pjBalance.TotalTransferOut, pjBalance.TotalTransferIn, pjBalance.Balance));
        Assert.Equal(("0.00", "50.00", "550.00"), (pfBalance.TotalTransferIn, pfBalance.TotalTransferOut, pfBalance.Balance));
    }

    [Fact]
    public async Task PastQuery_And_TransfersCombineWithIncomeAndExpense()
    {
        await ArrangeAsync();
        var salario = await CreateCategoryAsync("Salário", "Income");
        var mercado = await CreateCategoryAsync("Mercado", "Expense");
        await AddTransactionAsync(_pf, salario, "Income", "1000.00", Reference);
        await AddTransactionAsync(_pf, mercado, "Expense", "0.30", Reference);
        await CreateAsync(_pj, _pf, "0.10", Reference.AddDays(1));
        await CreateAsync(_pf, _pj, "0.20", Reference.AddDays(2));

        var pf = await BalanceAsync(_pf);
        Assert.Equal(("1000.00", "0.30", "0.10", "0.20", "1099.60"), (pf.TotalIncome, pf.TotalExpense, pf.TotalTransferIn, pf.TotalTransferOut, pf.Balance));
        Assert.Equal("1099.80", (await BalanceAsync(_pf, Reference.AddDays(1))).Balance);
        Assert.Equal("1999.90", (await BalanceAsync(_pj, Reference.AddDays(1))).Balance);
    }

    [Fact]
    public async Task SourceMayGoNegative_AndAnyScopeDirectionIsAllowed()
    {
        await ArrangeAsync();
        var pf2 = await CreateAccountAsync("Nubank PF", "PF", "0.00", Reference);
        var pj2 = await CreateAccountAsync("Inter PJ", "PJ", "0.00", Reference);

        await CreateAsync(_pf, _pj, "150.00", Reference); // PF → PJ, deixa PF negativo
        await CreateAsync(_pf, pf2, "1.00", Reference); // PF → PF
        await CreateAsync(_pj, pj2, "1.00", Reference); // PJ → PJ

        var pf = await BalanceAsync(_pf);
        Assert.Equal("-51.00", pf.Balance);
        Assert.Equal(3, (await ListAsync(_owner)).TotalCount);
    }

    [Fact]
    public async Task SameAccount_IsRejected()
    {
        await ArrangeAsync();

        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _owner, Body(_pf, _pf, "10.00", Reference)), "destinationAccountId");
        await AssertNoTransfersAsync();
    }

    [Fact]
    public async Task AccountsFromAnotherFamily_AreRejected_WithGenericMessages()
    {
        await ArrangeAsync();

        var foreignSource = await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _owner, Body(_foreignAccount, _pf, "10.00", Reference));
        var foreignDestination = await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _owner, Body(_pj, _foreignAccount, "10.00", Reference));
        var unknown = await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _owner, Body(Guid.CreateVersion7(), _pf, "10.00", Reference));

        foreach (var (response, field, message) in new[]
        {
            (foreignSource, "sourceAccountId", "Conta de origem inválida."),
            (foreignDestination, "destinationAccountId", "Conta de destino inválida."),
            (unknown, "sourceAccountId", "Conta de origem inválida."),
        })
        {
            await AssertValidationErrorAsync(response, field);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains(message, text);
            Assert.DoesNotContain("Estranha", text);
            Assert.DoesNotContain(_foreignAccount.ToString(), text);
        }

        await AssertNoTransfersAsync();
    }

    [Fact]
    public async Task InactiveAccounts_AreRejectedOnCreate_ButHistoryAndBalancesSurviveDeactivation()
    {
        await ArrangeAsync();
        var transfer = await CreateAsync(_pj, _pf, "500.00", Reference);
        await DeactivateAsync(_pj, "Itaú Empresas", "PJ", "2000.00");

        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _owner, Body(_pj, _pf, "1.00", Reference)), "sourceAccountId");
        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _owner, Body(_pf, _pj, "1.00", Reference)), "destinationAccountId");

        var listed = Assert.Single((await ListAsync(_ownerSecondSession)).Items);
        Assert.Equal((transfer, false, true), (listed.Id, listed.SourceAccount.IsActive, listed.DestinationAccount.IsActive));
        Assert.Equal(("1500.00", "600.00"), ((await BalanceAsync(_pj)).Balance, (await BalanceAsync(_pf)).Balance));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-10")]
    [InlineData("10.005")]
    [InlineData("1.500")]
    [InlineData("10000000000000")]
    public async Task InvalidAmount_Returns400_WithoutRounding(string amount)
    {
        await ArrangeAsync();

        var response = await SendRawAsync(TransfersUrl, _owner,
            $$"""{ "sourceAccountId": "{{_pj}}", "destinationAccountId": "{{_pf}}", "amount": {{amount}}, "transferDate": "2026-09-01", "description": "X" }""");

        await AssertValidationErrorAsync(response, "amount");
        await AssertNoTransfersAsync();
    }

    [Fact]
    public async Task FutureDate_IsRejected_TodayIsAccepted_AndDateWithTimeIs400()
    {
        await ArrangeAsync();
        var today = BusinessCalendar.Today(DateTimeOffset.UtcNow);

        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _owner, Body(_pj, _pf, "1.00", today.AddDays(1))), "transferDate");
        Assert.Equal(HttpStatusCode.Created,
            (await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _owner, Body(_pj, _pf, "1.00", today))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendRawAsync(TransfersUrl, _owner,
            $$"""{ "sourceAccountId": "{{_pj}}", "destinationAccountId": "{{_pf}}", "amount": 1, "transferDate": "2026-09-01T10:00:00", "description": "X" }""")).StatusCode);
    }

    [Fact]
    public async Task ServerControlledFieldsAndCategory_AreRejected_AndThereIsNoEdit()
    {
        await ArrangeAsync();
        var transfer = await CreateAsync(_pj, _pf, "10.00", Reference);
        var fields = $"\"sourceAccountId\": \"{_pj}\", \"destinationAccountId\": \"{_pf}\", \"amount\": 1, \"transferDate\": \"2026-09-01\", \"description\": \"X\"";

        var responses = new[]
        {
            await SendRawAsync(TransfersUrl, _owner, $$"""{ {{fields}}, "workspaceId": "{{_otherFamily.Id}}" }"""),
            await SendRawAsync(TransfersUrl, _owner, $$"""{ {{fields}}, "isCancelled": true }"""),
            await SendRawAsync(TransfersUrl, _owner, $$"""{ {{fields}}, "createdByUserId": "{{Guid.CreateVersion7()}}" }"""),
            await SendRawAsync(TransfersUrl, _owner, $$"""{ {{fields}}, "categoryId": "{{Guid.CreateVersion7()}}" }"""),
            await SendRawAsync(TransfersUrl, _owner, $$"""{ {{fields}}, "currency": "USD" }"""),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        var put = await SendWithCsrfAsync(HttpMethod.Put, $"{TransfersUrl}/{transfer}", _owner, Body(_pj, _pf, "99.00", Reference));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        await using var db = Db();
        Assert.Equal(10.00m, (await db.FinancialTransfers.SingleAsync()).Amount);
    }

    [Fact]
    public async Task Outsider_CannotReadOrCancel()
    {
        await ArrangeAsync();
        var transfer = await CreateAsync(_pj, _pf, "10.00", Reference);
        var url = $"{TransfersUrl}/{transfer}";
        var otherRoute = $"/api/financial-workspaces/{_otherFamily.Id}/transfers/{transfer}";

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, TransfersUrl, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, url, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _outsider, Body(_pj, _pf, "1.00", Reference))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"{url}/cancel", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, otherRoute, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"{otherRoute}/cancel", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(TransfersUrl)).StatusCode);

        await using var db = Db();
        Assert.False((await db.FinancialTransfers.SingleAsync()).IsCancelled);
    }

    [Fact]
    public async Task WriteOperations_RequireCsrf()
    {
        await ArrangeAsync();
        var transfer = await CreateAsync(_pj, _pf, "10.00", Reference);

        var post = await SendJsonAsync(HttpMethod.Post, TransfersUrl, _owner, csrf: null, Body(_pj, _pf, "1.00", Reference));
        var cancel = await SendJsonAsync(HttpMethod.Post, $"{TransfersUrl}/{transfer}/cancel", _owner, csrf: null, body: null);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (post.StatusCode, cancel.StatusCode));
        await using var db = Db();
        var stored = await db.FinancialTransfers.SingleAsync();
        Assert.False(stored.IsCancelled);
    }

    [Fact]
    public async Task List_FiltersByAccountOnEitherSide_DatesAndPaginates_WithoutMixingFamilies()
    {
        await ArrangeAsync();
        var nubank = await CreateAccountAsync("Nubank PF", "PF", "0.00", Reference);
        var ids = new List<(Guid Id, DateOnly Date)>();
        foreach (var (source, destination, date) in new[]
        {
            (_pj, _pf, Reference),
            (_pf, nubank, Reference.AddDays(1)),
            (_pj, nubank, Reference.AddDays(1)),
            (nubank, _pj, Reference.AddDays(2)),
        })
        {
            ids.Add((await CreateAsync(source, destination, "1.00", date), date));
        }

        var foreignOther = await CreateAccountAsync("Outra Estranha", "PF", "0.00", Reference, _outsider, _otherFamily.Id);
        await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_otherFamily.Id}/transfers", _outsider,
            Body(_foreignAccount, foreignOther, "1.00", Reference));

        var expected = ids.OrderByDescending(x => x.Date).ThenByDescending(x => x.Id.ToString()).Select(x => x.Id).ToList();
        var pages = new[] { await ListAsync(_ownerSecondSession, "pageSize=3&page=1"), await ListAsync(_ownerSecondSession, "pageSize=3&page=2") };
        Assert.All(pages, p => Assert.Equal(4, p.TotalCount));
        Assert.Equal(expected, pages.SelectMany(p => p.Items).Select(t => t.Id));

        Assert.Equal(3, (await ListAsync(_owner, $"accountId={_pj}")).TotalCount); // origem ou destino
        Assert.Equal(2, (await ListAsync(_owner, $"accountId={_pf}")).TotalCount);
        Assert.Equal(3, (await ListAsync(_owner, $"accountId={nubank}")).TotalCount);
        Assert.Equal(2, (await ListAsync(_owner, "from=2026-09-02&to=2026-09-02")).TotalCount);
        Assert.Equal(0, (await ListAsync(_owner, $"accountId={_foreignAccount}")).TotalCount);
    }

    [Theory]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("from=2026-10-02&to=2026-10-01", "from")]
    [InlineData("from=01/09/2026", "from")]
    [InlineData("to=2026-9-1", "to")]
    public async Task List_InvalidFilters_Return400(string query, string field)
    {
        await ArrangeAsync();

        await AssertValidationErrorAsync(await SendAsync(HttpMethod.Get, $"{TransfersUrl}?{query}", _owner), field);
    }

    [Fact]
    public async Task Responses_UseExactAmountStrings_AndDoNotExposeUsersOrFamily()
    {
        await ArrangeAsync();
        await CreateAsync(_pj, _pf, "1234.50", Reference);

        var body = await (await SendAsync(HttpMethod.Get, TransfersUrl, _owner)).Content.ReadAsStringAsync();

        using var json = JsonDocument.Parse(body);
        Assert.Equal("1234.50", json.RootElement.GetProperty("items")[0].GetProperty("amount").GetString());
        foreach (var forbidden in new[] { "workspaceId", "createdBy", "cancelledBy", "userId", _family.Id.ToString() })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static object Body(Guid source, Guid destination, string amount, DateOnly date, string description = "Pró-labore") =>
        new { sourceAccountId = source, destinationAccountId = destination, amount, transferDate = date, description };

    private async Task<Guid> CreateAsync(Guid source, Guid destination, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, TransfersUrl, _owner, Body(source, destination, amount, date));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<PageDto> ListAsync(string session, string query = "")
    {
        var response = await SendAsync(HttpMethod.Get, query.Length > 0 ? $"{TransfersUrl}?{query}" : TransfersUrl, session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PageDto>(response);
    }

    private async Task<BalanceDto> BalanceAsync(Guid account, DateOnly? asOf = null)
    {
        var url = $"/api/financial-workspaces/{_family.Id}/accounts/{account}/balance" + (asOf is { } d ? $"?asOf={d:yyyy-MM-dd}" : string.Empty);
        var response = await SendAsync(HttpMethod.Get, url, _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<BalanceDto>(response);
    }

    private async Task<Guid> CreateAccountAsync(
        string name, string scope, string openingBalance, DateOnly openingBalanceDate, string? session = null, Guid? workspaceId = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspaceId ?? _family.Id}/accounts", session ?? _owner,
            new { name, type = "Checking", financialScope = scope, openingBalance, openingBalanceDate });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task DeactivateAsync(Guid account, string name, string scope, string openingBalance)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{_family.Id}/accounts/{account}", _owner,
            new { name, type = "Checking", financialScope = scope, openingBalance, openingBalanceDate = Reference, isActive = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> CreateCategoryAsync(string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/categories", _owner, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task AddTransactionAsync(Guid account, Guid category, string type, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/transactions", _owner,
            new { accountId = account, categoryId = category, type, description = "Lançamento", amount, transactionDate = date });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<Guid> UserIdAsync(string subject)
    {
        await using var db = Db();
        return await db.Users.Where(u => u.Subject == subject).Select(u => u.Id).SingleAsync();
    }

    private async Task AssertNoTransfersAsync()
    {
        await using var db = Db();
        Assert.Equal(0, await db.FinancialTransfers.CountAsync());
    }

    private async Task<HttpResponseMessage> SendRawAsync(string url, string session, string json)
    {
        var (csrfCookie, csrfToken) = await GetCsrfAsync(session);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", $"{session}; {csrfCookie}");
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return await Client.SendAsync(request);
    }

    private static async Task AssertValidationErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"esperado erro em '{field}'");
    }

    private sealed record IdDto(Guid Id);

    private sealed record AccountRefDto(Guid Id, string Name, string FinancialScope, bool IsActive);

    private sealed record TransferDto(
        Guid Id, string Amount, DateOnly TransferDate, string Description, AccountRefDto SourceAccount,
        AccountRefDto DestinationAccount, bool IsCancelled, string Currency, DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt);

    private sealed record PageDto(List<TransferDto> Items, int Page, int PageSize, int TotalCount);

    private sealed record BalanceDto(
        string? OpeningBalance, string? TotalIncome, string? TotalExpense, string? TotalTransferIn, string? TotalTransferOut,
        string? Balance, string? UnavailableReason);
}

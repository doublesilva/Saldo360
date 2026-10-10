using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Lançamentos sobre PostgreSQL real: dois membros (via convite) e uma família estranha com conta e categoria próprias.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FinancialTransactionEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private string _member = null!;
    private string _outsider = null!;
    private FamilyDto _family = null!;
    private FamilyDto _otherFamily = null!;
    private Guid _itau;
    private Guid _nubank;
    private Guid _salario;
    private Guid _mercado;
    private Guid _foreignAccount;
    private Guid _foreignCategory;

    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);

    private string TransactionsUrl => $"/api/families/{_family.Id}/transactions";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("dona");
        _member = await SignInAsync("membro");
        _outsider = await SignInAsync("estranho");
        var created = await CreateFamilyAsync(_owner, "Família Silva");
        _family = await JoinByInvitationAsync(_owner, created.Id, _member);
        _otherFamily = await CreateFamilyAsync(_outsider, "Família Estranha");

        _itau = await CreateAccountAsync(_owner, _family.Id, "Itaú PF");
        _nubank = await CreateAccountAsync(_owner, _family.Id, "Nubank");
        _salario = await CreateCategoryAsync(_owner, _family.Id, "Salário", "Income");
        _mercado = await CreateCategoryAsync(_owner, _family.Id, "Mercado", "Expense");
        _foreignAccount = await CreateAccountAsync(_outsider, _otherFamily.Id, "Conta Estranha");
        _foreignCategory = await CreateCategoryAsync(_outsider, _otherFamily.Id, "Categoria Estranha", "Expense");
    }

    [Fact]
    public async Task IncomeAndExpense_PersistWithExactCentsAndDate()
    {
        await ArrangeAsync();

        var income = await SendRawAsync(HttpMethod.Post, TransactionsUrl, _owner,
            $$"""{ "accountId": "{{_itau}}", "categoryId": "{{_salario}}", "type": "Income", "description": "  Salário outubro ", "amount": "5432.10", "transactionDate": "2026-10-05" }""");
        var expense = await SendRawAsync(HttpMethod.Post, TransactionsUrl, _member,
            $$"""{ "accountId": "{{_itau}}", "categoryId": "{{_mercado}}", "type": "Expense", "description": "Feira", "amount": 0.01, "transactionDate": "2026-10-06" }""");

        Assert.Equal(HttpStatusCode.Created, income.StatusCode);
        Assert.Equal(HttpStatusCode.Created, expense.StatusCode);
        var created = await ReadAsync<TransactionDto>(income);
        Assert.Equal($"{TransactionsUrl}/{created.Id}", income.Headers.Location!.ToString());
        Assert.Equal(("Income", 5432.10m, new DateOnly(2026, 10, 5), "Salário outubro", "BRL", false),
            (created.Type, created.Amount, created.TransactionDate, created.Description, created.Currency, created.IsCancelled));
        Assert.Equal((_itau, "Itaú PF", _salario, "Salário"), (created.Account.Id, created.Account.Name, created.Category.Id, created.Category.Name));

        var reloaded = await ReadAsync<TransactionDto>(await SendAsync(HttpMethod.Get, $"{TransactionsUrl}/{created.Id}", _member));
        Assert.Equal((5432.10m, new DateOnly(2026, 10, 5)), (reloaded.Amount, reloaded.TransactionDate));

        await using var db = Db();
        var texts = await db.Database.SqlQueryRaw<string>(
            "SELECT type || ' ' || amount::text || ' ' || transaction_date::text AS \"Value\" FROM financial_transactions ORDER BY transaction_date").ToListAsync();
        Assert.Equal(["Income 5432.10 2026-10-05", "Expense 0.01 2026-10-06"], texts);
        var stored = await db.FinancialTransactions.SingleAsync(t => t.Id == created.Id);
        Assert.Equal(_family.Id, stored.FamilyId);
        Assert.Equal(await UserIdAsync("dona"), stored.CreatedByUserId);
        Assert.Null(stored.UpdatedAt);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-10")]
    [InlineData("10.005")]
    [InlineData("1.500")]
    [InlineData("10000000000000")]
    [InlineData("\"12.345\"")]
    public async Task InvalidAmount_Returns400_WithoutRounding(string amount)
    {
        await ArrangeAsync();

        var response = await SendRawAsync(HttpMethod.Post, TransactionsUrl, _owner,
            $$"""{ "accountId": "{{_itau}}", "categoryId": "{{_mercado}}", "type": "Expense", "description": "X", "amount": {{amount}}, "transactionDate": "2026-10-01" }""");

        await AssertValidationErrorAsync(response, "amount");
        await AssertNoTransactionsAsync();
    }

    [Fact]
    public async Task FutureDate_InSaoPaulo_IsRejected_TodayIsAccepted()
    {
        await ArrangeAsync();

        await AssertValidationErrorAsync(await PostAsync(_owner, Body(_itau, _mercado, "Expense", date: Today.AddDays(1))), "transactionDate");
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(_owner, Body(_itau, _mercado, "Expense", date: Today))).StatusCode);
    }

    [Theory]
    [InlineData("\"2026-10-01T10:00:00\"")]
    [InlineData("\"01/10/2026\"")]
    [InlineData("null")]
    public async Task DateWithTimeOrMissing_Returns400(string date)
    {
        await ArrangeAsync();

        var response = await SendRawAsync(HttpMethod.Post, TransactionsUrl, _owner,
            $$"""{ "accountId": "{{_itau}}", "categoryId": "{{_mercado}}", "type": "Expense", "description": "X", "amount": 10, "transactionDate": {{date}} }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoTransactionsAsync();
    }

    [Fact]
    public async Task DateBeforeOpeningBalance_IsAccepted_ForHistory()
    {
        await ArrangeAsync();
        await UpdateAccountAsync(_itau, "Itaú PF", isActive: true, openingBalance: 1000m, openingBalanceDate: "2026-10-01");

        var response = await PostAsync(_owner, Body(_itau, _mercado, "Expense", date: new DateOnly(2026, 9, 15)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("Income", "mercado")]
    [InlineData("Expense", "salario")]
    public async Task CategoryOfOtherType_Returns400(string type, string category)
    {
        await ArrangeAsync();

        var response = await PostAsync(_owner, Body(_itau, category == "mercado" ? _mercado : _salario, type));

        await AssertValidationErrorAsync(response, "categoryId");
        await AssertNoTransactionsAsync();
    }

    [Fact]
    public async Task ReferencesFromAnotherFamily_Return400_WithGenericMessage()
    {
        await ArrangeAsync();

        var foreignAccount = await PostAsync(_owner, Body(_foreignAccount, _mercado, "Expense"));
        var foreignCategory = await PostAsync(_owner, Body(_itau, _foreignCategory, "Expense"));
        var unknownAccount = await PostAsync(_owner, Body(Guid.CreateVersion7(), _mercado, "Expense"));

        foreach (var (response, field, message) in new[]
        {
            (foreignAccount, "accountId", "Conta inválida."),
            (foreignCategory, "categoryId", "Categoria inválida."),
            (unknownAccount, "accountId", "Conta inválida."),
        })
        {
            await AssertValidationErrorAsync(response, field);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains(message, text);
            Assert.DoesNotContain("Estranha", text);
            Assert.DoesNotContain(_foreignAccount.ToString(), text);
            Assert.DoesNotContain(_foreignCategory.ToString(), text);
        }

        await AssertNoTransactionsAsync();
    }

    [Fact]
    public async Task InactiveAccountOrCategory_IsRejected_OnCreate()
    {
        await ArrangeAsync();
        await UpdateAccountAsync(_nubank, "Nubank", isActive: false);
        await UpdateCategoryAsync(_mercado, "Mercado", isActive: false);

        await AssertValidationErrorAsync(await PostAsync(_owner, Body(_nubank, _salario, "Income")), "accountId");
        await AssertValidationErrorAsync(await PostAsync(_owner, Body(_itau, _mercado, "Expense")), "categoryId");
        await AssertNoTransactionsAsync();
    }

    [Fact]
    public async Task History_SurvivesDeactivation_AndEditMayKeepInactiveReferences_ButNotChooseNewOnes()
    {
        await ArrangeAsync();
        var created = await ReadAsync<TransactionDto>(await PostAsync(_owner, Body(_itau, _mercado, "Expense", description: "Feira")));
        var otherCategory = await CreateCategoryAsync(_owner, _family.Id, "Feira livre", "Expense");
        await UpdateAccountAsync(_itau, "Itaú PF", isActive: false);
        await UpdateAccountAsync(_nubank, "Nubank", isActive: false);
        await UpdateCategoryAsync(_mercado, "Mercado", isActive: false);
        await UpdateCategoryAsync(otherCategory, "Feira livre", isActive: false);
        var url = $"{TransactionsUrl}/{created.Id}";

        var listed = Assert.Single((await ListAsync(_member)).Items);
        Assert.Equal((false, false), (listed.Account.IsActive, listed.Category.IsActive));
        Assert.Equal("Itaú PF", listed.Account.Name);

        var kept = await SendWithCsrfAsync(HttpMethod.Put, url, _member, Body(_itau, _mercado, "Expense", description: "Feira do sábado", amount: 87.35m));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        var keptDto = await ReadAsync<TransactionDto>(kept);
        Assert.Equal(("Feira do sábado", 87.35m, false, false),
            (keptDto.Description, keptDto.Amount, keptDto.Account.IsActive, keptDto.Category.IsActive));

        await AssertValidationErrorAsync(await SendWithCsrfAsync(HttpMethod.Put, url, _member, Body(_nubank, _mercado, "Expense")), "accountId");
        await AssertValidationErrorAsync(await SendWithCsrfAsync(HttpMethod.Put, url, _member, Body(_itau, otherCategory, "Expense")), "categoryId");

        await using var db = Db();
        var stored = await db.FinancialTransactions.SingleAsync();
        Assert.Equal((_itau, _mercado, 87.35m, "Feira do sábado"), (stored.AccountId, stored.CategoryId, stored.Amount, stored.Description));
        Assert.Equal(await UserIdAsync("membro"), stored.UpdatedByUserId);
        Assert.NotNull(stored.UpdatedAt);
        Assert.Equal(await UserIdAsync("dona"), stored.CreatedByUserId);
    }

    [Fact]
    public async Task Update_CanChangeTypeAndCategoryTogether_ButNotToMismatchedPair()
    {
        await ArrangeAsync();
        var created = await ReadAsync<TransactionDto>(await PostAsync(_owner, Body(_itau, _mercado, "Expense")));
        var url = $"{TransactionsUrl}/{created.Id}";

        await AssertValidationErrorAsync(await SendWithCsrfAsync(HttpMethod.Put, url, _owner, Body(_itau, _mercado, "Income")), "categoryId");

        var changed = await ReadAsync<TransactionDto>(await SendWithCsrfAsync(HttpMethod.Put, url, _owner, Body(_nubank, _salario, "Income")));
        Assert.Equal(("Income", _salario, _nubank), (changed.Type, changed.Category.Id, changed.Account.Id));
    }

    [Fact]
    public async Task Cancel_IsIdempotent_PreservesRow_HidesByDefault_AndBlocksEdit()
    {
        await ArrangeAsync();
        var created = await ReadAsync<TransactionDto>(await PostAsync(_owner, Body(_itau, _mercado, "Expense")));
        var url = $"{TransactionsUrl}/{created.Id}";

        var first = await SendWithCsrfAsync(HttpMethod.Post, $"{url}/cancel", _member);
        var second = await SendWithCsrfAsync(HttpMethod.Post, $"{url}/cancel", _owner);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        var cancelled = await ReadAsync<TransactionDto>(first);
        Assert.True(cancelled.IsCancelled);
        Assert.Equal(cancelled.CancelledAt, (await ReadAsync<TransactionDto>(second)).CancelledAt);

        Assert.Empty((await ListAsync(_owner)).Items);
        Assert.True(Assert.Single((await ListAsync(_owner, "includeCancelled=true")).Items).IsCancelled);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, url, _owner)).StatusCode);

        var edit = await SendWithCsrfAsync(HttpMethod.Put, url, _owner, Body(_itau, _mercado, "Expense", description: "Editado"));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);

        await using var db = Db();
        var stored = await db.FinancialTransactions.SingleAsync();
        Assert.Equal((true, "Compra", await UserIdAsync("membro")), (stored.IsCancelled, stored.Description, stored.CancelledByUserId));
    }

    [Fact]
    public async Task ClientCannotSetServerControlledFields()
    {
        await ArrangeAsync();
        var created = await ReadAsync<TransactionDto>(await PostAsync(_owner, Body(_itau, _mercado, "Expense")));
        var url = $"{TransactionsUrl}/{created.Id}";
        const string Base = "\"categoryId\": \"{0}\", \"accountId\": \"{1}\", \"type\": \"Expense\", \"description\": \"X\", \"amount\": 1, \"transactionDate\": \"2026-10-01\"";
        var fields = string.Format(System.Globalization.CultureInfo.InvariantCulture, Base, _mercado, _itau);

        var responses = new[]
        {
            await SendRawAsync(HttpMethod.Post, TransactionsUrl, _owner, $$"""{ {{fields}}, "familyId": "{{_otherFamily.Id}}" }"""),
            await SendRawAsync(HttpMethod.Post, TransactionsUrl, _owner, $$"""{ {{fields}}, "isCancelled": true }"""),
            await SendRawAsync(HttpMethod.Post, TransactionsUrl, _owner, $$"""{ {{fields}}, "currency": "USD" }"""),
            await SendRawAsync(HttpMethod.Put, url, _owner, $$"""{ {{fields}}, "isCancelled": true }"""),
            await SendRawAsync(HttpMethod.Put, url, _owner, $$"""{ {{fields}}, "createdByUserId": "{{Guid.CreateVersion7()}}" }"""),
            await SendRawAsync(HttpMethod.Put, url, _owner, $$"""{ {{fields}}, "familyId": "{{_otherFamily.Id}}" }"""),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        await using var db = Db();
        var stored = await db.FinancialTransactions.SingleAsync();
        Assert.Equal((_family.Id, false, "Compra"), (stored.FamilyId, stored.IsCancelled, stored.Description));
    }

    [Fact]
    public async Task Outsider_CannotReadOrChangeTransactions()
    {
        await ArrangeAsync();
        var created = await ReadAsync<TransactionDto>(await PostAsync(_owner, Body(_itau, _mercado, "Expense")));
        var url = $"{TransactionsUrl}/{created.Id}";
        var otherRoute = $"/api/families/{_otherFamily.Id}/transactions/{created.Id}";

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, TransactionsUrl, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, url, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(_outsider, Body(_itau, _mercado, "Expense"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, url, _outsider, Body(_itau, _mercado, "Expense"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"{url}/cancel", _outsider)).StatusCode);

        // Pela rota da própria família, o lançamento alheio também não existe.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, otherRoute, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendWithCsrfAsync(HttpMethod.Put, otherRoute, _outsider, Body(_foreignAccount, _foreignCategory, "Expense"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"{otherRoute}/cancel", _outsider)).StatusCode);

        await using var db = Db();
        var stored = await db.FinancialTransactions.SingleAsync();
        Assert.Equal((false, "Compra", (DateTimeOffset?)null), (stored.IsCancelled, stored.Description, stored.UpdatedAt));
    }

    [Fact]
    public async Task WriteOperations_RequireCsrf()
    {
        await ArrangeAsync();
        var created = await ReadAsync<TransactionDto>(await PostAsync(_owner, Body(_itau, _mercado, "Expense")));
        var url = $"{TransactionsUrl}/{created.Id}";

        var post = await SendJsonAsync(HttpMethod.Post, TransactionsUrl, _owner, csrf: null, Body(_itau, _mercado, "Expense"));
        var put = await SendJsonAsync(HttpMethod.Put, url, _owner, csrf: null, Body(_itau, _mercado, "Expense", description: "Alterado"));
        var cancel = await SendJsonAsync(HttpMethod.Post, $"{url}/cancel", _owner, csrf: null, body: null);

        Assert.All(new[] { post, put, cancel }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        await using var db = Db();
        var stored = await db.FinancialTransactions.SingleAsync();
        Assert.Equal(("Compra", false), (stored.Description, stored.IsCancelled));
    }

    [Fact]
    public async Task List_FiltersAndPaginates_WithStableOrder_WithoutMixingFamilies()
    {
        await ArrangeAsync();
        var ids = new List<(Guid Id, DateOnly Date)>();
        async Task AddAsync(Guid account, Guid category, string type, DateOnly date)
        {
            var t = await ReadAsync<TransactionDto>(await PostAsync(_owner, Body(account, category, type, date: date)));
            ids.Add((t.Id, date));
        }

        await AddAsync(_itau, _salario, "Income", new DateOnly(2026, 9, 1));
        await AddAsync(_itau, _mercado, "Expense", new DateOnly(2026, 9, 10));
        await AddAsync(_nubank, _mercado, "Expense", new DateOnly(2026, 9, 10));
        await AddAsync(_nubank, _mercado, "Expense", new DateOnly(2026, 9, 10));
        await AddAsync(_itau, _mercado, "Expense", new DateOnly(2026, 10, 1));
        var foreign = await ReadAsync<TransactionDto>(await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_otherFamily.Id}/transactions",
            _outsider, Body(_foreignAccount, _foreignCategory, "Expense", date: new DateOnly(2026, 9, 10))));

        // Ordem: data decrescente, depois Id decrescente (Guid v7 crescente na criação).
        var expected = ids.OrderByDescending(x => x.Date).ThenByDescending(x => x.Id.ToString()).Select(x => x.Id).ToList();
        var pages = new[] { await ListAsync(_member, "pageSize=2&page=1"), await ListAsync(_member, "pageSize=2&page=2"), await ListAsync(_member, "pageSize=2&page=3") };
        Assert.All(pages, p => Assert.Equal(5, p.TotalCount));
        Assert.Equal([2, 2, 1], pages.Select(p => p.Items.Count));
        Assert.Equal(expected, pages.SelectMany(p => p.Items).Select(t => t.Id));
        Assert.DoesNotContain(foreign.Id, pages.SelectMany(p => p.Items).Select(t => t.Id));
        Assert.Empty((await ListAsync(_member, "page=4&pageSize=2")).Items);

        Assert.Equal(3, (await ListAsync(_owner, "from=2026-09-10&to=2026-09-10")).TotalCount);
        Assert.Equal(4, (await ListAsync(_owner, "from=2026-09-05")).TotalCount);
        Assert.Equal(1, (await ListAsync(_owner, "to=2026-09-01")).TotalCount);
        Assert.Equal(2, (await ListAsync(_owner, $"accountId={_nubank}")).TotalCount);
        Assert.Equal(1, (await ListAsync(_owner, $"categoryId={_salario}")).TotalCount);
        Assert.Equal(4, (await ListAsync(_owner, "type=Expense")).TotalCount);
        Assert.Equal(2, (await ListAsync(_owner, $"type=Expense&accountId={_itau}")).TotalCount);
        Assert.Equal(0, (await ListAsync(_owner, $"accountId={_foreignAccount}")).TotalCount);
        Assert.Equal(1, (await ListAsync(_outsider, string.Empty, _otherFamily.Id)).TotalCount);
        Assert.Equal(50, (await ListAsync(_owner)).PageSize);
        Assert.Equal(100, (await ListAsync(_owner, "pageSize=100")).PageSize);
    }

    [Theory]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("from=2026-10-02&to=2026-10-01", "from")]
    [InlineData("type=expense", "type")]
    [InlineData("from=01/09/2026", "from")] // não pode virar 9 de janeiro
    [InlineData("to=2026-9-1", "to")]
    [InlineData("to=2026-02-30", "to")]
    public async Task List_InvalidFilters_Return400(string query, string field)
    {
        await ArrangeAsync();

        await AssertValidationErrorAsync(await SendAsync(HttpMethod.Get, $"{TransactionsUrl}?{query}", _owner), field);
    }

    [Fact]
    public async Task Responses_DoNotExposeUsersOrFamily()
    {
        await ArrangeAsync();
        await PostAsync(_owner, Body(_itau, _mercado, "Expense"));

        var body = await (await SendAsync(HttpMethod.Get, TransactionsUrl, _owner)).Content.ReadAsStringAsync();

        foreach (var forbidden in new[] { "familyId", "createdBy", "updatedBy", "cancelledBy", "userId", _family.Id.ToString() })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/api/families/{Guid.CreateVersion7()}/transactions")).StatusCode);
    }

    private static object Body(
        Guid account, Guid category, string type, string description = "Compra", decimal amount = 123.45m, DateOnly? date = null) =>
        new { accountId = account, categoryId = category, type, description, amount, transactionDate = date ?? new DateOnly(2026, 10, 1) };

    private Task<HttpResponseMessage> PostAsync(string session, object body) =>
        SendWithCsrfAsync(HttpMethod.Post, TransactionsUrl, session, body);

    private async Task<PageDto> ListAsync(string session, string query = "", Guid? familyId = null)
    {
        var url = $"/api/families/{familyId ?? _family.Id}/transactions" + (query.Length > 0 ? $"?{query}" : string.Empty);
        var response = await SendAsync(HttpMethod.Get, url, session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PageDto>(response);
    }

    private async Task<Guid> CreateAccountAsync(string session, Guid familyId, string name)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/accounts", session,
            new { name, type = "Checking", financialScope = "PF" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateCategoryAsync(string session, Guid familyId, string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/categories", session, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task UpdateAccountAsync(
        Guid accountId, string name, bool isActive, decimal? openingBalance = null, string? openingBalanceDate = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Put, $"/api/families/{_family.Id}/accounts/{accountId}", _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate, isActive });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task UpdateCategoryAsync(Guid categoryId, string name, bool isActive)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Put, $"/api/families/{_family.Id}/categories/{categoryId}", _owner, new { name, isActive });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> UserIdAsync(string subject)
    {
        await using var db = Db();
        return await db.Users.Where(u => u.Subject == subject).Select(u => u.Id).SingleAsync();
    }

    private async Task AssertNoTransactionsAsync()
    {
        await using var db = Db();
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
    }

    /// <summary>Envia o JSON exatamente como escrito (números com casas extras, datas com horário).</summary>
    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, string session, string json)
    {
        var (csrfCookie, csrfToken) = await GetCsrfAsync(session);
        var request = new HttpRequestMessage(method, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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

    private sealed record ReferenceDto(Guid Id, string Name, bool IsActive);

    private sealed record TransactionDto(
        Guid Id, string Type, decimal Amount, DateOnly TransactionDate, string Description,
        ReferenceDto Account, ReferenceDto Category, bool IsCancelled, string Currency,
        DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? CancelledAt);

    private sealed record PageDto(List<TransactionDto> Items, int Page, int PageSize, int TotalCount);
}

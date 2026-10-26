using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Receitas e despesas previstas sobre PostgreSQL real: dois membros (via convite) e uma família estranha com conta e
/// categoria próprias. Previsões nunca viram lançamentos nem mexem no saldo realizado.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
public sealed class FinancialPlannedTransactionEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static readonly DateOnly Reference = new(2026, 9, 1);

    private string _owner = null!;
    private string _ownerSecondSession = null!;
    private string _outsider = null!;
    private WorkspaceDto _family = null!;
    private WorkspaceDto _otherFamily = null!;
    private Guid _itau;
    private Guid _nubank;
    private Guid _salario;
    private Guid _aluguel;
    private Guid _foreignAccount;
    private Guid _foreignCategory;

    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);

    private string PlannedUrl => $"/api/financial-workspaces/{_family.Id}/planned-transactions";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("dona");
        _ownerSecondSession = _owner;
        _outsider = await SignInAsync("estranho");
        var created = await CreateWorkspaceAsync(_owner, "Família Silva");
        _family = created;
        _otherFamily = await CreateWorkspaceAsync(_outsider, "Família Estranha");

        _itau = await CreateAccountAsync(_owner, _family.Id, "Itaú PF", "1000.00");
        _nubank = await CreateAccountAsync(_owner, _family.Id, "Nubank", "0.00");
        _salario = await CreateCategoryAsync(_owner, _family.Id, "Salário", "Income");
        _aluguel = await CreateCategoryAsync(_owner, _family.Id, "Aluguel", "Expense");
        _foreignAccount = await CreateAccountAsync(_outsider, _otherFamily.Id, "Conta Estranha", "0.00");
        _foreignCategory = await CreateCategoryAsync(_outsider, _otherFamily.Id, "Categoria Estranha", "Expense");
    }

    [Fact]
    public async Task FutureIncome_And_OverdueExpense_PersistExactly_StayPending_AndNeverTouchRealizedBalance()
    {
        await ArrangeAsync();
        var before = await BalanceJsonAsync(_itau);

        var income = await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _ownerSecondSession,
            Body(_itau, _salario, "Income", "5432.10", Today.AddDays(20), "  Salário de novembro "));
        var expense = await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
            Body(_itau, _aluguel, "Expense", "1800.00", Today.AddDays(-10), "Aluguel"));

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (income.StatusCode, expense.StatusCode));
        var created = await ReadAsync<PlannedDto>(income);
        Assert.Equal($"{PlannedUrl}/{created.Id}", income.Headers.Location!.ToString());
        Assert.Equal(("Income", "5432.10", Today.AddDays(20), "Salário de novembro", "BRL", false),
            (created.Type, created.Amount, created.DueDate, created.Description, created.Currency, created.IsCancelled));
        var overdue = await ReadAsync<PlannedDto>(expense);
        Assert.False(overdue.IsCancelled); // vencida continua pendente

        await using (var db = Db())
        {
            var texts = await db.Database.SqlQueryRaw<string>(
                "SELECT type || ' ' || amount::text || ' ' || due_date::text AS \"Value\" FROM financial_planned_transactions ORDER BY due_date").ToListAsync();
            Assert.Equal([$"Expense 1800.00 {Today.AddDays(-10):yyyy-MM-dd}", $"Income 5432.10 {Today.AddDays(20):yyyy-MM-dd}"], texts);
            Assert.Equal(0, await db.FinancialTransactions.CountAsync()); // nada vira lançamento realizado
            var stored = await db.FinancialPlannedTransactions.SingleAsync(p => p.Id == created.Id);
            Assert.Equal((_family.Id, await UserIdAsync("dona")), (stored.WorkspaceId, stored.CreatedByUserId));
        }

        // Editar e cancelar também não mexem no saldo realizado.
        await SendWithCsrfAsync(HttpMethod.Put, $"{PlannedUrl}/{overdue.Id}", _ownerSecondSession,
            Body(_itau, _aluguel, "Expense", "1850.00", Today.AddDays(-10), "Aluguel reajustado"));
        await SendWithCsrfAsync(HttpMethod.Post, $"{PlannedUrl}/{created.Id}/cancel", _owner);

        Assert.Equal(before, await BalanceJsonAsync(_itau));
        await using var verify = Db();
        Assert.Equal(0, await verify.FinancialTransactions.CountAsync());
    }

    [Theory]
    [InlineData("0.10")]
    [InlineData("1234.56")]
    [InlineData("9999999999999.99")]
    public async Task Amount_IsAnExactString_InEveryResponse(string amount)
    {
        await ArrangeAsync();
        var body = Body(_itau, _aluguel, "Expense", amount, Today.AddDays(5));

        var id = await AssertAmountAsync(await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner, body), amount);
        var url = $"{PlannedUrl}/{id}";
        await AssertAmountAsync(await SendAsync(HttpMethod.Get, url, _ownerSecondSession), amount);
        using (var list = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, PlannedUrl, _ownerSecondSession)).Content.ReadAsStringAsync()))
        {
            var item = list.RootElement.GetProperty("items")[0].GetProperty("amount");
            Assert.Equal((JsonValueKind.String, amount), (item.ValueKind, item.GetString()));
        }

        await AssertAmountAsync(await SendWithCsrfAsync(HttpMethod.Put, url, _ownerSecondSession, Body(_itau, _aluguel, "Expense", amount, Today, "Editada")), amount);
        await AssertAmountAsync(await SendWithCsrfAsync(HttpMethod.Post, $"{url}/cancel", _owner), amount);
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

        var response = await SendRawAsync(HttpMethod.Post, PlannedUrl, _owner,
            $$"""{ "accountId": "{{_itau}}", "categoryId": "{{_aluguel}}", "type": "Expense", "description": "X", "amount": {{amount}}, "dueDate": "2026-12-01" }""");

        await AssertValidationErrorAsync(response, "amount");
        await AssertNothingStoredAsync();
    }

    [Theory]
    [InlineData("\"2026-12-01T10:00:00\"")]
    [InlineData("\"01/12/2026\"")]
    [InlineData("null")]
    public async Task DueDateWithTimeOrMissing_Returns400(string date)
    {
        await ArrangeAsync();

        var response = await SendRawAsync(HttpMethod.Post, PlannedUrl, _owner,
            $$"""{ "accountId": "{{_itau}}", "categoryId": "{{_aluguel}}", "type": "Expense", "description": "X", "amount": "10.00", "dueDate": {{date}} }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task FarFutureAndPastDates_AreAccepted()
    {
        await ArrangeAsync();

        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
            Body(_itau, _aluguel, "Expense", "1.00", new DateOnly(2030, 1, 31)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
            Body(_itau, _aluguel, "Expense", "1.00", new DateOnly(2020, 1, 31)))).StatusCode);
    }

    [Theory]
    [InlineData("Income", "aluguel")]
    [InlineData("Expense", "salario")]
    public async Task CategoryOfOtherType_Returns400(string type, string category)
    {
        await ArrangeAsync();

        var response = await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
            Body(_itau, category == "aluguel" ? _aluguel : _salario, type, "10.00", Today));

        await AssertValidationErrorAsync(response, "categoryId");
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task ReferencesFromAnotherFamily_Return400_WithGenericMessage()
    {
        await ArrangeAsync();

        var foreignAccount = await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner, Body(_foreignAccount, _aluguel, "Expense", "1.00", Today));
        var foreignCategory = await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner, Body(_itau, _foreignCategory, "Expense", "1.00", Today));

        foreach (var (response, field, message) in new[] { (foreignAccount, "accountId", "Conta inválida."), (foreignCategory, "categoryId", "Categoria inválida.") })
        {
            await AssertValidationErrorAsync(response, field);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains(message, text);
            Assert.DoesNotContain("Estranha", text);
            Assert.DoesNotContain(_foreignAccount.ToString(), text);
            Assert.DoesNotContain(_foreignCategory.ToString(), text);
        }

        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task InactiveReferences_RejectedOnCreate_KeptOnEdit_NewInactiveRejected_HistoryVisible()
    {
        await ArrangeAsync();
        var created = await ReadAsync<PlannedDto>(await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
            Body(_itau, _aluguel, "Expense", "1800.00", Today.AddDays(5))));
        var condominio = await CreateCategoryAsync(_owner, _family.Id, "Condomínio", "Expense");
        await DeactivateAccountAsync(_itau, "Itaú PF", "1000.00");
        await DeactivateAccountAsync(_nubank, "Nubank", "0.00");
        await DeactivateCategoryAsync(_aluguel, "Aluguel");
        await DeactivateCategoryAsync(condominio, "Condomínio");
        var url = $"{PlannedUrl}/{created.Id}";

        await AssertValidationErrorAsync(await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner, Body(_itau, _salario, "Income", "1.00", Today)), "accountId");

        var listed = Assert.Single((await ListAsync(_ownerSecondSession)).Items);
        Assert.Equal((false, false, "Itaú PF", "Aluguel"), (listed.Account.IsActive, listed.Category.IsActive, listed.Account.Name, listed.Category.Name));

        var kept = await SendWithCsrfAsync(HttpMethod.Put, url, _ownerSecondSession, Body(_itau, _aluguel, "Expense", "1850.00", Today.AddDays(6), "Aluguel reajustado"));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        await AssertValidationErrorAsync(await SendWithCsrfAsync(HttpMethod.Put, url, _ownerSecondSession, Body(_nubank, _aluguel, "Expense", "1.00", Today)), "accountId");
        await AssertValidationErrorAsync(await SendWithCsrfAsync(HttpMethod.Put, url, _ownerSecondSession, Body(_itau, condominio, "Expense", "1.00", Today)), "categoryId");

        await using var db = Db();
        var stored = await db.FinancialPlannedTransactions.SingleAsync();
        Assert.Equal((_itau, _aluguel, 1850.00m, "Aluguel reajustado", Today.AddDays(6)),
            (stored.AccountId, stored.CategoryId, stored.Amount, stored.Description, stored.DueDate));
        Assert.Equal((await UserIdAsync("dona"), await UserIdAsync("dona")), (stored.UpdatedByUserId, stored.CreatedByUserId));
        Assert.NotNull(stored.UpdatedAt);
    }

    [Fact]
    public async Task Cancel_IsIdempotent_PreservesRow_HidesByDefault_AndBlocksEdit()
    {
        await ArrangeAsync();
        var created = await ReadAsync<PlannedDto>(await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
            Body(_itau, _aluguel, "Expense", "100.00", Today)));
        var url = $"{PlannedUrl}/{created.Id}";

        var first = await SendWithCsrfAsync(HttpMethod.Post, $"{url}/cancel", _ownerSecondSession);
        var second = await SendWithCsrfAsync(HttpMethod.Post, $"{url}/cancel", _owner);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        var cancelled = await ReadAsync<PlannedDto>(first);
        Assert.True(cancelled.IsCancelled);
        Assert.Equal(cancelled.CancelledAt, (await ReadAsync<PlannedDto>(second)).CancelledAt);
        Assert.Empty((await ListAsync(_owner)).Items);
        Assert.True(Assert.Single((await ListAsync(_owner, "includeCancelled=true")).Items).IsCancelled);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, url, _owner)).StatusCode);

        var edit = await SendWithCsrfAsync(HttpMethod.Put, url, _owner, Body(_itau, _aluguel, "Expense", "1.00", Today, "Editada"));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);

        await using var db = Db();
        var stored = await db.FinancialPlannedTransactions.SingleAsync();
        Assert.Equal((true, "Compra", await UserIdAsync("dona")), (stored.IsCancelled, stored.Description, stored.CancelledByUserId));
    }

    [Fact]
    public async Task ServerControlledFields_AreRejected()
    {
        await ArrangeAsync();
        var created = await ReadAsync<PlannedDto>(await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
            Body(_itau, _aluguel, "Expense", "1.00", Today)));
        var url = $"{PlannedUrl}/{created.Id}";
        var fields = $"\"accountId\": \"{_itau}\", \"categoryId\": \"{_aluguel}\", \"type\": \"Expense\", \"description\": \"X\", \"amount\": \"1.00\", \"dueDate\": \"2026-12-01\"";

        var responses = new[]
        {
            await SendRawAsync(HttpMethod.Post, PlannedUrl, _owner, $$"""{ {{fields}}, "workspaceId": "{{_otherFamily.Id}}" }"""),
            await SendRawAsync(HttpMethod.Post, PlannedUrl, _owner, $$"""{ {{fields}}, "isCancelled": true }"""),
            await SendRawAsync(HttpMethod.Post, PlannedUrl, _owner, $$"""{ {{fields}}, "isOverdue": true }"""),
            await SendRawAsync(HttpMethod.Post, PlannedUrl, _owner, $$"""{ {{fields}}, "currency": "USD" }"""),
            await SendRawAsync(HttpMethod.Put, url, _owner, $$"""{ {{fields}}, "isCancelled": true }"""),
            await SendRawAsync(HttpMethod.Put, url, _owner, $$"""{ {{fields}}, "createdByUserId": "{{Guid.CreateVersion7()}}" }"""),
            await SendRawAsync(HttpMethod.Put, url, _owner, $$"""{ {{fields}}, "workspaceId": "{{_otherFamily.Id}}" }"""),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        await using var db = Db();
        var stored = await db.FinancialPlannedTransactions.SingleAsync();
        Assert.Equal((_family.Id, false, "Compra"), (stored.WorkspaceId, stored.IsCancelled, stored.Description));
    }

    [Fact]
    public async Task Outsider_CannotReadOrChange()
    {
        await ArrangeAsync();
        var created = await ReadAsync<PlannedDto>(await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
            Body(_itau, _aluguel, "Expense", "1.00", Today)));
        var url = $"{PlannedUrl}/{created.Id}";
        var otherRoute = $"/api/financial-workspaces/{_otherFamily.Id}/planned-transactions/{created.Id}";

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, PlannedUrl, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, url, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _outsider, Body(_itau, _aluguel, "Expense", "1.00", Today))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, url, _outsider, Body(_itau, _aluguel, "Expense", "2.00", Today))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"{url}/cancel", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, otherRoute, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendWithCsrfAsync(HttpMethod.Put, otherRoute, _outsider, Body(_foreignAccount, _foreignCategory, "Expense", "2.00", Today))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"{otherRoute}/cancel", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(PlannedUrl)).StatusCode);

        await using var db = Db();
        var stored = await db.FinancialPlannedTransactions.SingleAsync();
        Assert.Equal((false, 1.00m, (DateTimeOffset?)null), (stored.IsCancelled, stored.Amount, stored.UpdatedAt));
    }

    [Fact]
    public async Task WriteOperations_RequireCsrf()
    {
        await ArrangeAsync();
        var created = await ReadAsync<PlannedDto>(await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
            Body(_itau, _aluguel, "Expense", "1.00", Today)));
        var url = $"{PlannedUrl}/{created.Id}";

        var post = await SendJsonAsync(HttpMethod.Post, PlannedUrl, _owner, csrf: null, Body(_itau, _aluguel, "Expense", "1.00", Today));
        var put = await SendJsonAsync(HttpMethod.Put, url, _owner, csrf: null, Body(_itau, _aluguel, "Expense", "9.00", Today, "Alterada"));
        var cancel = await SendJsonAsync(HttpMethod.Post, $"{url}/cancel", _owner, csrf: null, body: null);

        Assert.All(new[] { post, put, cancel }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        await using var db = Db();
        var stored = await db.FinancialPlannedTransactions.SingleAsync();
        Assert.Equal(("Compra", false), (stored.Description, stored.IsCancelled));
    }

    [Fact]
    public async Task List_FiltersByDueDateAndReferences_PaginatesAscending_WithoutMixingFamilies()
    {
        await ArrangeAsync();
        var ids = new List<(Guid Id, DateOnly Date)>();
        foreach (var (account, category, type, date) in new[]
        {
            (_itau, _salario, "Income", new DateOnly(2026, 12, 5)),
            (_itau, _aluguel, "Expense", new DateOnly(2026, 11, 10)),
            (_nubank, _aluguel, "Expense", new DateOnly(2026, 11, 10)),
            (_nubank, _aluguel, "Expense", new DateOnly(2026, 9, 1)),
            (_itau, _aluguel, "Expense", new DateOnly(2027, 1, 10)),
        })
        {
            ids.Add(((await ReadAsync<PlannedDto>(await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
                Body(account, category, type, "1.00", date)))).Id, date));
        }

        await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_otherFamily.Id}/planned-transactions", _outsider,
            Body(_foreignAccount, _foreignCategory, "Expense", "1.00", new DateOnly(2026, 11, 10)));

        var expected = ids.OrderBy(x => x.Date).ThenBy(x => x.Id.ToString()).Select(x => x.Id).ToList();
        var pages = new[] { await ListAsync(_ownerSecondSession, "pageSize=2&page=1"), await ListAsync(_ownerSecondSession, "pageSize=2&page=2"), await ListAsync(_ownerSecondSession, "pageSize=2&page=3") };
        Assert.All(pages, p => Assert.Equal(5, p.TotalCount));
        Assert.Equal(expected, pages.SelectMany(p => p.Items).Select(p => p.Id));

        Assert.Equal(2, (await ListAsync(_owner, "from=2026-11-10&to=2026-11-10")).TotalCount);
        Assert.Equal(3, (await ListAsync(_owner, "from=2026-11-01&to=2026-12-31")).TotalCount);
        Assert.Equal(2, (await ListAsync(_owner, $"accountId={_nubank}")).TotalCount);
        Assert.Equal(1, (await ListAsync(_owner, $"categoryId={_salario}")).TotalCount);
        Assert.Equal(4, (await ListAsync(_owner, "type=Expense")).TotalCount);
        Assert.Equal(0, (await ListAsync(_owner, $"accountId={_foreignAccount}")).TotalCount);
        Assert.Equal(50, (await ListAsync(_owner)).PageSize);
    }

    [Theory]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("from=2026-10-02&to=2026-10-01", "from")]
    [InlineData("type=expense", "type")]
    [InlineData("from=01/12/2026", "from")]
    [InlineData("to=2026-12-1", "to")]
    public async Task List_InvalidFilters_Return400(string query, string field)
    {
        await ArrangeAsync();

        await AssertValidationErrorAsync(await SendAsync(HttpMethod.Get, $"{PlannedUrl}?{query}", _owner), field);
    }

    private static object Body(Guid account, Guid category, string type, string amount, DateOnly dueDate, string description = "Compra") =>
        new { accountId = account, categoryId = category, type, description, amount, dueDate };

    private async Task<PageDto> ListAsync(string session, string query = "")
    {
        var response = await SendAsync(HttpMethod.Get, query.Length > 0 ? $"{PlannedUrl}?{query}" : PlannedUrl, session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PageDto>(response);
    }

    private async Task<string> BalanceJsonAsync(Guid account)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{_family.Id}/accounts/{account}/balance", _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<Guid> CreateAccountAsync(string session, Guid workspaceId, string name, string openingBalance)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspaceId}/accounts", session,
            new { name, type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate = Reference });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateCategoryAsync(string session, Guid workspaceId, string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspaceId}/categories", session, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task DeactivateAccountAsync(Guid account, string name, string openingBalance)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{_family.Id}/accounts/{account}", _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate = Reference, isActive = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task DeactivateCategoryAsync(Guid category, string name)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{_family.Id}/categories/{category}", _owner, new { name, isActive = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> UserIdAsync(string subject)
    {
        await using var db = Db();
        return await db.Users.Where(u => u.Subject == subject).Select(u => u.Id).SingleAsync();
    }

    private async Task AssertNothingStoredAsync()
    {
        await using var db = Db();
        Assert.Equal(0, await db.FinancialPlannedTransactions.CountAsync());
    }

    private static async Task<Guid> AssertAmountAsync(HttpResponseMessage response, string expected)
    {
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var amount = json.RootElement.GetProperty("amount");
        Assert.Equal((JsonValueKind.String, expected), (amount.ValueKind, amount.GetString()));
        Assert.False(json.RootElement.TryGetProperty("isOverdue", out _)); // vencimento é derivado, não persistido
        return json.RootElement.GetProperty("id").GetGuid();
    }

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

    private sealed record PlannedDto(
        Guid Id, string Type, string Amount, DateOnly DueDate, string Description, ReferenceDto Account, ReferenceDto Category,
        bool IsCancelled, string Currency, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? CancelledAt);

    private sealed record PageDto(List<PlannedDto> Items, int Page, int PageSize, int TotalCount);
}

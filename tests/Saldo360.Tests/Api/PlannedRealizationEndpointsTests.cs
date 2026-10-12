using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Realização de previsões sobre PostgreSQL real: vínculo, saldo, idempotência, concorrência e reabertura ao
/// cancelar o lançamento efetivo.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlannedRealizationEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static readonly DateOnly Reference = new(2026, 9, 1);
    private static readonly DateOnly Effective = new(2026, 9, 10);

    private string _owner = null!;
    private string _member = null!;
    private string _outsider = null!;
    private FamilyDto _family = null!;
    private FamilyDto _otherFamily = null!;
    private Guid _itau;
    private Guid _aluguel;

    private string PlannedUrl => $"/api/families/{_family.Id}/planned-transactions";

    private string TransactionsUrl => $"/api/families/{_family.Id}/transactions";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("dona");
        _member = await SignInAsync("membro");
        _outsider = await SignInAsync("estranho");
        var created = await CreateFamilyAsync(_owner, "Família Silva");
        _family = await JoinByInvitationAsync(_owner, created.Id, _member);
        _otherFamily = await CreateFamilyAsync(_outsider, "Família Estranha");
        _itau = await CreateAccountAsync("Itaú PF", "1000.00");
        _aluguel = await CreateCategoryAsync("Aluguel", "Expense");
    }

    [Fact]
    public async Task Acceptance_Planned200_DoesNotChangeBalance_Realized210_Gives790_AndKeepsThePlanned200()
    {
        await ArrangeAsync();
        var planned = await CreatePlannedAsync("200.00", new DateOnly(2026, 9, 5));
        Assert.Equal("1000.00", await BalanceAsync());

        var response = await RealizeAsync(planned, "210.00", Effective, _member);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transaction = await ReadAsync<TransactionDto>(response);
        Assert.Equal($"{TransactionsUrl}/{transaction.Id}", response.Headers.Location!.ToString());
        Assert.Equal(("Expense", "210.00", Effective, "Aluguel de setembro", _itau, _aluguel, planned),
            (transaction.Type, transaction.Amount, transaction.TransactionDate, transaction.Description,
             transaction.Account.Id, transaction.Category.Id, transaction.PlannedTransactionId));
        Assert.Equal("790.00", await BalanceAsync());

        var after = await GetPlannedAsync(planned);
        Assert.Equal(("Realized", "200.00", new DateOnly(2026, 9, 5)), (after.Status, after.Amount, after.DueDate));
        Assert.Equal((transaction.Id, "210.00", Effective), (after.Realization!.TransactionId, after.Realization.Amount, after.Realization.TransactionDate));

        await using var db = Db();
        var stored = await db.FinancialTransactions.SingleAsync();
        Assert.Equal((await UserIdAsync("membro"), planned), (stored.CreatedByUserId, stored.PlannedTransactionId));
        var plannedRow = await db.FinancialPlannedTransactions.SingleAsync();
        Assert.Equal((200.00m, new DateOnly(2026, 9, 5), await UserIdAsync("dona")), (plannedRow.Amount, plannedRow.DueDate, plannedRow.CreatedByUserId));
    }

    [Fact]
    public async Task Repeat_WithSameData_ReturnsTheExistingTransaction_DifferentData_Is409()
    {
        await ArrangeAsync();
        var planned = await CreatePlannedAsync("200.00");
        var first = await ReadAsync<TransactionDto>(await RealizeAsync(planned, "210.00", Effective));

        var same = await RealizeAsync(planned, "210", Effective, _member); // 210 == 210.00
        var otherAmount = await RealizeAsync(planned, "211.00", Effective);
        var otherDate = await RealizeAsync(planned, "210.00", Effective.AddDays(1));

        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        var repeated = await ReadAsync<TransactionDto>(same);
        Assert.Equal((first.Id, first.CreatedAt), (repeated.Id, repeated.CreatedAt));
        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (otherAmount.StatusCode, otherDate.StatusCode));
        Assert.Contains("já foi realizada", await otherAmount.Content.ReadAsStringAsync());

        await using var db = Db();
        var stored = await db.FinancialTransactions.SingleAsync();
        Assert.Equal((await UserIdAsync("dona"), (DateTimeOffset?)null), (stored.CreatedByUserId, stored.UpdatedAt)); // auditoria intacta
    }

    [Fact]
    public async Task ConcurrentRealizations_CreateExactlyOneTransaction()
    {
        await ArrangeAsync();
        var planned = await CreatePlannedAsync("200.00");

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            RealizeAsync(planned, i % 3 == 0 ? "210.00" : "210", Effective, i % 2 == 0 ? _owner : _member)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await ReadAsync<TransactionDto>(r)).Id));
        Assert.Single(ids.Distinct());
        await using var db = Db();
        Assert.Equal(1, await db.FinancialTransactions.CountAsync());
        Assert.Equal("790.00", await BalanceAsync());
    }

    [Fact]
    public async Task ConcurrentRealizations_WithDifferentAmounts_StillCreateOne_OthersGet409()
    {
        await ArrangeAsync();
        var planned = await CreatePlannedAsync("200.00");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => RealizeAsync(planned, $"{200 + i}.00", Effective)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        await using var db = Db();
        Assert.Equal(1, await db.FinancialTransactions.CountAsync());
    }

    [Fact]
    public async Task RealizeRacingCancelOfThePlanned_EndsConsistent()
    {
        await ArrangeAsync();
        for (var round = 0; round < 6; round++)
        {
            var planned = await CreatePlannedAsync("200.00");

            var realize = RealizeAsync(planned, "210.00", Effective);
            var cancel = SendWithCsrfAsync(HttpMethod.Post, $"{PlannedUrl}/{planned}/cancel", _member);
            var (realized, cancelled) = (await realize, await cancel);

            var state = await GetPlannedAsync(planned);
            await using var db = Db();
            var active = await db.FinancialTransactions.CountAsync(t => t.PlannedTransactionId == planned && !t.IsCancelled);
            if (realized.StatusCode == HttpStatusCode.Created)
            {
                // Realizou antes: cancelar a previsão realizada é recusado.
                Assert.Equal((HttpStatusCode.Conflict, "Realized", 1), (cancelled.StatusCode, state.Status, active));
            }
            else
            {
                // Cancelou antes: realizar a previsão cancelada é recusado e nenhum lançamento existe.
                Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.OK, "Cancelled", 0), (realized.StatusCode, cancelled.StatusCode, state.Status, active));
            }
        }
    }

    [Fact]
    public async Task RealizeRacingEditOfThePlanned_UsesTheEditedDataOrRejectsTheEdit()
    {
        await ArrangeAsync();
        for (var round = 0; round < 6; round++)
        {
            var planned = await CreatePlannedAsync("200.00");

            var realize = RealizeAsync(planned, "210.00", Effective);
            var edit = SendWithCsrfAsync(HttpMethod.Put, $"{PlannedUrl}/{planned}", _member,
                PlannedBody("250.00", new DateOnly(2026, 9, 6), "Aluguel reajustado"));
            var (realized, edited) = (await realize, await edit);

            Assert.Equal(HttpStatusCode.Created, realized.StatusCode);
            var transaction = await ReadAsync<TransactionDto>(realized);
            var state = await GetPlannedAsync(planned);
            if (edited.StatusCode == HttpStatusCode.OK)
            {
                // Editou antes: a realização usou a descrição nova.
                Assert.Equal(("Aluguel reajustado", "250.00"), (transaction.Description, state.Amount));
            }
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, edited.StatusCode);
                Assert.Equal(("Aluguel de setembro", "200.00"), (transaction.Description, state.Amount));
            }
        }
    }

    [Fact]
    public async Task CancellingTheEffectiveTransaction_ReopensThePlanned_RestoresBalance_AndAllowsRealizingAgain()
    {
        await ArrangeAsync();
        var planned = await CreatePlannedAsync("200.00");
        var first = await ReadAsync<TransactionDto>(await RealizeAsync(planned, "210.00", Effective));

        var cancel = await SendWithCsrfAsync(HttpMethod.Post, $"{TransactionsUrl}/{first.Id}/cancel", _member);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("1000.00", await BalanceAsync());
        var reopened = await GetPlannedAsync(planned);
        Assert.Equal(("Pending", (RealizationDto?)null), (reopened.Status, reopened.Realization));
        Assert.Contains(planned, (await ListPlannedAsync()).Select(p => p.Id)); // volta à lista padrão

        var second = await RealizeAsync(planned, "205.50", Effective.AddDays(2));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondTx = await ReadAsync<TransactionDto>(second);
        Assert.NotEqual(first.Id, secondTx.Id);
        Assert.Equal("794.50", await BalanceAsync());

        // Repetir o cancelamento do lançamento antigo não interfere na nova realização.
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Post, $"{TransactionsUrl}/{first.Id}/cancel", _owner)).StatusCode);
        var state = await GetPlannedAsync(planned);
        Assert.Equal(("Realized", secondTx.Id), (state.Status, state.Realization!.TransactionId));
        Assert.Equal("794.50", await BalanceAsync());

        await using var db = Db();
        var history = await db.FinancialTransactions.Where(t => t.PlannedTransactionId == planned).OrderBy(t => t.CreatedAt).ToListAsync();
        Assert.Equal([(true, 210.00m), (false, 205.50m)], history.Select(t => (t.IsCancelled, t.Amount)));
        Assert.Equal(await UserIdAsync("membro"), history[0].CancelledByUserId); // auditoria do primeiro cancelamento preservada
    }

    [Fact]
    public async Task RealizedPlanned_CannotBeEditedOrCancelled_AndLinkedTransactionCannotBeEdited()
    {
        await ArrangeAsync();
        var planned = await CreatePlannedAsync("200.00");
        var transaction = await ReadAsync<TransactionDto>(await RealizeAsync(planned, "210.00", Effective));

        var editPlanned = await SendWithCsrfAsync(HttpMethod.Put, $"{PlannedUrl}/{planned}", _owner, PlannedBody("300.00", Effective, "X"));
        var cancelPlanned = await SendWithCsrfAsync(HttpMethod.Post, $"{PlannedUrl}/{planned}/cancel", _owner);
        var editTransaction = await SendWithCsrfAsync(HttpMethod.Put, $"{TransactionsUrl}/{transaction.Id}", _owner, new
        {
            accountId = _itau, categoryId = _aluguel, type = "Expense", description = "Editado", amount = "1.00", transactionDate = Effective,
        });

        Assert.Equal([HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.Conflict],
            new[] { editPlanned.StatusCode, cancelPlanned.StatusCode, editTransaction.StatusCode });
        Assert.Contains("previsão", await editTransaction.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await using var db = Db();
        Assert.Equal((210.00m, false), ((await db.FinancialTransactions.SingleAsync()).Amount, (await db.FinancialPlannedTransactions.SingleAsync()).IsCancelled));
    }

    [Fact]
    public async Task CancelledPlanned_CannotBeRealized()
    {
        await ArrangeAsync();
        var planned = await CreatePlannedAsync("200.00");
        await SendWithCsrfAsync(HttpMethod.Post, $"{PlannedUrl}/{planned}/cancel", _owner);

        var response = await RealizeAsync(planned, "200.00", Effective);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = Db();
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
    }

    [Theory]
    [InlineData("0", "amount")]
    [InlineData("-1", "amount")]
    [InlineData("10.005", "amount")]
    [InlineData("10000000000000", "amount")]
    public async Task InvalidAmount_Returns400(string amount, string field)
    {
        await ArrangeAsync();
        var planned = await CreatePlannedAsync("200.00");

        var response = await SendRawAsync($"{PlannedUrl}/{planned}/realize", $$"""{ "amount": {{amount}}, "transactionDate": "2026-09-10" }""");

        await AssertValidationErrorAsync(response, field);
        await AssertNoTransactionsAsync();
    }

    [Fact]
    public async Task FutureDate_IsRejected_TodayAccepted_AndDateBeforeReferenceFollowsTheBalanceRule()
    {
        await ArrangeAsync();
        var today = BusinessCalendar.Today(DateTimeOffset.UtcNow);
        var planned = await CreatePlannedAsync("200.00", today.AddDays(10));

        await AssertValidationErrorAsync(await RealizeAsync(planned, "200.00", today.AddDays(1)), "transactionDate");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendRawAsync($"{PlannedUrl}/{planned}/realize", """{ "amount": "200.00", "transactionDate": "2026-09-10T10:00:00" }""")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await RealizeAsync(planned, "200.00", today)).StatusCode);

        // Data anterior à referência: lançamento existe (histórico), mas não entra no saldo.
        var old = await CreatePlannedAsync("50.00", Reference.AddDays(-5));
        Assert.Equal(HttpStatusCode.Created, (await RealizeAsync(old, "50.00", Reference.AddDays(-3))).StatusCode);
        Assert.Equal("800.00", await BalanceAsync());
    }

    [Fact]
    public async Task InactiveAccountOrCategory_BlocksRealization_WithGuidance()
    {
        await ArrangeAsync();
        var planned = await CreatePlannedAsync("200.00");
        await SendWithCsrfAsync(HttpMethod.Put, $"/api/families/{_family.Id}/accounts/{_itau}", _owner,
            new { name = "Itaú PF", type = "Checking", financialScope = "PF", openingBalance = "1000.00", openingBalanceDate = Reference, isActive = false });

        var response = await RealizeAsync(planned, "200.00", Effective);

        await AssertValidationErrorAsync(response, "accountId");
        Assert.Contains("Edite a previsão", await response.Content.ReadAsStringAsync());

        await SendWithCsrfAsync(HttpMethod.Put, $"/api/families/{_family.Id}/accounts/{_itau}", _owner,
            new { name = "Itaú PF", type = "Checking", financialScope = "PF", openingBalance = "1000.00", openingBalanceDate = Reference, isActive = true });
        await SendWithCsrfAsync(HttpMethod.Put, $"/api/families/{_family.Id}/categories/{_aluguel}", _owner, new { name = "Aluguel", isActive = false });

        await AssertValidationErrorAsync(await RealizeAsync(planned, "200.00", Effective), "categoryId");
        await AssertNoTransactionsAsync();
    }

    [Fact]
    public async Task Isolation_Csrf_AndServerFields()
    {
        await ArrangeAsync();
        var planned = await CreatePlannedAsync("200.00");

        Assert.Equal(HttpStatusCode.NotFound, (await RealizeAsync(planned, "200.00", Effective, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{_otherFamily.Id}/planned-transactions/{planned}/realize", _outsider, new { amount = "1.00", transactionDate = Effective })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RealizeAsync(Guid.CreateVersion7(), "200.00", Effective)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendJsonAsync(HttpMethod.Post, $"{PlannedUrl}/{planned}/realize", _owner, csrf: null,
            new { amount = "200.00", transactionDate = Effective })).StatusCode);
        foreach (var extra in new[] { "\"accountId\": \"" + Guid.CreateVersion7() + "\"", "\"description\": \"X\"", "\"categoryId\": \"" + _aluguel + "\"", "\"type\": \"Income\"" })
        {
            Assert.Equal(HttpStatusCode.BadRequest,
                (await SendRawAsync($"{PlannedUrl}/{planned}/realize", $$"""{ "amount": "200.00", "transactionDate": "2026-09-10", {{extra}} }""")).StatusCode);
        }

        await AssertNoTransactionsAsync();
    }

    [Fact]
    public async Task List_ShowsPendingByDefault_IncludeRealizedAndIncludeCancelled()
    {
        await ArrangeAsync();
        var pending = await CreatePlannedAsync("1.00");
        var realized = await CreatePlannedAsync("2.00");
        var cancelled = await CreatePlannedAsync("3.00");
        await RealizeAsync(realized, "2.00", Effective);
        await SendWithCsrfAsync(HttpMethod.Post, $"{PlannedUrl}/{cancelled}/cancel", _owner);

        Assert.Equal([pending], (await ListPlannedAsync()).Select(p => p.Id));
        Assert.Equal(new[] { pending, realized }.Order(), (await ListPlannedAsync("includeRealized=true")).Select(p => p.Id).Order());
        Assert.Equal(new[] { pending, cancelled }.Order(), (await ListPlannedAsync("includeCancelled=true")).Select(p => p.Id).Order());
        var all = await ListPlannedAsync("includeRealized=true&includeCancelled=true");
        Assert.Equal(3, all.Count);
        Assert.Equal(["Pending", "Realized", "Cancelled"],
            new[] { pending, realized, cancelled }.Select(id => all.Single(p => p.Id == id).Status));
        Assert.Null(all.Single(p => p.Id == cancelled).Realization);
    }

    private object PlannedBody(string amount, DateOnly dueDate, string description = "Aluguel de setembro") =>
        new { accountId = _itau, categoryId = _aluguel, type = "Expense", description, amount, dueDate };

    private async Task<Guid> CreatePlannedAsync(string amount, DateOnly? dueDate = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner, PlannedBody(amount, dueDate ?? new DateOnly(2026, 9, 5)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private Task<HttpResponseMessage> RealizeAsync(Guid planned, string amount, DateOnly date, string? session = null) =>
        SendWithCsrfAsync(HttpMethod.Post, $"{PlannedUrl}/{planned}/realize", session ?? _owner, new { amount, transactionDate = date });

    private async Task<PlannedDto> GetPlannedAsync(Guid planned)
    {
        var response = await SendAsync(HttpMethod.Get, $"{PlannedUrl}/{planned}", _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PlannedDto>(response);
    }

    private async Task<List<PlannedDto>> ListPlannedAsync(string query = "")
    {
        var response = await SendAsync(HttpMethod.Get, query.Length > 0 ? $"{PlannedUrl}?{query}" : PlannedUrl, _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PageDto>(response)).Items;
    }

    private async Task<string> BalanceAsync()
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/families/{_family.Id}/accounts/{_itau}/balance", _owner);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("balance").GetString()!;
    }

    private async Task<Guid> CreateAccountAsync(string name, string openingBalance)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/accounts", _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate = Reference });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateCategoryAsync(string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/categories", _owner, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
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

    private async Task<HttpResponseMessage> SendRawAsync(string url, string json)
    {
        var (csrfCookie, csrfToken) = await GetCsrfAsync(_owner);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", $"{_owner}; {csrfCookie}");
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
        Guid Id, string Type, string Amount, DateOnly TransactionDate, string Description, ReferenceDto Account, ReferenceDto Category,
        bool IsCancelled, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, Guid? PlannedTransactionId);

    private sealed record RealizationDto(Guid TransactionId, string Amount, DateOnly TransactionDate);

    private sealed record PlannedDto(
        Guid Id, string Amount, DateOnly DueDate, string Description, string Status, RealizationDto? Realization, bool IsCancelled);

    private sealed record PageDto(List<PlannedDto> Items, int Page, int PageSize, int TotalCount);
}

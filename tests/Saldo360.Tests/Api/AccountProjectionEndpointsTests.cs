using System.Net;
using System.Text.Json;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Saldo projetado sobre PostgreSQL real: saldo realizado de hoje + previsões pendentes vencidas e até a data-alvo.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountProjectionEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static readonly DateOnly Reference = new(2026, 9, 1);

    private string _owner = null!;
    private string _ownerSecondSession = null!;
    private string _outsider = null!;
    private WorkspaceDto _family = null!;
    private WorkspaceDto _otherFamily = null!;
    private Guid _itau;
    private Guid _salario;
    private Guid _aluguel;

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
        _itau = await CreateAccountAsync("Itaú PF", "1000.00", Reference);
        _salario = await CreateCategoryAsync("Salário", "Income");
        _aluguel = await CreateCategoryAsync("Aluguel", "Expense");
    }

    [Fact]
    public async Task Acceptance_1000_Plus500_Minus200_Is1300_Overdue100_Gives1200_Realize210_Gives1190_CancelRestores1200()
    {
        await ArrangeAsync();
        var through = Today.AddDays(30);
        await CreatePlannedAsync(_salario, "Income", "500.00", Today.AddDays(5));
        var aluguel = await CreatePlannedAsync(_aluguel, "Expense", "200.00", Today.AddDays(10));
        await CreatePlannedAsync(_aluguel, "Expense", "999.00", through.AddDays(1)); // depois da data-alvo: fora

        var first = await ProjectionJsonAsync(_itau, through);
        Assert.Equal(("1000.00", "0.00", "0.00", "500.00", "200.00", "1300.00"), Money(first));
        Assert.Equal((Today.ToString("yyyy-MM-dd"), through.ToString("yyyy-MM-dd")),
            (first.GetProperty("baseDate").GetString(), first.GetProperty("through").GetString()));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("unavailableReason").ValueKind);

        await CreatePlannedAsync(_aluguel, "Expense", "100.00", Today.AddDays(-3)); // vencida pendente
        Assert.Equal(("1000.00", "0.00", "100.00", "500.00", "200.00", "1200.00"), Money(await ProjectionJsonAsync(_itau, through)));

        var realized = await SendWithCsrfAsync(HttpMethod.Post, $"{PlannedUrl}/{aluguel}/realize", _ownerSecondSession, new { amount = "210.00", transactionDate = Today });
        Assert.Equal(HttpStatusCode.Created, realized.StatusCode);
        Assert.Equal(("790.00", "0.00", "100.00", "500.00", "0.00", "1190.00"), Money(await ProjectionJsonAsync(_itau, through)));

        var transactionId = JsonDocument.Parse(await realized.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/transactions/{transactionId}/cancel", _owner);
        Assert.Equal(("1000.00", "0.00", "100.00", "500.00", "200.00", "1200.00"), Money(await ProjectionJsonAsync(_itau, through)));
    }

    [Fact]
    public async Task TodayIsInThePeriod_And_OverdueSplitsByType()
    {
        await ArrangeAsync();
        await CreatePlannedAsync(_salario, "Income", "10.00", Today);
        await CreatePlannedAsync(_salario, "Income", "7.00", Today.AddDays(-1));
        await CreatePlannedAsync(_aluguel, "Expense", "3.00", Today.AddDays(-40));

        Assert.Equal(("1000.00", "7.00", "3.00", "10.00", "0.00", "1014.00"), Money(await ProjectionJsonAsync(_itau, Today)));
    }

    [Fact]
    public async Task PendingBeforeTheOpeningBalanceDate_StillCounts_CancelledDoesNot()
    {
        await ArrangeAsync();
        await CreatePlannedAsync(_aluguel, "Expense", "50.00", Reference.AddDays(-20)); // antes da referência: ainda pendente
        var cancelled = await CreatePlannedAsync(_aluguel, "Expense", "70.00", Today.AddDays(1));
        await SendWithCsrfAsync(HttpMethod.Post, $"{PlannedUrl}/{cancelled}/cancel", _owner);

        Assert.Equal(("1000.00", "0.00", "50.00", "0.00", "0.00", "950.00"), Money(await ProjectionJsonAsync(_itau, Today.AddDays(5))));
    }

    [Fact]
    public async Task InactiveAccountAndCategory_DoNotHidePendingCommitments()
    {
        await ArrangeAsync();
        await CreatePlannedAsync(_aluguel, "Expense", "300.00", Today.AddDays(2));
        await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{_family.Id}/categories/{_aluguel}", _owner, new { name = "Aluguel", isActive = false });
        await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{_family.Id}/accounts/{_itau}", _owner,
            new { name = "Itaú PF", type = "Checking", financialScope = "PF", openingBalance = "1000.00", openingBalanceDate = Reference, isActive = false });

        Assert.Equal("700.00", Money(await ProjectionJsonAsync(_itau, Today.AddDays(5))).Item6);
    }

    [Fact]
    public async Task RealizedPart_IncludesTransactionsAndTransfers_AndMatchesTheBalanceEndpoint()
    {
        await ArrangeAsync();
        var nubank = await CreateAccountAsync("Nubank", "0.00", Reference);
        await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/transactions", _owner,
            new { accountId = _itau, categoryId = _aluguel, type = "Expense", description = "Compra", amount = "12.34", transactionDate = Today });
        await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/transfers", _owner,
            new { sourceAccountId = _itau, destinationAccountId = nubank, amount = "100.00", transferDate = Today, description = "Reserva" });
        await CreatePlannedAsync(_salario, "Income", "0.10", Today.AddDays(1));
        await CreatePlannedAsync(_salario, "Income", "0.20", Today.AddDays(2));

        var projection = Money(await ProjectionJsonAsync(_itau, Today.AddDays(3)));
        using var balance = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{_family.Id}/accounts/{_itau}/balance", _owner)).Content.ReadAsStringAsync());

        Assert.Equal(balance.RootElement.GetProperty("balance").GetString(), projection.Item1);
        Assert.Equal(("887.66", "0.30", "887.96"), (projection.Item1, projection.Item4, projection.Item6));
    }

    [Fact]
    public async Task SumsAboveTheSingleLimit_KeepFullPrecision()
    {
        await ArrangeAsync();
        for (var i = 0; i < 3; i++)
        {
            await CreatePlannedAsync(_salario, "Income", "9999999999999.99", Today.AddDays(1));
        }

        await CreatePlannedAsync(_aluguel, "Expense", "0.01", Today.AddDays(-1));

        var projection = Money(await ProjectionJsonAsync(_itau, Today.AddDays(1)));

        // 1.000,00 + 29.999.999.999.999,97 − 0,01
        Assert.Equal(("29999999999999.97", "0.01", "30000000000999.96"), (projection.Item4, projection.Item3, projection.Item6));
    }

    [Fact]
    public async Task OtherAccountsAndFamilies_DoNotInterfere()
    {
        await ArrangeAsync();
        var nubank = await CreateAccountAsync("Nubank", "0.00", Reference);
        await CreatePlannedAsync(_aluguel, "Expense", "40.00", Today.AddDays(1), nubank);
        var foreign = await CreateAccountAsync("Conta Estranha", "0.00", Reference, _outsider, _otherFamily.Id);
        var foreignCategory = await CreateCategoryAsync("Aluguel", "Expense", _outsider, _otherFamily.Id);
        await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_otherFamily.Id}/planned-transactions", _outsider,
            new { accountId = foreign, categoryId = foreignCategory, type = "Expense", description = "X", amount = "77.00", dueDate = Today });

        Assert.Equal("1000.00", Money(await ProjectionJsonAsync(_itau, Today.AddDays(5))).Item6);
        Assert.Equal("-40.00", Money(await ProjectionJsonAsync(nubank, Today.AddDays(5))).Item6);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, Url(_itau, Today), _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, Url(foreign, Today), _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{_otherFamily.Id}/accounts/{_itau}/projection?through={Today:yyyy-MM-dd}", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, Url(Guid.CreateVersion7(), Today), _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(Url(_itau, Today))).StatusCode);
    }

    [Fact]
    public async Task WithoutOpeningBalance_OrFutureReference_IsUnavailable_WithAllMoneyNull()
    {
        await ArrangeAsync();
        var semSaldo = await CreateAccountAsync("Dinheiro", null, null);
        var futura = await CreateAccountAsync("Conta nova", "500.00", Today.AddDays(10));
        await CreatePlannedAsync(_aluguel, "Expense", "10.00", Today.AddDays(1), semSaldo);

        var none = await ProjectionJsonAsync(semSaldo, Today.AddDays(30));
        var future = await ProjectionJsonAsync(futura, Today.AddDays(30));

        Assert.Equal("OpeningBalanceNotConfigured", none.GetProperty("unavailableReason").GetString());
        Assert.Equal("BeforeOpeningBalanceDate", future.GetProperty("unavailableReason").GetString());
        foreach (var json in new[] { none, future })
        {
            foreach (var field in new[] { "realizedBalance", "overdueIncome", "overdueExpense", "plannedIncome", "plannedExpense", "projectedBalance" })
            {
                Assert.Equal(JsonValueKind.Null, json.GetProperty(field).ValueKind);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("?through=")]
    [InlineData("?through=01/12/2026")]
    [InlineData("?through=2026-12-1")]
    [InlineData("?through=2026-02-30")]
    public async Task MissingOrInvalidThrough_Returns400(string query)
    {
        await ArrangeAsync();

        var response = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{_family.Id}/accounts/{_itau}/projection{query}", _owner);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("through", out _));
    }

    [Fact]
    public async Task PastThrough_Returns400_TodayIsAccepted()
    {
        await ArrangeAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, Url(_itau, Today.AddDays(-1)), _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, Url(_itau, Today), _ownerSecondSession)).StatusCode);
    }

    [Fact]
    public async Task Projection_IsConsistent_WhileRealizingAndCancellingConcurrently()
    {
        await ArrangeAsync();
        var through = Today.AddDays(30);
        await CreatePlannedAsync(_salario, "Income", "500.00", Today.AddDays(5));
        await CreatePlannedAsync(_aluguel, "Expense", "100.00", Today.AddDays(-3));
        var aluguel = await CreatePlannedAsync(_aluguel, "Expense", "200.00", Today.AddDays(10));

        // Antes de realizar: 1200,00. Realizada por 210,00: 1190,00. Contar os dois daria 990,00; nenhum, 1400,00.
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                var realized = await SendWithCsrfAsync(HttpMethod.Post, $"{PlannedUrl}/{aluguel}/realize", _ownerSecondSession, new { amount = "210.00", transactionDate = Today });
                var id = JsonDocument.Parse(await realized.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
                await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/transactions/{id}/cancel", _ownerSecondSession);
            }
        });

        var seen = new HashSet<string>();
        while (!writer.IsCompleted)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ProjectionJsonAsync(_itau, through)));
            foreach (var projected in results.Select(r => r.GetProperty("projectedBalance").GetString()!))
            {
                seen.Add(projected);
            }
        }

        await writer;
        Assert.Subset(new HashSet<string> { "1200.00", "1190.00" }, seen);
        Assert.Equal("1200.00", (await ProjectionJsonAsync(_itau, through)).GetProperty("projectedBalance").GetString());
    }

    private static (string?, string?, string?, string?, string?, string?) Money(JsonElement json) => (
        json.GetProperty("realizedBalance").GetString(),
        json.GetProperty("overdueIncome").GetString(),
        json.GetProperty("overdueExpense").GetString(),
        json.GetProperty("plannedIncome").GetString(),
        json.GetProperty("plannedExpense").GetString(),
        json.GetProperty("projectedBalance").GetString());

    private string Url(Guid account, DateOnly through) =>
        $"/api/financial-workspaces/{_family.Id}/accounts/{account}/projection?through={through:yyyy-MM-dd}";

    private async Task<JsonElement> ProjectionJsonAsync(Guid account, DateOnly through)
    {
        var response = await SendAsync(HttpMethod.Get, Url(account, through), _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private async Task<Guid> CreatePlannedAsync(Guid category, string type, string amount, DateOnly dueDate, Guid? account = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner,
            new { accountId = account ?? _itau, categoryId = category, type, description = $"Previsão {amount}", amount, dueDate });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateAccountAsync(
        string name, string? openingBalance, DateOnly? openingBalanceDate, string? session = null, Guid? workspaceId = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspaceId ?? _family.Id}/accounts", session ?? _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateCategoryAsync(string name, string type, string? session = null, Guid? workspaceId = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspaceId ?? _family.Id}/categories", session ?? _owner, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private sealed record IdDto(Guid Id);
}

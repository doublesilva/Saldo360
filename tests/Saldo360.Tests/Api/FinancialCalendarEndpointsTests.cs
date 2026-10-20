using System.Net;
using System.Text.Json;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialCalendarEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private string _ownerSecondSession = null!;
    private string _outsider = null!;
    private WorkspaceDto _family = null!;
    private Guid _account;
    private Guid _otherAccount;
    private Guid _income;
    private Guid _expense;

    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);

    private string CalendarUrl(DateOnly from, DateOnly through) =>
        $"/api/financial-workspaces/{_family.Id}/financial-calendar?from={from:yyyy-MM-dd}&through={through:yyyy-MM-dd}";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("dona");
        _ownerSecondSession = _owner;
        _outsider = await SignInAsync("estranho");
        var created = await CreateWorkspaceAsync(_owner, "Família Silva");
        _family = created;
        _account = await CreateAccountAsync("Itaú");
        _otherAccount = await CreateAccountAsync("Nubank");
        _income = await CreateCategoryAsync("Salário", "Income");
        _expense = await CreateCategoryAsync("Aluguel", "Expense");
    }

    [Fact]
    public async Task EmptyPeriod_ReturnsAllSummaryValuesAsExactZeroStrings()
    {
        await ArrangeAsync();

        var calendar = await ReadAsync<CalendarDto>(
            await SendAsync(HttpMethod.Get, CalendarUrl(Today.AddDays(-2), Today.AddDays(2)), _owner));

        Assert.Empty(calendar.Items);
        Assert.Equal(new SummaryDto("0.00", "0.00", "0.00", "0.00", "0.00", "0.00"), calendar.Summary);
    }

    [Fact]
    public async Task CombinesEffectivePendingAndOverdue_WithoutCancelledOrRealizedDuplicate_InChronologicalOrder()
    {
        await ArrangeAsync();
        var effective = await CreateTransactionAsync(_account, _expense, "Expense", "10.25", Today.AddDays(-4), "Mercado");
        var cancelledEffective = await CreateTransactionAsync(_account, _expense, "Expense", "99.00", Today.AddDays(-3), "Cancelado");
        await CancelTransactionAsync(cancelledEffective);

        var overdue = await CreatePlannedAsync(_account, _income, "Income", "20.00", Today.AddDays(-2), "Reembolso");
        var overdueExpense = await CreatePlannedAsync(_account, _expense, "Expense", "21.25", Today.AddDays(-1), "Conta vencida");
        // A data prevista fica fora do período: a realização deve entrar pela data efetiva, sem depender dela.
        var realizedPlanned = await CreatePlannedAsync(_account, _expense, "Expense", "30.00", Today.AddDays(40), "Conta prevista");
        var realization = await RealizeAsync(realizedPlanned, "31.50", Today, _ownerSecondSession);
        var cancelledPlanned = await CreatePlannedAsync(_account, _expense, "Expense", "88.00", Today.AddDays(2), "Previsão cancelada");
        await CancelPlannedAsync(cancelledPlanned);

        var response = await SendAsync(HttpMethod.Get, CalendarUrl(Today.AddDays(-10), Today.AddDays(10)), _ownerSecondSession);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var calendar = await ReadAsync<CalendarDto>(response);
        Assert.Equal((Today.AddDays(-10), Today.AddDays(10)), (calendar.From, calendar.Through));
        Assert.Equal([effective, overdue, overdueExpense, realization], calendar.Items.Select(item => item.Id));

        Assert.Equal(("Transaction", "Realized", "10.25", Today.AddDays(-4), (Guid?)null),
            Item(calendar, effective));
        Assert.Equal(("PlannedTransaction", "Overdue", "20.00", Today.AddDays(-2), (Guid?)null),
            Item(calendar, overdue));
        Assert.Equal(("PlannedTransaction", "Overdue", "21.25", Today.AddDays(-1), (Guid?)null),
            Item(calendar, overdueExpense));
        Assert.Equal(("Transaction", "Realized", "31.50", Today, (Guid?)realizedPlanned),
            Item(calendar, realization));
        Assert.DoesNotContain(calendar.Items, item => item.Id == realizedPlanned || item.Id == cancelledEffective || item.Id == cancelledPlanned);
        Assert.Equal(new SummaryDto("0.00", "41.75", "20.00", "21.25", "20.00", "21.25"), calendar.Summary);
        Assert.Equal(["Edit", "Cancel"], calendar.Items.Single(x => x.Id == effective).AllowedActions);
        Assert.Equal(["Realize", "Edit", "Cancel"], calendar.Items.Single(x => x.Id == overdue).AllowedActions);
        Assert.Equal(["UndoRealization", "ViewOrigin"], calendar.Items.Single(x => x.Id == realization).AllowedActions);
    }

    [Fact]
    public async Task CancellingRealization_ReopensForecast_AtForecastDateAndAmount()
    {
        await ArrangeAsync();
        var dueDate = Today.AddDays(3);
        var planned = await CreatePlannedAsync(_account, _expense, "Expense", "200.00", dueDate, "Aluguel");
        var realization = await RealizeAsync(planned, "210.00", Today, _owner);

        await CancelTransactionAsync(realization);
        var response = await SendAsync(HttpMethod.Get, CalendarUrl(Today.AddDays(-1), dueDate.AddDays(1)), _owner);
        var calendar = await ReadAsync<CalendarDto>(response);

        var item = Assert.Single(calendar.Items);
        Assert.Equal((planned, "PlannedTransaction", "Pending", "200.00", dueDate),
            (item.Id, item.Source, item.Status, item.Amount, item.Date));
        Assert.Equal(new SummaryDto("0.00", "0.00", "0.00", "200.00", "0.00", "0.00"), calendar.Summary);
    }

    [Fact]
    public async Task ValidatesRequiredStrictDatesRangeAndType_AndEnforcesFamilyMembership()
    {
        await ArrangeAsync();

        await AssertErrorAsync(await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{_family.Id}/financial-calendar", _owner), "from", "through");
        await AssertErrorAsync(await SendAsync(HttpMethod.Get,
            $"/api/financial-workspaces/{_family.Id}/financial-calendar?from=01/09/2026&through=2026-09-30", _owner), "from");
        await AssertErrorAsync(await SendAsync(HttpMethod.Get,
            $"/api/financial-workspaces/{_family.Id}/financial-calendar?from=2026-09-30&through=2026-09-01", _owner), "through");
        await AssertErrorAsync(await SendAsync(HttpMethod.Get,
            $"/api/financial-workspaces/{_family.Id}/financial-calendar?from=2026-09-01&through=2026-09-30&type=Both", _owner), "type");

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, CalendarUrl(Today.AddDays(-1), Today.AddDays(1)), _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client.GetAsync(CalendarUrl(Today.AddDays(-1), Today.AddDays(1)))).StatusCode);
    }

    [Fact]
    public async Task AccountAndTypeFiltersApplyToBothSources()
    {
        await ArrangeAsync();
        var matchingTransaction = await CreateTransactionAsync(_account, _income, "Income", "10.00", Today, "Receita realizada");
        var matchingPlanned = await CreatePlannedAsync(_account, _income, "Income", "20.00", Today.AddDays(1), "Receita prevista");
        await CreateTransactionAsync(_otherAccount, _income, "Income", "30.00", Today, "Outra conta");
        await CreatePlannedAsync(_account, _expense, "Expense", "40.00", Today.AddDays(1), "Outro tipo");

        var url = CalendarUrl(Today.AddDays(-1), Today.AddDays(2)) + $"&accountId={_account}&type=Income";
        var calendar = await ReadAsync<CalendarDto>(await SendAsync(HttpMethod.Get, url, _owner));

        Assert.Equal([matchingTransaction, matchingPlanned], calendar.Items.Select(item => item.Id));
        Assert.All(calendar.Items, item =>
        {
            Assert.Equal(_account, item.Account.Id);
            Assert.Equal("Income", item.Type);
        });
        Assert.Equal(new SummaryDto("10.00", "0.00", "20.00", "0.00", "0.00", "0.00"), calendar.Summary);
    }

    [Fact]
    public async Task Summary_UsesExactDecimals_AndTodayIsPendingButNotOverdue()
    {
        await ArrangeAsync();
        await CreateTransactionAsync(_account, _income, "Income", "0.10", Today, "Centavos 1");
        await CreateTransactionAsync(_account, _income, "Income", "0.20", Today, "Centavos 2");
        await CreatePlannedAsync(_account, _income, "Income", "9.99", Today.AddDays(-1), "Fora do período");
        await CreatePlannedAsync(_account, _income, "Income", "0.10", Today, "Vence hoje");
        await CreatePlannedAsync(_account, _income, "Income", "0.20", Today.AddDays(1), "Vence amanhã");

        var calendar = await ReadAsync<CalendarDto>(
            await SendAsync(HttpMethod.Get, CalendarUrl(Today, Today.AddDays(1)), _owner));

        Assert.Equal(new SummaryDto("0.30", "0.00", "0.30", "0.00", "0.00", "0.00"), calendar.Summary);
        Assert.DoesNotContain(calendar.Items, item => item.Description == "Fora do período");
        Assert.Equal("Pending", calendar.Items.Single(item => item.Description == "Vence hoje").Status);
    }

    private static (string Source, string Status, string Amount, DateOnly Date, Guid? PlannedTransactionId) Item(
        CalendarDto calendar,
        Guid id)
    {
        var item = calendar.Items.Single(value => value.Id == id);
        return (item.Source, item.Status, item.Amount, item.Date, item.PlannedTransactionId);
    }

    private async Task<Guid> CreateAccountAsync(string name)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/accounts", _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance = "0.00", openingBalanceDate = Today.AddDays(-30) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateCategoryAsync(string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/categories", _owner, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateTransactionAsync(
        Guid accountId, Guid categoryId, string type, string amount, DateOnly date, string description)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/transactions", _owner,
            new { accountId, categoryId, type, amount, transactionDate = date, description });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreatePlannedAsync(
        Guid accountId, Guid categoryId, string type, string amount, DateOnly date, string description)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/planned-transactions", _owner,
            new { accountId, categoryId, type, amount, dueDate = date, description });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> RealizeAsync(Guid plannedId, string amount, DateOnly date, string session)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/financial-workspaces/{_family.Id}/planned-transactions/{plannedId}/realize", session,
            new { amount, transactionDate = date });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task CancelTransactionAsync(Guid transactionId)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/financial-workspaces/{_family.Id}/transactions/{transactionId}/cancel", _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task CancelPlannedAsync(Guid plannedId)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/financial-workspaces/{_family.Id}/planned-transactions/{plannedId}/cancel", _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, params string[] fields)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var field in fields)
        {
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"esperado erro em '{field}'");
        }
    }

    private sealed record IdDto(Guid Id);
    private sealed record ReferenceDto(Guid Id, string Name, bool IsActive);
    private sealed record CalendarItemDto(
        Guid Id,
        string Source,
        string Status,
        string Type,
        string Amount,
        DateOnly Date,
        string Description,
        ReferenceDto Account,
        ReferenceDto Category,
        string Currency,
        Guid? PlannedTransactionId,
        string[] AllowedActions);
    private sealed record SummaryDto(
        string RealizedIncome,
        string RealizedExpense,
        string PendingIncome,
        string PendingExpense,
        string OverdueIncome,
        string OverdueExpense);
    private sealed record CalendarDto(
        DateOnly From,
        DateOnly Through,
        List<CalendarItemDto> Items,
        SummaryDto Summary);
}

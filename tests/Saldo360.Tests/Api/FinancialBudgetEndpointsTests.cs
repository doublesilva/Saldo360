using System.Net;
using System.Text.Json;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialBudgetEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private string _member = null!;
    private string _outsider = null!;
    private FamilyDto _family = null!;
    private FamilyDto _otherFamily = null!;
    private Guid _expense;
    private Guid _income;
    private Guid _otherExpense;
    private string BudgetsUrl => $"/api/families/{_family.Id}/budgets";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("budget-owner");
        _member = await SignInAsync("budget-member");
        _outsider = await SignInAsync("budget-outsider");
        var created = await CreateFamilyAsync(_owner);
        _family = await JoinByInvitationAsync(_owner, created.Id, _member);
        _otherFamily = await CreateFamilyAsync(_outsider, "Outra família");
        _expense = await CreateCategoryAsync(_family.Id, _owner, "Moradia", "Expense");
        _income = await CreateCategoryAsync(_family.Id, _owner, "Salário", "Income");
        _otherExpense = await CreateCategoryAsync(_otherFamily.Id, _outsider, "Alheia", "Expense");
    }

    [Fact]
    public async Task CreatesListsFiltersAndUpdatesMonthlyBudget()
    {
        await ArrangeAsync();
        var created = await CreateBudgetAsync("2026-10", " Outubro ");
        Assert.Equal(("2026-10", "Outubro", false, "0.00"),
            (created.Competence, created.Description, created.IsArchived, created.TotalPlannedAmount));

        var november = await CreateBudgetAsync("2026-11", null);
        Assert.Equal(2, (await ListAsync()).Count);
        Assert.Equal(created.Id, (await ListAsync("2026-10")).Single().Id);

        var updatedResponse = await SendWithCsrfAsync(HttpMethod.Put, $"{BudgetsUrl}/{created.Id}", _member,
            new { description = "Casa e família" });
        Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        Assert.Equal("Casa e família", (await ReadAsync<BudgetDto>(updatedResponse)).Description);
        Assert.Equal(november.Id, (await ReadAsync<BudgetDto>(await SendAsync(HttpMethod.Get,
            $"{BudgetsUrl}/{november.Id}", _member))).Id);
    }

    [Fact]
    public async Task ArchivesWithoutDeletingHistory_AndUniquenessStillIncludesArchivedBudget()
    {
        await ArrangeAsync();
        var budget = await CreateBudgetAsync("2026-10", "Histórico");
        var response = await SendWithCsrfAsync(HttpMethod.Delete, $"{BudgetsUrl}/{budget.Id}", _owner, null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ListAsync());
        var archived = (await ListAsync(includeArchived: true)).Single();
        Assert.True(archived.IsArchived);
        Assert.NotNull(archived.ArchivedAt);
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendWithCsrfAsync(HttpMethod.Post, BudgetsUrl, _owner,
                new { competence = "2026-10", description = "Duplicado" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendWithCsrfAsync(HttpMethod.Put, $"{BudgetsUrl}/{budget.Id}", _owner,
                new { description = "Não muda" })).StatusCode);
    }

    [Fact]
    public async Task CreatesUpdatesAndArchivesExactCategoryTarget_IncludingInactiveCategory()
    {
        await ArrangeAsync();
        var budget = await CreateBudgetAsync("2026-10", null);
        var created = await AddTargetAsync(budget.Id, _expense, " Moradia ", "9999999999999.99");
        var target = created.Targets.Single();
        Assert.Equal(("Moradia", "9999999999999.99", "Category", false),
            (target.Name, target.PlannedAmount, target.GroupingType, target.IsArchived));
        Assert.Equal("9999999999999.99", created.TotalPlannedAmount);

        var update = await SendWithCsrfAsync(HttpMethod.Put,
            $"{BudgetsUrl}/{budget.Id}/targets/{target.Id}", _member,
            new { name = "Habitação", plannedAmount = "1234.56" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("1234.56", (await ReadAsync<BudgetDto>(update)).TotalPlannedAmount);

        var deactivate = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/families/{_family.Id}/categories/{_expense}", _owner,
            new { name = "Moradia", isActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        var historical = await ReadAsync<BudgetDto>(await SendAsync(HttpMethod.Get, $"{BudgetsUrl}/{budget.Id}", _member));
        Assert.False(historical.Targets.Single().Category.IsActive);

        var archive = await SendWithCsrfAsync(HttpMethod.Delete,
            $"{BudgetsUrl}/{budget.Id}/targets/{target.Id}", _owner, null);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        var after = await ReadAsync<BudgetDto>(await SendAsync(HttpMethod.Get, $"{BudgetsUrl}/{budget.Id}", _owner));
        Assert.True(after.Targets.Single().IsArchived);
        Assert.Equal("0.00", after.TotalPlannedAmount);
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl}/{budget.Id}/targets", _owner,
                TargetBody(_expense, "Outra", "10.00"))).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-1")]
    [InlineData("2026-13")]
    [InlineData("10/2026")]
    public async Task InvalidCompetenceReturns400(string? competence)
    {
        await ArrangeAsync();
        var response = await SendWithCsrfAsync(HttpMethod.Post, BudgetsUrl, _owner, new { competence, description = (string?)null });
        await AssertValidationAsync(response, "competence");
    }

    [Fact]
    public async Task RejectsNegativeScaleRangeInvalidGroupingIncomeAndForeignCategory()
    {
        await ArrangeAsync();
        var budget = await CreateBudgetAsync("2026-10", null);
        foreach (var amount in new[] { "-0.01", "1.001", "10000000000000.00" })
            await AssertValidationAsync(await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl}/{budget.Id}/targets", _owner,
                TargetBody(_expense, "Inválida", amount)), "plannedAmount");
        await AssertValidationAsync(await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl}/{budget.Id}/targets", _owner,
            new { name = "Grupo", plannedAmount = "10.00", groupingType = "Group", categoryId = _expense }), "groupingType");
        await AssertValidationAsync(await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl}/{budget.Id}/targets", _owner,
            TargetBody(_income, "Receita", "10.00")), "categoryId");
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl}/{budget.Id}/targets", _owner,
                TargetBody(_otherExpense, "Alheia", "10.00"))).StatusCode);
    }

    [Fact]
    public async Task DuplicateTargetAndCrossFamilyAccessAreHidden()
    {
        await ArrangeAsync();
        var budget = await CreateBudgetAsync("2026-10", null);
        await AddTargetAsync(budget.Id, _expense, "Moradia", "10.00");
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl}/{budget.Id}/targets", _member,
                TargetBody(_expense, "Mesmo agrupamento", "20.00"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, BudgetsUrl, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"/api/families/{_otherFamily.Id}/budgets/{budget.Id}", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client.GetAsync($"/api/families/{_family.Id}/budgets")).StatusCode);
    }

    [Fact]
    public async Task BudgetChangesDoNotAffectBalanceProjectionTransactionsOrForecasts()
    {
        await ArrangeAsync();
        var today = BusinessCalendar.Today(DateTimeOffset.UtcNow);
        var account = await CreateAccountAsync("1000.00", today.AddDays(-5));
        await CreatePlannedAsync(account, "100.00", today.AddDays(1));
        var balanceUrl = $"/api/families/{_family.Id}/accounts/{account}/balance?asOf={today:yyyy-MM-dd}";
        var projectionUrl = $"/api/families/{_family.Id}/accounts/{account}/projection?through={today.AddDays(2):yyyy-MM-dd}";
        var beforeBalance = await (await SendAsync(HttpMethod.Get, balanceUrl, _member)).Content.ReadAsStringAsync();
        var beforeProjection = await (await SendAsync(HttpMethod.Get, projectionUrl, _member)).Content.ReadAsStringAsync();

        var budget = await CreateBudgetAsync($"{today:yyyy-MM}", null);
        var withTarget = await AddTargetAsync(budget.Id, _expense, "Moradia", "5000.00");
        await SendWithCsrfAsync(HttpMethod.Put, $"{BudgetsUrl}/{budget.Id}/targets/{withTarget.Targets.Single().Id}", _owner,
            new { name = "Moradia", plannedAmount = "7000.00" });

        Assert.Equal(beforeBalance, await (await SendAsync(HttpMethod.Get, balanceUrl, _member)).Content.ReadAsStringAsync());
        Assert.Equal(beforeProjection, await (await SendAsync(HttpMethod.Get, projectionUrl, _member)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WritesRequireCsrfAndUnknownFieldsAreRejected()
    {
        await ArrangeAsync();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendJsonAsync(HttpMethod.Post, BudgetsUrl, _owner, null,
                new { competence = "2026-10", description = "Sem CSRF" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendWithCsrfAsync(HttpMethod.Post, BudgetsUrl, _owner,
                new { competence = "2026-10", description = "Inválido", familyId = _otherFamily.Id })).StatusCode);
    }

    [Fact]
    public async Task ExecutionReturnsZerosWithoutTargetsOrMovements()
    {
        await ArrangeAsync();
        var budget = await CreateBudgetAsync("2026-10", null);

        var execution = await GetExecutionAsync(budget.Id);

        Assert.Empty(execution.Targets);
        Assert.Equal(("0.00", "0.00", "0.00", "0.00", "0.00", "0.00", "0.00"), Values(execution.Total));
        Assert.Equal("WithinBudget", execution.Total.Status);
        Assert.Equal(0, execution.Total.ApproachingCount);
        Assert.Equal(0, execution.Total.ReachedCount);
        Assert.Equal(0, execution.Total.ExceededCount);
    }

    [Fact]
    public async Task ExecutionAggregatesOnlyBudgetedExpenseCategoriesWithExactDecimals()
    {
        await ArrangeAsync();
        var food = await CreateCategoryAsync(_family.Id, _owner, "Alimentação", "Expense");
        var unbudgeted = await CreateCategoryAsync(_family.Id, _owner, "Sem meta", "Expense");
        var account = await CreateAccountAsync("0.00", new DateOnly(2026, 10, 1));
        var secondAccount = await CreateAccountAsync("0.00", new DateOnly(2026, 10, 1), "Reserva");
        var budget = await CreateBudgetAsync("2026-10", null);
        await AddTargetAsync(budget.Id, _expense, "Moradia", "1000.00");
        await AddTargetAsync(budget.Id, food, "Alimentação", "0.00");
        await CreateTransactionAsync(account, _expense, "100.10", new DateOnly(2026, 10, 2));
        await CreatePlannedAsync(account, _expense, "200.20", new DateOnly(2026, 10, 20));
        await CreateTransactionAsync(account, unbudgeted, "999.99", new DateOnly(2026, 10, 3));
        await CreateTransferAsync(account, secondAccount, "500.00", new DateOnly(2026, 10, 4));

        var execution = await GetExecutionAsync(budget.Id);
        var housing = execution.Targets.Single(item => item.CategoryId == _expense);
        var empty = execution.Targets.Single(item => item.CategoryId == food);

        Assert.Equal(("1000.00", "100.10", "200.20", "300.30", "699.70", "0.00", "30.03"), Values(housing));
        Assert.Equal(("0.00", "0.00", "0.00", "0.00", "0.00", "0.00", "0.00"), Values(empty));
        Assert.Equal(("1000.00", "100.10", "200.20", "300.30", "699.70", "0.00", "30.03"), Values(execution.Total));
    }

    [Fact]
    public async Task ZeroTargetWithConsumptionHasNegativeAvailableExceededAndNullPercentage()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("0.00", new DateOnly(2026, 10, 1));
        var budget = await CreateBudgetAsync("2026-10", null);
        await AddTargetAsync(budget.Id, _expense, "Moradia", "0.00");
        await CreateTransactionAsync(account, _expense, "0.10", new DateOnly(2026, 10, 2));
        await CreatePlannedAsync(account, _expense, "0.20", new DateOnly(2026, 10, 3));

        var amount = (await GetExecutionAsync(budget.Id)).Targets.Single();

        Assert.Equal(("0.00", "0.10", "0.20", "0.30", "-0.30", "0.30", null), Values(amount));
    }

    [Fact]
    public async Task RealizationUsesEffectiveValueWithoutDuplicationAndCancellationReopensForecast()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("0.00", new DateOnly(2026, 10, 1));
        var budget = await CreateBudgetAsync("2026-10", null);
        await AddTargetAsync(budget.Id, _expense, "Moradia", "1000.00");
        var planned = await CreatePlannedAsync(account, _expense, "300.00", new DateOnly(2026, 10, 10));
        Assert.Equal(("1000.00", "0.00", "300.00", "300.00", "700.00", "0.00", "30.00"),
            Values((await GetExecutionAsync(budget.Id)).Targets.Single()));

        var realizedAtPlannedValue = await RealizeAsync(planned, "300.00", new DateOnly(2026, 10, 11));
        Assert.Equal(("1000.00", "300.00", "0.00", "300.00", "700.00", "0.00", "30.00"),
            Values((await GetExecutionAsync(budget.Id)).Targets.Single()));
        await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{_family.Id}/transactions/{realizedAtPlannedValue}/cancel", _owner, null);

        var realized = await RealizeAsync(planned, "320.00", new DateOnly(2026, 10, 11));
        Assert.Equal(("1000.00", "320.00", "0.00", "320.00", "680.00", "0.00", "32.00"),
            Values((await GetExecutionAsync(budget.Id)).Targets.Single()));

        var cancel = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{_family.Id}/transactions/{realized}/cancel", _owner, null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(("1000.00", "0.00", "300.00", "300.00", "700.00", "0.00", "30.00"),
            Values((await GetExecutionAsync(budget.Id)).Targets.Single()));
    }

    [Fact]
    public async Task ExecutionUsesFinancialCompetenceAndRealizationDate()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("0.00", new DateOnly(2026, 9, 1));
        var budget = await CreateBudgetAsync("2026-10", null);
        await AddTargetAsync(budget.Id, _expense, "Moradia", "1000.00");
        await CreatePlannedAsync(account, _expense, "90.00", new DateOnly(2026, 9, 30));
        await CreatePlannedAsync(account, _expense, "110.00", new DateOnly(2026, 11, 1));
        var october = await CreatePlannedAsync(account, _expense, "300.00", new DateOnly(2026, 10, 10));
        await RealizeAsync(october, "320.00", new DateOnly(2026, 9, 29));

        var target = (await GetExecutionAsync(budget.Id)).Targets.Single();

        Assert.Equal(("1000.00", "0.00", "0.00", "0.00", "1000.00", "0.00", "0.00"), Values(target));
    }

    [Fact]
    public async Task CancelledMovementsArchivedTargetsAndArchivedBudgetAreExcluded()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync("0.00", new DateOnly(2026, 10, 1));
        var budget = await CreateBudgetAsync("2026-10", null);
        var withTarget = await AddTargetAsync(budget.Id, _expense, "Moradia", "100.00");
        var targetId = withTarget.Targets.Single().Id;
        var transaction = await CreateTransactionAsync(account, _expense, "20.00", new DateOnly(2026, 10, 2));
        var planned = await CreatePlannedAsync(account, _expense, "30.00", new DateOnly(2026, 10, 3));
        await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/transactions/{transaction}/cancel", _owner, null);
        await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/planned-transactions/{planned}/cancel", _owner, null);
        Assert.Equal(("100.00", "0.00", "0.00", "0.00", "100.00", "0.00", "0.00"),
            Values((await GetExecutionAsync(budget.Id)).Targets.Single()));

        await SendWithCsrfAsync(HttpMethod.Delete, $"{BudgetsUrl}/{budget.Id}/targets/{targetId}", _owner, null);
        Assert.Empty((await GetExecutionAsync(budget.Id)).Targets);
        await SendWithCsrfAsync(HttpMethod.Delete, $"{BudgetsUrl}/{budget.Id}", _owner, null);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"{BudgetsUrl}/{budget.Id}/execution", _member)).StatusCode);
    }

    [Fact]
    public async Task ExecutionAllowsInactiveCategoryAndEnforcesFamilyAuthorization()
    {
        await ArrangeAsync();
        var budget = await CreateBudgetAsync("2026-10", null);
        await AddTargetAsync(budget.Id, _expense, "Moradia", "100.00");
        await SendWithCsrfAsync(HttpMethod.Put, $"/api/families/{_family.Id}/categories/{_expense}", _owner,
            new { name = "Moradia", isActive = false });

        Assert.Single((await GetExecutionAsync(budget.Id)).Targets);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"{BudgetsUrl}/{budget.Id}/execution", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"/api/families/{_otherFamily.Id}/budgets/{budget.Id}/execution", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client.GetAsync($"{BudgetsUrl}/{budget.Id}/execution")).StatusCode);
    }

    private object TargetBody(Guid categoryId, string name, string amount) =>
        new { name, plannedAmount = amount, groupingType = "Category", categoryId };

    private async Task<BudgetDto> CreateBudgetAsync(string competence, string? description)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, BudgetsUrl, _owner, new { competence, description });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<BudgetDto>(response);
    }

    private async Task<BudgetDto> AddTargetAsync(Guid budgetId, Guid categoryId, string name, string amount)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl}/{budgetId}/targets", _owner,
            TargetBody(categoryId, name, amount));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<BudgetDto>(response);
    }

    private async Task<List<BudgetDto>> ListAsync(string? competence = null, bool includeArchived = false)
    {
        var query = new List<string>();
        if (competence is not null) query.Add($"competence={competence}");
        if (includeArchived) query.Add("includeArchived=true");
        var response = await SendAsync(HttpMethod.Get, BudgetsUrl + (query.Count == 0 ? "" : $"?{string.Join('&', query)}"), _member);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<List<BudgetDto>>(response);
    }

    private async Task<Guid> CreateCategoryAsync(Guid familyId, string session, string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/categories", session, new { name, type });
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateAccountAsync(string balance, DateOnly date, string name = "Conta")
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/accounts", _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance = balance, openingBalanceDate = date });
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private Task<Guid> CreatePlannedAsync(Guid accountId, string amount, DateOnly date) =>
        CreatePlannedAsync(accountId, _expense, amount, date);

    private async Task<Guid> CreatePlannedAsync(Guid accountId, Guid categoryId, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/planned-transactions", _owner,
            new { accountId, categoryId, type = "Expense", description = "Prevista", amount, dueDate = date });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CreateTransactionAsync(Guid accountId, Guid categoryId, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/transactions", _owner,
            new { accountId, categoryId, type = "Expense", description = "Efetiva", amount, transactionDate = date });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> RealizeAsync(Guid plannedId, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{_family.Id}/planned-transactions/{plannedId}/realize", _owner,
            new { amount, transactionDate = date });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task CreateTransferAsync(Guid source, Guid destination, string amount, DateOnly date)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/transfers", _owner,
            new { sourceAccountId = source, destinationAccountId = destination, amount, transferDate = date, description = "Interna" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<ExecutionDto> GetExecutionAsync(Guid budgetId)
    {
        var response = await SendAsync(HttpMethod.Get, $"{BudgetsUrl}/{budgetId}/execution", _member);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<ExecutionDto>(response);
    }

    private static (string, string, string, string, string, string, string?) Values(ExecutionAmountsDto value) =>
        (value.Planned, value.Realized, value.Committed, value.ProjectedConsumption, value.Available,
            value.ExceededAmount, value.ConsumptionPercentage);

    private static (string, string, string, string, string, string, string?) Values(TargetExecutionDto value) =>
        (value.Planned, value.Realized, value.Committed, value.ProjectedConsumption, value.Available,
            value.ExceededAmount, value.ConsumptionPercentage);

    private static async Task AssertValidationAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    private sealed record IdDto(Guid Id);
    private sealed record CategoryDto(Guid Id, string Name, bool IsActive);
    private sealed record TargetDto(Guid Id, string Name, string PlannedAmount, string GroupingType, CategoryDto Category, bool IsArchived);
    private sealed record BudgetDto(Guid Id, string Competence, string? Description, bool IsArchived,
        string TotalPlannedAmount, List<TargetDto> Targets, DateTimeOffset? ArchivedAt);
    private sealed record ExecutionDto(Guid BudgetId, string Competence, ExecutionAmountsDto Total, List<TargetExecutionDto> Targets);
    private sealed record ExecutionAmountsDto(string Planned, string Realized, string Committed,
        string ProjectedConsumption, string Available, string ExceededAmount, string? ConsumptionPercentage,
        string? Status, string? AlertMessage, int ApproachingCount, int ReachedCount, int ExceededCount);
    private sealed record TargetExecutionDto(Guid TargetId, Guid CategoryId, string Planned, string Realized,
        string Committed, string ProjectedConsumption, string Available, string ExceededAmount, string? ConsumptionPercentage,
        string? Status, string? AlertMessage);
}

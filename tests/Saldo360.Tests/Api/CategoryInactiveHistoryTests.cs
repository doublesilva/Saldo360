using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Recurrences;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>Fluxos de resolução e de histórico em torno de categorias inativas, de ponta a ponta pelo HTTP.</summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Categories")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CategoryInactiveHistoryTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private WorkspaceDto _family = null!;
    private Guid _account;
    private string Base => $"/api/financial-workspaces/{_family.Id}";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("inativa-dona");
        _family = await CreateWorkspaceAsync(_owner);
        _account = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/accounts", _owner, new { name = "Conta", type = "Checking", financialScope = "PF" }))).Id;
    }

    [Theory]
    [InlineData("end")]
    [InlineData("archive")]
    public async Task ActiveRecurrenceBlocksInactivation_UntilTheUserEndsOrRemovesIt_ThenInactivationIsAllowed(string resolution)
    {
        await ArrangeAsync();
        var category = (await CreateCategoryAsync("Aluguel")).Id;
        var recurrence = await AddRecurrenceAsync(category);
        var put = () => SendWithCsrfAsync(HttpMethod.Put, $"{Base}/categories/{category}", _owner, new { name = "Aluguel", isActive = false });

        Assert.Equal(HttpStatusCode.Conflict, (await put()).StatusCode);

        var resolved = resolution == "end"
            ? await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/recurrences/{recurrence}/end", _owner)
            : await SendWithCsrfAsync(HttpMethod.Delete, $"{Base}/recurrences/{recurrence}", _owner);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await put()).StatusCode);
        await using var db = Db();
        Assert.False((await db.FinancialCategories.AsNoTracking().SingleAsync(c => c.Id == category)).IsActive);
        Assert.Equal(category, (await db.FinancialRecurrences.AsNoTracking().SingleAsync(r => r.Id == recurrence)).FinancialCategoryId); // vínculo histórico preservado
    }

    [Fact]
    public async Task PendingPlannedTransactionWithAnInactiveCategory_CanBeCancelled_AndKeepsTheHistoricalLink()
    {
        await ArrangeAsync();
        var category = (await CreateCategoryAsync("Luz")).Id;
        var planned = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/planned-transactions", _owner,
            new { accountId = _account, categoryId = category, type = "Expense", description = "Luz", amount = "10.00", dueDate = "2026-10-20" }))).Id;
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{Base}/categories/{category}", _owner, new { name = "Luz", isActive = false })).StatusCode);

        var cancelled = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/planned-transactions/{planned}/cancel", _owner);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        await using var db = Db();
        Assert.Equal(category, (await db.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == planned)).CategoryId);
    }

    [Fact]
    public async Task BudgetLimitOfAnInactiveCategory_StaysListedAndCalculated()
    {
        await ArrangeAsync();
        var category = (await CreateCategoryAsync("Mercado")).Id;
        var budget = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets", _owner, new { competence = "2026-10", description = "Outubro" }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets/{budget}/targets", _owner,
            new { name = "Mercado", groupingType = "Category", categoryId = category, plannedAmount = "500.00" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/transactions", _owner,
            new { accountId = _account, categoryId = category, type = "Expense", description = "Feira", amount = "120.00", transactionDate = "2026-10-05" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{Base}/categories/{category}", _owner, new { name = "Mercado", isActive = false })).StatusCode);

        var execution = await SendAsync(HttpMethod.Get, $"{Base}/budgets/{budget}/execution", _owner);

        Assert.Equal(HttpStatusCode.OK, execution.StatusCode);
        var body = await execution.Content.ReadAsStringAsync();
        Assert.Contains("500.00", body); // limite continua exibido
        Assert.Contains("120.00", body); // e o realizado continua calculado
        using var document = JsonDocument.Parse(body);
        Assert.NotEqual(JsonValueKind.Null, document.RootElement.ValueKind);
    }

    private async Task<IdDto> CreateCategoryAsync(string name)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/categories", _owner, new { name, type = "Expense" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<IdDto>(response);
    }

    private async Task<Guid> AddRecurrenceAsync(Guid categoryId)
    {
        await using var db = Db();
        var recurrence = FinancialRecurrence.Create(_family.Id,
            new(_account, categoryId, CategoryType.Expense, "Aluguel", 100m, true, RecurrenceFrequency.Monthly, 1, new DateOnly(2027, 1, 10), null, null, 10),
            DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
        db.FinancialRecurrences.Add(recurrence);
        await db.SaveChangesAsync();
        return recurrence.Id;
    }

    private sealed record IdDto(Guid Id);
}

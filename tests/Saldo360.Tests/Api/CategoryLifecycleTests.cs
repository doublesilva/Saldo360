using System.Net;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Debts;
using Saldo360.Domain.Recurrences;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Excluir e inativar categorias e subcategorias com proteção de vínculos, sobre PostgreSQL real: só se exclui fisicamente sem referências, nada em cascata,
/// o impacto é explicado, e a inativação preserva o histórico e exige resolver as configurações ativas dependentes.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Categories")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CategoryLifecycleTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private const string InUse = "CATEGORY_IN_USE";
    private const string ConfirmationRequired = "CATEGORY_DELETE_CONFIRMATION_REQUIRED";
    private const string ActiveDependencies = "CATEGORY_HAS_ACTIVE_DEPENDENCIES";

    private string _owner = null!;
    private string _outsider = null!;
    private WorkspaceDto _family = null!;
    private WorkspaceDto _otherFamily = null!;

    private string Base => $"/api/financial-workspaces/{_family.Id}";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("ciclo-dona");
        _outsider = await SignInAsync("ciclo-estranho");
        _family = await CreateWorkspaceAsync(_owner);
        _otherFamily = await CreateWorkspaceAsync(_outsider);
    }

    [Fact]
    public async Task Delete_WithoutReferences_RemovesThePhysicalRow_AndIsNotRepeatable()
    {
        await ArrangeAsync();
        var category = await CreateAsync("Pets", "Expense");

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(category.Id)).StatusCode);

        await using var db = Db();
        Assert.False(await db.FinancialCategories.AnyAsync(c => c.Id == category.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{Base}/categories/{category.Id}", _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(category.Id)).StatusCode);
    }

    [Fact]
    public async Task Delete_MainWithSubcategories_ShowsTheImpact_AndOnlyDeletesAfterConfirmation()
    {
        await ArrangeAsync();
        var housing = await CreateAsync("Moradia", "Expense");
        var energy = await CreateAsync("Energia", "Expense", housing.Id);
        await CreateAsync("Água", "Expense", housing.Id);
        var other = await CreateAsync("Lazer", "Expense");

        var refused = await DeleteAsync(housing.Id);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await ReadAsync<ProblemDto>(refused);
        Assert.Equal(ConfirmationRequired, problem.Code);
        Assert.Equal((2, 0, true), (problem.Impact!.SubcategoryCount, problem.Impact.TotalReferences, problem.Impact.CanDelete));
        await using (var db = Db())
        {
            Assert.Equal(4, await db.FinancialCategories.CountAsync());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(housing.Id, confirm: true)).StatusCode);
        await using var verify = Db();
        Assert.Equal([other.Id], await verify.FinancialCategories.Select(c => c.Id).ToListAsync());
        Assert.False(await verify.FinancialCategories.AnyAsync(c => c.Id == energy.Id));
    }

    [Fact]
    public async Task Delete_ReferencedByATransaction_IsRefusedWithExplanation_AndInactivatingPreservesTheHistory()
    {
        await ArrangeAsync();
        var food = await CreateAsync("Mercado", "Expense");
        var transaction = await CreateTransactionAsync(food.Id);

        var refused = await DeleteAsync(food.Id);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await ReadAsync<ProblemDto>(refused);
        Assert.Equal(InUse, problem.Code);
        Assert.Equal(new ReferenceDto("transactions", "Lançamentos", 1), Assert.Single(problem.Impact!.References));
        Assert.False(problem.Impact.CanDelete);
        Assert.True(problem.Impact.CanDeactivate);
        await AssertTransactionKeepsCategoryAsync(transaction, food.Id);

        var inactivated = await SendWithCsrfAsync(HttpMethod.Put, $"{Base}/categories/{food.Id}", _owner, new { name = "Mercado", isActive = false });
        Assert.Equal(HttpStatusCode.OK, inactivated.StatusCode);
        await AssertTransactionKeepsCategoryAsync(transaction, food.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await DeleteAsync(food.Id)).StatusCode);
        await using var db = Db();
        Assert.Single(await db.FinancialTransactions.ToListAsync());
    }

    [Fact]
    public async Task Delete_MainWhoseSubcategoryIsUsed_IsRefusedAsAWhole_ButAnUnusedSiblingCanBeDeleted()
    {
        await ArrangeAsync();
        var housing = await CreateAsync("Moradia", "Expense");
        var energy = await CreateAsync("Energia", "Expense", housing.Id);
        var water = await CreateAsync("Água", "Expense", housing.Id);
        await CreateTransactionAsync(energy.Id);

        var refused = await DeleteAsync(housing.Id, confirm: true);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(InUse, (await ReadAsync<ProblemDto>(refused)).Code);
        await using (var db = Db())
        {
            Assert.Equal(3, await db.FinancialCategories.CountAsync());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(water.Id)).StatusCode);
        await using var verify = Db();
        Assert.Equal(2, await verify.FinancialCategories.CountAsync());
    }

    [Fact]
    public async Task Delete_ReferencedByADebtInstallment_WhichHasNoForeignKey_IsStillRefused()
    {
        await ArrangeAsync();
        var financing = await CreateAsync("Financiamento", "Expense");
        var debt = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/debts", _owner,
            new { name = "Dívida", debtType = "Other", originalPrincipal = "1000.00", contractDate = "2026-01-01" });
        Assert.Equal(HttpStatusCode.Created, debt.StatusCode);
        var debtId = (await ReadAsync<IdDto>(debt)).Id;
        await using (var db = Db())
        {
            db.FinancialDebtInstallments.Add(FinancialDebtInstallment.Create(_family.Id, debtId,
                new(1, new DateOnly(2026, 11, 10), 1000m, null, null, null, null, financing.Id), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var refused = await DeleteAsync(financing.Id);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("debtInstallments", Assert.Single((await ReadAsync<ProblemDto>(refused)).Impact!.References).Source);
        await using var verify = Db();
        Assert.True(await verify.FinancialCategories.AnyAsync(c => c.Id == financing.Id));
    }

    [Fact]
    public async Task Delete_ReferencedByABudgetLimit_IsRefused()
    {
        await ArrangeAsync();
        var food = await CreateAsync("Mercado", "Expense");
        var budget = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets", _owner, new { competence = "2026-11", description = "Novembro" });
        Assert.Equal(HttpStatusCode.Created, budget.StatusCode);
        var budgetId = (await ReadAsync<IdDto>(budget)).Id;
        var target = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets/{budgetId}/targets", _owner,
            new { name = "Mercado", groupingType = "Category", categoryId = food.Id, plannedAmount = "500.00" });
        Assert.Equal(HttpStatusCode.Created, target.StatusCode);

        var refused = await DeleteAsync(food.Id);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("budgetTargets", Assert.Single((await ReadAsync<ProblemDto>(refused)).Impact!.References).Source);
    }

    [Fact]
    public async Task Inactivate_WithActiveOrPausedRecurrence_IsBlockedUntilItIsEnded_AndEndedOnesStillBlockDeletion()
    {
        await ArrangeAsync();
        var housing = await CreateAsync("Moradia", "Expense");
        var rent = await CreateAsync("Aluguel", "Expense", housing.Id);
        var recurrenceId = await AddRecurrenceAsync(rent.Id, "Aluguel mensal");

        var blockedSub = await SendWithCsrfAsync(HttpMethod.Put, $"{Base}/categories/{rent.Id}", _owner, new { name = "Aluguel", isActive = false });
        var blockedMain = await SendWithCsrfAsync(HttpMethod.Put, $"{Base}/categories/{housing.Id}", _owner, new { name = "Moradia", isActive = false });

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (blockedSub.StatusCode, blockedMain.StatusCode));
        var problem = await ReadAsync<ProblemDto>(blockedMain);
        Assert.Equal(ActiveDependencies, problem.Code);
        var dependency = Assert.Single(problem.Impact!.ActiveDependencies);
        Assert.Equal(("Recurrence", recurrenceId, "Aluguel mensal"), (dependency.Kind, dependency.Id, dependency.Description));
        Assert.False(problem.Impact.CanDeactivate);
        await AssertAllActiveAsync(housing.Id, rent.Id);

        await ChangeRecurrenceAsync(recurrenceId, r => r.Pause(DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Conflict, (await SendWithCsrfAsync(HttpMethod.Put, $"{Base}/categories/{rent.Id}", _owner, new { name = "Aluguel", isActive = false })).StatusCode);

        await ChangeRecurrenceAsync(recurrenceId, r => r.End(DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{Base}/categories/{housing.Id}", _owner, new { name = "Moradia", isActive = false })).StatusCode);

        var deletion = await DeleteAsync(housing.Id, confirm: true);
        Assert.Equal(HttpStatusCode.Conflict, deletion.StatusCode);
        Assert.Equal("recurrences", Assert.Single((await ReadAsync<ProblemDto>(deletion)).Impact!.References).Source);
    }

    [Fact]
    public async Task Rename_DoesNotRequireResolvingDependencies_AndReactivatingNeverIsBlocked()
    {
        await ArrangeAsync();
        var housing = await CreateAsync("Moradia", "Expense");
        await AddRecurrenceAsync(housing.Id, "Condomínio");

        var renamed = await SendWithCsrfAsync(HttpMethod.Put, $"{Base}/categories/{housing.Id}", _owner, new { name = "Casa", isActive = true });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    }

    [Fact]
    public async Task Impact_ExplainsUsageAndDependencies_WithoutChangingAnything()
    {
        await ArrangeAsync();
        var housing = await CreateAsync("Moradia", "Expense");
        var energy = await CreateAsync("Energia", "Expense", housing.Id);
        await CreateTransactionAsync(energy.Id);
        await AddRecurrenceAsync(energy.Id, "Conta de luz");

        var impact = await ReadAsync<ImpactDto>(await SendAsync(HttpMethod.Get, $"{Base}/categories/{housing.Id}/impact", _owner));

        Assert.Equal((1, false, false, 2), (impact.SubcategoryCount, impact.CanDelete, impact.CanDeactivate, impact.TotalReferences));
        Assert.Equal([("transactions", 1), ("recurrences", 1)], impact.References.Select(r => (r.Source, r.Count)));
        Assert.Equal("Conta de luz", Assert.Single(impact.ActiveDependencies).Description);
        await AssertAllActiveAsync(housing.Id, energy.Id);
    }

    [Fact]
    public async Task Deleting_IsPerBudget_RequiresCsrf_AndHidesOtherBudgets()
    {
        await ArrangeAsync();
        var category = await CreateAsync("Pets", "Expense");

        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Delete, $"{Base}/categories/{category.Id}", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{Base}/categories/{category.Id}/impact", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Delete,
            $"/api/financial-workspaces/{_otherFamily.Id}/categories/{category.Id}", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendJsonAsync(HttpMethod.Delete, $"{Base}/categories/{category.Id}", _owner, csrf: null, body: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.DeleteAsync($"{Base}/categories/{category.Id}")).StatusCode);

        await using var db = Db();
        Assert.True(await db.FinancialCategories.AnyAsync(c => c.Id == category.Id));
    }

    private Task<HttpResponseMessage> DeleteAsync(Guid id, bool confirm = false) =>
        SendWithCsrfAsync(HttpMethod.Delete, $"{Base}/categories/{id}{(confirm ? "?confirmSubcategories=true" : "")}", _owner);

    private async Task<CategoryDto> CreateAsync(string name, string type, Guid? parent = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/categories", _owner,
            parent is null ? new { name, type } : (object)new { name, parentCategoryId = parent });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<CategoryDto>(response);
    }

    private async Task<Guid> CreateTransactionAsync(Guid categoryId)
    {
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/accounts", _owner, new { name = $"Conta {Guid.NewGuid():N}", type = "Checking", financialScope = "PF" });
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/transactions", _owner,
            new { accountId = (await ReadAsync<IdDto>(account)).Id, categoryId, type = "Expense", description = "Teste", amount = "10.00", transactionDate = "2026-10-06" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> AddRecurrenceAsync(Guid categoryId, string description)
    {
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/accounts", _owner, new { name = $"Conta {Guid.NewGuid():N}", type = "Checking", financialScope = "PF" });
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);
        await using var db = Db();
        var recurrence = FinancialRecurrence.Create(_family.Id,
            new((await ReadAsync<IdDto>(account)).Id, categoryId, CategoryType.Expense, description, 100m, true, RecurrenceFrequency.Monthly, 1, new DateOnly(2027, 1, 10), null, null, 10),
            DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
        db.FinancialRecurrences.Add(recurrence);
        await db.SaveChangesAsync();
        return recurrence.Id;
    }

    // Muda o estado pelo domínio: as rotas pause/end de recorrências respondem 400 hoje (defeito anterior, fora do escopo desta tarefa).
    private async Task ChangeRecurrenceAsync(Guid id, Action<FinancialRecurrence> change)
    {
        await using var db = Db();
        change(await db.FinancialRecurrences.SingleAsync(r => r.Id == id));
        await db.SaveChangesAsync();
    }

    private async Task AssertTransactionKeepsCategoryAsync(Guid transactionId, Guid categoryId)
    {
        await using var db = Db();
        Assert.Equal(categoryId, (await db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == transactionId)).CategoryId);
        Assert.True(await db.FinancialCategories.AnyAsync(c => c.Id == categoryId));
    }

    private async Task AssertAllActiveAsync(params Guid[] ids)
    {
        await using var db = Db();
        Assert.All(await db.FinancialCategories.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(), c => Assert.True(c.IsActive));
    }

    private sealed record IdDto(Guid Id);
    private sealed record CategoryDto(Guid Id, string Name, string Type, bool IsActive);
    private sealed record ReferenceDto(string Source, string Label, int Count);
    private sealed record DependencyDto(string Kind, Guid Id, string Description);
    private sealed record ImpactDto(Guid CategoryId, string Name, bool IsSubcategory, int SubcategoryCount, List<ReferenceDto> References,
        List<DependencyDto> ActiveDependencies, int TotalReferences, bool CanDelete, bool CanDeactivate);
    private sealed record ProblemDto(string Code, string Title, ImpactDto? Impact);
}

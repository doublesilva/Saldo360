using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// R02: hierarquia de categorias (principal + subcategoria), classificação única por despesa e tipo de despesa (fixa, variável, não definida),
/// sobre PostgreSQL real. Os casos de transações, planejadas e recorrências cobrem a propagação; o orçamento cobre a agregação sem dupla contagem.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Categories")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CategoryHierarchyAndExpenseNatureTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private string _outsider = null!;
    private WorkspaceDto _family = null!;
    private WorkspaceDto _otherFamily = null!;
    private Guid _account;
    private Guid _housing;
    private Guid _energy;
    private Guid _water;
    private Guid _food;
    private Guid _salary;

    private string Base => $"/api/financial-workspaces/{_family.Id}";
    private string CategoriesUrl => $"{Base}/categories";
    private string TransactionsUrl => $"{Base}/transactions";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("r02-dona");
        _outsider = await SignInAsync("r02-estranho");
        _family = await CreateWorkspaceAsync(_owner, "Família R02");
        _otherFamily = await CreateWorkspaceAsync(_outsider, "Família Estranha R02");
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/accounts", _owner, new { name = "Conta", type = "Checking", financialScope = "PF" });
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);
        _account = (await ReadAsync<IdDto>(account)).Id;
        _housing = (await CreateCategoryAsync("Moradia", "Expense")).Id;
        _energy = (await CreateCategoryAsync("Energia", "Expense", _housing)).Id;
        _water = (await CreateCategoryAsync("Água", "Expense", _housing)).Id;
        _food = (await CreateCategoryAsync("Alimentação", "Expense")).Id;
        _salary = (await CreateCategoryAsync("Salário", "Income")).Id;
    }

    // ---- Hierarquia -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Subcategory_InheritsTheTypeOfTheMain_AndExposesParentAndPath()
    {
        await ArrangeAsync();

        var sub = await ReadAsync<CategoryDto>(await SendAsync(HttpMethod.Get, $"{CategoriesUrl}/{_energy}", _owner));

        Assert.Equal(("Energia", "Expense", _housing, "Moradia", "Moradia / Energia", true, true), (sub.Name, sub.Type, sub.ParentCategoryId, sub.ParentName, sub.Path, sub.IsActive, sub.IsAvailable));
        var main = await ReadAsync<CategoryDto>(await SendAsync(HttpMethod.Get, $"{CategoriesUrl}/{_housing}", _owner));
        Assert.Equal((null, "Moradia"), (main.ParentCategoryId, main.Path));
    }

    [Fact]
    public async Task InvalidHierarchy_IsRejected_ThirdLevelInactiveParentUnknownParentOtherWorkspaceAndTypeMismatch()
    {
        await ArrangeAsync();
        var foreignMain = (await CreateCategoryAsync("Alheia", "Expense", workspace: _otherFamily.Id, session: _outsider)).Id;
        var inactive = (await CreateCategoryAsync("Antiga", "Expense")).Id;
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{inactive}", _owner, new { name = "Antiga", isActive = false })).StatusCode);

        await AssertValidationAsync(await PostCategoryAsync(new { name = "Fio", parentCategoryId = _energy }), "parentCategoryId"); // 3º nível
        await AssertValidationAsync(await PostCategoryAsync(new { name = "Fio", parentCategoryId = foreignMain }), "parentCategoryId"); // outro orçamento
        await AssertValidationAsync(await PostCategoryAsync(new { name = "Fio", parentCategoryId = Guid.CreateVersion7() }), "parentCategoryId"); // inexistente
        await AssertValidationAsync(await PostCategoryAsync(new { name = "Fio", parentCategoryId = inactive }), "parentCategoryId"); // principal inativa
        await AssertValidationAsync(await PostCategoryAsync(new { name = "Bônus", type = "Income", parentCategoryId = _housing }), "type"); // tipo diferente da principal

        await using var db = Db();
        Assert.Equal(6, await db.FinancialCategories.CountAsync(c => c.WorkspaceId == _family.Id)); // nada foi criado pelas recusas
    }

    [Fact]
    public async Task Names_AreUniquePerLevel_MainsPerTypeAndSubcategoriesWithinTheirMain()
    {
        await ArrangeAsync();
        var casa = (await CreateCategoryAsync("Casa", "Expense")).Id;

        Assert.Equal(HttpStatusCode.Conflict, (await PostCategoryAsync(new { name = "MORADIA", type = "Expense" })).StatusCode); // principal repetida
        Assert.Equal(HttpStatusCode.Conflict, (await PostCategoryAsync(new { name = "energia", parentCategoryId = _housing })).StatusCode); // irmã repetida
        Assert.Equal(HttpStatusCode.Created, (await PostCategoryAsync(new { name = "Energia", parentCategoryId = casa })).StatusCode); // outra principal: permitido
        Assert.Equal(HttpStatusCode.Created, (await PostCategoryAsync(new { name = "Alimentação", parentCategoryId = _housing })).StatusCode); // igual a uma principal, mas é subcategoria
        var rename = await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{_water}", _owner, new { name = "Energia", isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
    }

    [Fact]
    public async Task DeactivatingTheMain_MakesSubcategoriesUnavailableForNewClassification_ButExistingRecordsStayReadableAndEditable()
    {
        await ArrangeAsync();
        var existing = await CreateExpenseAsync(_energy, "120.00", "2026-10-05");

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{_housing}", _owner, new { name = "Moradia", isActive = false })).StatusCode);

        var visible = await ReadAsync<List<CategoryDto>>(await SendAsync(HttpMethod.Get, CategoriesUrl, _owner));
        Assert.DoesNotContain(visible, c => c.Id == _housing);
        var energy = visible.Single(c => c.Id == _energy);
        Assert.Equal((true, false), (energy.IsActive, energy.IsAvailable)); // ativa em si, indisponível por causa da principal
        var transaction = await ReadAsync<TransactionDto>(await SendAsync(HttpMethod.Get, $"{TransactionsUrl}/{existing}", _owner));
        Assert.Equal("Moradia / Energia", transaction.Category!.Path);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostTransactionAsync(_energy, "10.00", "2026-10-06")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostTransactionAsync(_housing, "10.00", "2026-10-06")).StatusCode);
        var unchanged = await SendWithCsrfAsync(HttpMethod.Put, $"{TransactionsUrl}/{existing}", _owner,
            new { accountId = _account, categoryId = _energy, type = "Expense", description = "Conta de luz", amount = "121.00", transactionDate = "2026-10-05" });
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode); // mesma categoria mantida: editável

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{_housing}", _owner, new { name = "Moradia", isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostTransactionAsync(_energy, "10.00", "2026-10-06")).StatusCode);
    }

    // ---- Tipo de despesa ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Fixed", "Fixed")]
    [InlineData("Variable", "Variable")]
    [InlineData("Undefined", "Undefined")]
    [InlineData(null, "Undefined")]
    public async Task ExpenseNature_IsExplicit_AndNeverSilentlyVariable(string? sent, string stored)
    {
        await ArrangeAsync();

        var response = await PostTransactionAsync(_food, "50.00", "2026-10-07", sent);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(stored, (await ReadAsync<TransactionDto>(response)).ExpenseNature);
    }

    [Fact]
    public async Task ExpenseNature_DoesNotApplyToIncome_AndInvalidValuesAreRejected()
    {
        await ArrangeAsync();

        await AssertValidationAsync(await PostTransactionAsync(_salary, "100.00", "2026-10-07", "Fixed", "Income"), "expenseNature");
        await AssertValidationAsync(await PostTransactionAsync(_food, "100.00", "2026-10-07", "Sometimes"), "expenseNature");
        var income = await ReadAsync<TransactionDto>(await PostTransactionAsync(_salary, "100.00", "2026-10-07", null, "Income"));
        Assert.Equal("Undefined", income.ExpenseNature);
        await AssertValidationAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{TransactionsUrl}/{income.Id}/expense-nature", _owner, new { expenseNature = "Variable" }), "expenseNature");
    }

    [Fact]
    public async Task ChangingTheNature_DoesNotChangeCategoryAmountDateOrBalance_AndIsRestrictedToTheWorkspace()
    {
        await ArrangeAsync();
        var created = await ReadAsync<TransactionDto>(await PostTransactionAsync(_energy, "200.00", "2026-10-08", "Variable"));
        var balanceBefore = await BalanceAsync();

        var changed = await SendWithCsrfAsync(HttpMethod.Put, $"{TransactionsUrl}/{created.Id}/expense-nature", _owner, new { expenseNature = "Fixed" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var after = await ReadAsync<TransactionDto>(changed);
        Assert.Equal(("Fixed", "200.00", new DateOnly(2026, 10, 8), _energy), (after.ExpenseNature, after.Amount, after.TransactionDate, after.Category!.Id));
        Assert.Equal(balanceBefore, await BalanceAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await SendWithCsrfAsync(HttpMethod.Put, $"{TransactionsUrl}/{created.Id}/expense-nature", _owner, new { expenseNature = "Fixed", amount = "1.00" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, $"{TransactionsUrl}/{created.Id}/expense-nature", _outsider, new { expenseNature = "Variable" })).StatusCode);
    }

    [Theory]
    [InlineData("CreditCardInvoicePayment")]
    [InlineData("DebtInstallmentPayment")]
    public async Task PaymentLedgerTransactions_NeverReceiveAnExpenseNature_SoTheCommitmentIsNotCountedTwice(string origin)
    {
        await ArrangeAsync();
        var payment = (await ReadAsync<TransactionDto>(await PostTransactionAsync(_food, "300.00", "2026-10-09"))).Id;
        await using (var db = Db())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE financial_transactions DISABLE TRIGGER USER; UPDATE financial_transactions SET origin = {0} WHERE id = {1}; ALTER TABLE financial_transactions ENABLE TRIGGER USER;", origin, payment);

        await AssertValidationAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{TransactionsUrl}/{payment}/expense-nature", _owner, new { expenseNature = "Fixed" }), "expenseNature");
    }

    [Fact]
    public async Task FixedAndVariable_CanLiveInTheSameCategory_AndFixedInDifferentOnes_AndTheFilterSeparatesThem()
    {
        await ArrangeAsync();
        var fixedEnergy = (await ReadAsync<TransactionDto>(await PostTransactionAsync(_energy, "100.00", "2026-10-01", "Fixed"))).Id;
        var variableEnergy = (await ReadAsync<TransactionDto>(await PostTransactionAsync(_energy, "30.00", "2026-10-02", "Variable"))).Id;
        var fixedFood = (await ReadAsync<TransactionDto>(await PostTransactionAsync(_food, "500.00", "2026-10-03", "Fixed"))).Id;
        var undefinedFood = (await ReadAsync<TransactionDto>(await PostTransactionAsync(_food, "40.00", "2026-10-04"))).Id;

        Assert.Equal(new[] { fixedEnergy, fixedFood }.Order(), (await ListIdsAsync("expenseNature=Fixed")).Order());
        Assert.Equal([variableEnergy], await ListIdsAsync("expenseNature=Variable"));
        Assert.Equal([undefinedFood], await ListIdsAsync("expenseNature=Undefined"));
        Assert.Equal([fixedEnergy], await ListIdsAsync($"expenseNature=Fixed&categoryId={_energy}")); // combina com o filtro de categoria
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{TransactionsUrl}?expenseNature=Maybe", _owner)).StatusCode);
    }

    [Fact]
    public async Task MainCategoryFilter_IncludesItsSubcategories_AndTheSubcategoryFilterReturnsOnlyItsOwn()
    {
        await ArrangeAsync();
        var direct = await CreateExpenseAsync(_housing, "100.00", "2026-10-01");
        var energy = await CreateExpenseAsync(_energy, "200.00", "2026-10-02");
        var water = await CreateExpenseAsync(_water, "50.00", "2026-10-03");
        var food = await CreateExpenseAsync(_food, "10.00", "2026-10-04");

        Assert.Equal(new[] { direct, energy, water }.Order(), (await ListIdsAsync($"categoryId={_housing}")).Order());
        Assert.Equal([energy], await ListIdsAsync($"categoryId={_energy}"));
        Assert.Equal([food], await ListIdsAsync($"categoryId={_food}"));
    }

    // ---- Propagação ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlannedTransaction_CarriesCategoryAndNatureIntoItsRealization()
    {
        await ArrangeAsync();
        var planned = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/planned-transactions", _owner,
            new { accountId = _account, categoryId = _energy, type = "Expense", description = "Luz", amount = "180.00", dueDate = "2026-10-20", expenseNature = "Fixed" });
        Assert.Equal(HttpStatusCode.Created, planned.StatusCode);
        var plannedId = (await ReadAsync<IdDto>(planned)).Id;

        var realized = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/planned-transactions/{plannedId}/realize", _owner, new { amount = "175.00", transactionDate = "2026-10-21" });

        Assert.Equal(HttpStatusCode.Created, realized.StatusCode);
        var transaction = await ReadAsync<TransactionDto>(await SendAsync(HttpMethod.Get, $"{TransactionsUrl}/{(await ReadAsync<IdDto>(realized)).Id}", _owner));
        Assert.Equal(("Fixed", _energy), (transaction.ExpenseNature, transaction.Category!.Id));
    }

    [Fact]
    public async Task Recurrence_MaterializesWithItsNature_AndEditingItOnlyAffectsFutureMaterializations()
    {
        await ArrangeAsync();
        var created = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/recurrences", _owner, new
        {
            accountId = _account, categoryId = _water, type = "Expense", description = "Água", amount = "90.00", isEstimated = false,
            frequency = "Monthly", interval = 1, startDate = "2026-11-10", dayOfMonth = 10, expenseNature = "Fixed"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var recurrenceId = (await ReadAsync<IdDto>(created)).Id;

        var first = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/recurrences/{recurrenceId}/materialize", _owner, new { from = "2026-11-01", through = "2026-11-30" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        await using var db = Db();
        var planned = await db.FinancialPlannedTransactions.AsNoTracking().SingleAsync();
        Assert.Equal((_water, Saldo360.Domain.Categories.ExpenseNature.Fixed), (planned.CategoryId, planned.Nature));
    }

    // ---- Orçamento sem dupla contagem -------------------------------------------------------------------------------------

    [Fact]
    public async Task BudgetTargetOnTheMain_CoversDirectAndSubcategoryRecordsWithoutDoubleCounting_AndOverlappingTargetsAreRejected()
    {
        await ArrangeAsync();
        var budget = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets", _owner, new { competence = "2026-10", description = (string?)null });
        Assert.Equal(HttpStatusCode.Created, budget.StatusCode);
        var budgetId = (await ReadAsync<IdDto>(budget)).Id;
        await CreateExpenseAsync(_housing, "100.00", "2026-10-05"); // direto na principal
        await CreateExpenseAsync(_energy, "200.00", "2026-10-06"); // na subcategoria

        var main = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets/{budgetId}/targets", _owner, new { name = "Moradia", plannedAmount = "1000.00", groupingType = "Category", categoryId = _housing });
        Assert.Equal(HttpStatusCode.Created, main.StatusCode);
        var overlapping = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets/{budgetId}/targets", _owner, new { name = "Energia", plannedAmount = "300.00", groupingType = "Category", categoryId = _energy });
        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);
        var sibling = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets/{budgetId}/targets", _owner, new { name = "Alimentação", plannedAmount = "400.00", groupingType = "Category", categoryId = _food });
        Assert.Equal(HttpStatusCode.Created, sibling.StatusCode);

        using var execution = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Base}/budgets/{budgetId}/execution", _owner)).Content.ReadAsStringAsync());
        var targets = execution.RootElement.GetProperty("targets").EnumerateArray().ToList();
        var housing = targets.Single(t => t.GetProperty("categoryId").GetGuid() == _housing);
        Assert.Equal(("1000.00", "300.00"), (housing.GetProperty("planned").GetString(), housing.GetProperty("realized").GetString())); // 100 + 200, e não 500
        Assert.Equal("300.00", execution.RootElement.GetProperty("categories").GetProperty("consumed").GetString());
    }

    // ---- Integridade no banco (ninguém contorna a regra falando direto com o PostgreSQL) ------------------------------------

    [Fact]
    public async Task Database_RejectsThirdLevelParentWithChildrenBecomingChildTypeMismatchCrossWorkspaceAndSelfParent()
    {
        await ArrangeAsync();
        var loose = (await CreateCategoryAsync("Solta", "Expense")).Id;
        var foreign = (await CreateCategoryAsync("Alheia", "Expense", workspace: _otherFamily.Id, session: _outsider)).Id;
        await using var db = Db();

        async Task<string?> SqlStateOfAsync(FormattableString sql)
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql));
            return error.SqlState;
        }

        Assert.Equal("23514", await SqlStateOfAsync($"UPDATE financial_categories SET parent_category_id = {_energy} WHERE id = {loose}")); // 3º nível
        Assert.Equal("23514", await SqlStateOfAsync($"UPDATE financial_categories SET parent_category_id = {_food} WHERE id = {_housing}")); // principal com filhas virando filha
        Assert.Equal("23503", await SqlStateOfAsync($"UPDATE financial_categories SET parent_category_id = {_salary} WHERE id = {_water}")); // tipo diferente
        Assert.Equal("23503", await SqlStateOfAsync($"UPDATE financial_categories SET parent_category_id = {foreign} WHERE id = {_water}")); // outro orçamento
        Assert.Equal("23514", await SqlStateOfAsync($"UPDATE financial_categories SET parent_category_id = id WHERE id = {_food}")); // si mesma como principal
        Assert.Equal("23505", await SqlStateOfAsync($"UPDATE financial_categories SET name = 'ENERGIA' WHERE id = {_water}")); // irmã homônima, sem diferenciar caixa
    }

    [Fact]
    public async Task Database_RejectsNatureOnIncomeAndUnknownNatureValues()
    {
        await ArrangeAsync();
        var income = (await ReadAsync<TransactionDto>(await PostTransactionAsync(_salary, "100.00", "2026-10-07", null, "Income"))).Id;
        var expense = (await ReadAsync<TransactionDto>(await PostTransactionAsync(_food, "10.00", "2026-10-07", "Fixed"))).Id;
        await using var db = Db();

        var onIncome = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_transactions SET expense_nature = 'Fixed' WHERE id = {income}"));
        var unknown = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_transactions SET expense_nature = 'Sometimes' WHERE id = {expense}"));

        Assert.Equal(("23514", "23514"), (onIncome.SqlState, unknown.SqlState));
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    private Task<HttpResponseMessage> PostCategoryAsync(object body) => SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, body);

    private async Task<CategoryDto> CreateCategoryAsync(string name, string type, Guid? parent = null, Guid? workspace = null, string? session = null)
    {
        var url = $"/api/financial-workspaces/{workspace ?? _family.Id}/categories";
        var response = await SendWithCsrfAsync(HttpMethod.Post, url, session ?? _owner, parent is null ? new { name, type } : (object)new { name, parentCategoryId = parent });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<CategoryDto>(response);
    }

    private Task<HttpResponseMessage> PostTransactionAsync(Guid category, string amount, string date, string? nature = null, string type = "Expense") =>
        SendWithCsrfAsync(HttpMethod.Post, TransactionsUrl, _owner,
            new { accountId = _account, categoryId = category, type, description = "Teste", amount, transactionDate = date, expenseNature = nature });

    private async Task<Guid> CreateExpenseAsync(Guid category, string amount, string date)
    {
        var response = await PostTransactionAsync(category, amount, date);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<List<Guid>> ListIdsAsync(string query)
    {
        var response = await SendAsync(HttpMethod.Get, $"{TransactionsUrl}?{query}&pageSize=100", _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PageDto>(response)).Items.Select(t => t.Id).ToList();
    }

    private async Task<string> BalanceAsync()
    {
        await using var db = Db();
        return string.Join('|', await db.FinancialTransactions.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Amount.ToString()).ToListAsync());
    }

    private static async Task AssertValidationAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"esperado erro em '{field}'");
    }

    private sealed record IdDto(Guid Id);
    private sealed record CategoryDto(Guid Id, string Name, string Type, bool IsActive, Guid? ParentCategoryId, string? ParentName, bool IsAvailable, string Path);
    private sealed record RefDto(Guid Id, string Name, bool IsActive, string? ParentName, string Path);
    private sealed record TransactionDto(Guid Id, string Type, string Amount, DateOnly TransactionDate, RefDto? Category, string ExpenseNature);
    private sealed record PageDto(List<TransactionDto> Items);
}

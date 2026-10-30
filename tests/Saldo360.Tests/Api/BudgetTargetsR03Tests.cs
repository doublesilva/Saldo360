using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// R03: metas por categoria, de despesas fixas e de todos os cartões no mesmo orçamento mensal. Os cenários A–H do aceite rodam sobre PostgreSQL real e
/// conferem a mesma lista de registros no resumo, na composição e no painel. Valores sintéticos.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Budgets")]
[Trait("Dependency", "PostgreSQL")]
public sealed class BudgetTargetsR03Tests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static readonly DateOnly Today = BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static string Competence(int offset = 0) => Today.AddMonths(offset).ToString("yyyy-MM");
    private static string FirstDay(int offset = 0) => $"{Competence(offset)}-01";

    private string _owner = null!;
    private string _outsider = null!;
    private WorkspaceDto _family = null!;
    private Guid _account;
    private Guid _housing, _water, _power, _media, _leisure, _food;

    private string Base => $"/api/financial-workspaces/{_family.Id}";

    private async Task ArrangeAsync(string who)
    {
        _owner = await SignInAsync($"r03-{who}");
        _outsider = await SignInAsync($"r03-{who}-out");
        _family = await CreateWorkspaceAsync(_owner, "Família R03");
        _account = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/accounts", _owner, new { name = "Conta", type = "Checking", financialScope = "PF" }))).Id;
        _housing = await CategoryAsync("Moradia");
        _water = await CategoryAsync("Água", _housing);
        _power = await CategoryAsync("Luz", _housing);
        _media = await CategoryAsync("Mídia");
        _leisure = await CategoryAsync("Lazer");
        _food = await CategoryAsync("Alimentação");
    }

    // ---- A. Cartões consolidados -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_AllCardsSumsEveryCardOfTheCompetenceAgainstASingleLimitWithNoIndividualCeiling()
    {
        await ArrangeAsync("a");
        var black = await CardAsync("Black"); var nubank = await CardAsync("Nubank"); var credicard = await CardAsync("Credicard");
        await PurchaseAsync(black, _food, "3000.00"); await PurchaseAsync(nubank, _food, "1000.00"); await PurchaseAsync(credicard, _leisure, "200.00");
        var budget = await BudgetAsync(Competence());
        var target = await AddTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "5000.00" });

        var run = (await ExecutionAsync(budget)).Target(target);
        var composition = await CompositionAsync(budget, target);

        Assert.Equal(("5000.00", "4200.00", "800.00", "0.00", "AllCards"), (run.Text("planned"), run.Text("consumed"), run.Text("available"), run.Text("exceededAmount"), run.Text("groupingType")));
        Assert.Equal(new[] { "Black", "Nubank", "Credicard" }, composition.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("cardName").GetString()).ToArray()); // participação por cartão, só informativa
        Assert.Equal(3, composition.GetProperty("totalCount").GetInt32());
        // Não existe teto individual: nem por cartão, nem por categoria numa meta de cartões.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "10.00", cardId = black })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "10.00", categoryId = _food })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "10.00" })).StatusCode); // uma por orçamento
    }

    // ---- B. Fixas em categorias distintas --------------------------------------------------------------------------------------

    [Fact]
    public async Task B_FixedExpensesSumOnlyFixedAcrossCategoriesAndPaymentMethodsAndFlagUndefinedOnesWithoutInflatingTheTarget()
    {
        await ArrangeAsync("b");
        var card = await CardAsync("Cartão");
        await TransactionAsync(_water, "150.00", "Fixed"); await TransactionAsync(_power, "200.00", "Fixed");
        await PurchaseAsync(card, _media, "50.00", "Fixed", description: "Streaming");
        await TransactionAsync(_leisure, "140.00", "Fixed"); await TransactionAsync(_food, "100.00", "Variable", "Restaurante");
        await TransactionAsync(_food, "30.00", null, "Compra sem tipo definido");
        var budget = await BudgetAsync(Competence());
        var target = await AddTargetAsync(budget, new { groupingType = "FixedExpenses", plannedAmount = "600.00" });

        var run = (await ExecutionAsync(budget)).Target(target);
        var composition = await CompositionAsync(budget, target);

        Assert.Equal(("540.00", "60.00"), (run.Text("consumed"), run.Text("available"))); // Restaurante (variável) e a despesa sem tipo ficam de fora
        Assert.Equal(4, composition.GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain(composition.GetProperty("items").EnumerateArray(), i => i.GetProperty("description").GetString() is "Restaurante" or "Compra sem tipo definido");
        // A despesa sem tipo vira indicação de classificação pendente, sem entrar na meta.
        var coverage = run.GetProperty("coverage");
        Assert.Equal((1, "30.00"), (coverage.GetProperty("undefinedNatureCount").GetInt32(), coverage.GetProperty("undefinedNatureAmount").GetString()));
    }

    // ---- C. Sobreposição entre perspectivas ------------------------------------------------------------------------------------

    [Fact]
    public async Task C_OneStreamingExpenseOnACardParticipatesInMediaFixedAndCardsWithoutBecomingThreeExpenses()
    {
        await ArrangeAsync("c");
        var card = await CardAsync("Cartão");
        await PurchaseAsync(card, _media, "50.00", "Fixed", description: "Streaming");
        var budget = await BudgetAsync(Competence());
        var mediaTarget = await AddTargetAsync(budget, new { name = "Mídia", groupingType = "Category", categoryId = _media, plannedAmount = "100.00" });
        var fixedTarget = await AddTargetAsync(budget, new { groupingType = "FixedExpenses", plannedAmount = "600.00" });
        var cardsTarget = await AddTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "5000.00" });

        var execution = await ExecutionAsync(budget);

        Assert.All(new[] { mediaTarget, fixedTarget, cardsTarget }, id => Assert.Equal("50.00", execution.Target(id).Text("consumed")));
        // Perspectivas nunca se somam: o resumo conjunto só considera as metas de categoria (50, e não 150) e o total planejado do orçamento também.
        Assert.Equal(("50.00", "100.00", 1), (execution.Root.GetProperty("categories").GetProperty("consumed").GetString(), execution.Root.GetProperty("categories").GetProperty("planned").GetString(),
            execution.Root.GetProperty("categories").GetProperty("targetCount").GetInt32()));
        using var listed = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Base}/budgets/{budget}", _owner)).Content.ReadAsStringAsync());
        Assert.Equal("100.00", listed.RootElement.GetProperty("totalPlannedAmount").GetString());
        await using var db = Db();
        Assert.Equal((1, 1), (await db.FinancialCreditCardPurchases.CountAsync(), await db.FinancialCreditCardInstallments.CountAsync())); // continua sendo uma única despesa
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
    }

    // ---- D. Hierarquia, e coexistência com fixas e cartões ---------------------------------------------------------------------

    [Fact]
    public async Task D_MainCategoryCoversItsSubcategoriesOnceAndCoexistsWithFixedAndCardsButNotWithItsOwnSubcategoryTarget()
    {
        await ArrangeAsync("d");
        await TransactionAsync(_housing, "100.00", "Variable"); await TransactionAsync(_power, "200.00", "Variable");
        var budget = await BudgetAsync(Competence());

        var housing = await AddTargetAsync(budget, new { name = "Moradia", groupingType = "Category", categoryId = _housing, plannedAmount = "1000.00" });
        await AddTargetAsync(budget, new { groupingType = "FixedExpenses", plannedAmount = "10.00" });
        await AddTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "10.00" }); // coexistem com a categoria sem 409
        var overlap = await PostTargetAsync(budget, new { name = "Luz", groupingType = "Category", categoryId = _power, plannedAmount = "10.00" });

        Assert.Equal("300.00", (await ExecutionAsync(budget)).Target(housing).Text("consumed")); // 100 + 200, e não 500
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
    }

    // ---- E. Parcela e pagamento ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task E_AnInstallmentPurchaseConsumesEachInvoiceCompetenceAndThePaymentOfTheInvoiceDoesNotConsumeAgain()
    {
        await ArrangeAsync("e");
        var card = await CardAsync("Cartão");
        await PurchaseAsync(card, _food, "900.00", "Fixed", installments: 3, description: "Geladeira");
        var budgets = new[] { await BudgetAsync(Competence()), await BudgetAsync(Competence(1)), await BudgetAsync(Competence(2)), await BudgetAsync(Competence(3)) };
        var targets = new List<Guid>();
        foreach (var budget in budgets) targets.Add(await AddTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "5000.00" }));
        var fixedTarget = await AddTargetAsync(budgets[0], new { groupingType = "FixedExpenses", plannedAmount = "5000.00" });
        // O pagamento da fatura é uma saída da conta, não consumo: mesmo categorizado e fixo, ele não entra em nenhuma meta.
        var payment = await TransactionAsync(_food, "300.00", "Fixed", "Pagamento da fatura");
        await using (var db = Db())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE financial_transactions DISABLE TRIGGER USER; UPDATE financial_transactions SET origin = 'CreditCardInvoicePayment' WHERE id = {0}; ALTER TABLE financial_transactions ENABLE TRIGGER USER;", payment);

        var consumption = new List<string>();
        for (var i = 0; i < budgets.Length; i++) consumption.Add((await ExecutionAsync(budgets[i])).Target(targets[i]).Text("consumed"));

        Assert.Equal(["300.00", "300.00", "300.00", "0.00"], consumption); // R$ 300 em cada competência, nunca R$ 900 no primeiro mês
        Assert.Equal("300.00", (await ExecutionAsync(budgets[0])).Target(fixedTarget).Text("consumed")); // só a parcela; o pagamento (300) não soma
    }

    // ---- F. Previsto e realizado -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task F_AForecastIsAdditionalUntilRealizedAndThenTheEffectiveValueReplacesItWithoutCountingTwice()
    {
        await ArrangeAsync("f");
        var budget = await BudgetAsync(Competence());
        var target = await AddTargetAsync(budget, new { groupingType = "FixedExpenses", plannedAmount = "500.00" });
        var plannedId = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/planned-transactions", _owner,
            new { accountId = _account, categoryId = _power, type = "Expense", description = "Energia", amount = "200.00", dueDate = Today, expenseNature = "Fixed" }))).Id;

        var forecast = (await ExecutionAsync(budget)).Target(target);
        Assert.Equal(("0.00", "200.00", "200.00", "500.00", "300.00"), (forecast.Text("consumed"), forecast.Text("committed"), forecast.Text("projectedConsumption"), forecast.Text("available"), forecast.Text("availableProjected")));

        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/planned-transactions/{plannedId}/realize", _owner, new { amount = "210.00", transactionDate = Today })).StatusCode);
        var realized = (await ExecutionAsync(budget)).Target(target);

        Assert.Equal(("210.00", "0.00", "210.00", "290.00"), (realized.Text("consumed"), realized.Text("committed"), realized.Text("projectedConsumption"), realized.Text("available")));
    }

    // ---- E2. Dívida: obrigação e pagamento são uma única despesa ------------------------------------------------------------

    [Fact]
    public async Task E2_ADebtInstallmentAndItsPaymentAreASingleObligationNeverContractPrincipalPlusInstallmentPlusPayment()
    {
        await ArrangeAsync("debt");
        var bank = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/accounts", _owner,
            new { name = "Conta devedora", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = "2026-01-01" }))).Id;
        var debt = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/debts", _owner,
            new { name = "Empréstimo", debtType = "PersonalLoan", responsibleAccountId = bank, originalPrincipal = "9000.00", contractDate = "2026-01-01" }))).Id;
        var installment = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/debts/{debt}/installments", _owner,
            new { installmentNumber = 1, dueDate = Today, scheduledAmount = "1000.00", categoryId = _housing, expenseNature = "Fixed" });
        Assert.Equal(HttpStatusCode.Created, installment.StatusCode);
        var installmentId = (await ReadAsync<IdDto>(installment)).Id;
        var budget = await BudgetAsync(Competence());
        var fixedTarget = await AddTargetAsync(budget, new { groupingType = "FixedExpenses", plannedAmount = "2000.00" });
        var housing = await AddTargetAsync(budget, new { name = "Moradia", groupingType = "Category", categoryId = _housing, plannedAmount = "2000.00" });

        var before = await ExecutionAsync(budget);
        Assert.Equal(("0.00", "1000.00"), (before.Target(fixedTarget).Text("consumed"), before.Target(fixedTarget).Text("committed")));

        var payment = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/debts/{debt}/installments/{installmentId}/payments", _owner,
            new { operationId = "r03-payment", financialAccountId = bank, paymentDate = Today, amount = "400.00" });
        Assert.True(payment.IsSuccessStatusCode, await payment.Content.ReadAsStringAsync());
        var after = await ExecutionAsync(budget);

        // 400 pagos (consumo) + 600 ainda devidos (previsto adicional) = a mesma parcela de 1.000; o lançamento bancário do pagamento não conta de novo.
        foreach (var id in new[] { fixedTarget, housing })
            Assert.Equal(("400.00", "600.00", "1000.00"), (after.Target(id).Text("consumed"), after.Target(id).Text("committed"), after.Target(id).Text("projectedConsumption")));
    }

    // ---- G. Estorno ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task G_ARefundReducesTheCardConsumptionOfTheInvoiceCompetenceInCategoryAndCardsTargets()
    {
        await ArrangeAsync("g");
        var card = await CardAsync("Cartão");
        await PurchaseAsync(card, _food, "100.00");
        await using (var db = Db())
        {
            var invoice = await db.FinancialCreditCardInvoices.SingleAsync();
            var owner = await db.FinancialWorkspaces.Where(w => w.Id == _family.Id).Select(w => w.OwnerUserId).SingleAsync();
            db.FinancialCreditCardInvoiceAdjustments.Add(FinancialCreditCardInvoiceAdjustment.Create(_family.Id, card, invoice.Id, FinancialCreditCardInvoiceAdjustmentType.Refund,
                FinancialCreditCardInvoiceAdjustmentNature.Credit, Today, "Estorno", 30m, "r03-refund", _food, owner, DateTimeOffset.UtcNow));
            // Saldo anterior e financiado não são consumo do mês.
            db.FinancialCreditCardInvoiceAdjustments.Add(FinancialCreditCardInvoiceAdjustment.Create(_family.Id, card, invoice.Id, FinancialCreditCardInvoiceAdjustmentType.PreviousBalance,
                FinancialCreditCardInvoiceAdjustmentNature.Debit, Today, "Saldo anterior", 999m, "r03-previous", null, owner, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var budget = await BudgetAsync(Competence());
        var cards = await AddTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "500.00" });
        var food = await AddTargetAsync(budget, new { name = "Alimentação", groupingType = "Category", categoryId = _food, plannedAmount = "500.00" });

        var execution = await ExecutionAsync(budget);

        Assert.Equal(("70.00", "70.00"), (execution.Target(cards).Text("consumed"), execution.Target(food).Text("consumed")));
    }

    // ---- H. Limites e ausência de dados ----------------------------------------------------------------------------------------

    [Fact]
    public async Task H_ZeroLimitMissingTargetsNoCardsAndIncompleteDataAreNeverPresentedAsAConfirmedZero()
    {
        await ArrangeAsync("h");
        await TransactionAsync(_food, "10.00", "Variable");
        var budget = await BudgetAsync(Competence());
        Assert.Empty((await ExecutionAsync(budget)).Root.GetProperty("targets").EnumerateArray()); // sem meta definida: nada de linhas zeradas

        var zero = await AddTargetAsync(budget, new { name = "Alimentação", groupingType = "Category", categoryId = _food, plannedAmount = "0.00" });
        var untouched = await AddTargetAsync(budget, new { groupingType = "FixedExpenses", plannedAmount = "0.00" });
        var cards = await AddTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "100.00" });
        var execution = await ExecutionAsync(budget);

        var withConsumption = execution.Target(zero);
        Assert.Equal(("0.00", "10.00", "-10.00", "10.00", "Exceeded"), (withConsumption.Text("planned"), withConsumption.Text("consumed"), withConsumption.Text("available"), withConsumption.Text("exceededAmount"), withConsumption.Text("status")));
        Assert.Equal(JsonValueKind.Null, withConsumption.GetProperty("consumptionPercentage").ValueKind); // limite zero com consumo: sem divisão por zero
        var noConsumption = execution.Target(untouched);
        Assert.Equal(("0.00", "WithinBudget"), (noConsumption.Text("consumed"), noConsumption.Text("status")));
        Assert.Equal("0.00", noConsumption.Text("consumptionPercentage"));
        // Sem cartões cadastrados, o "0,00" de cartões não é um zero confirmado: a cobertura avisa que não há dado.
        Assert.True(execution.Target(cards).GetProperty("coverage").GetProperty("noCards").GetBoolean());
        // Despesa sem tipo definido continua entrando em categoria, fica fora das fixas e é sinalizada como classificação pendente.
        await TransactionAsync(_food, "25.00", null, "Sem tipo");
        var again = await ExecutionAsync(budget);
        Assert.Equal("35.00", again.Target(zero).Text("consumed"));
        Assert.Equal("0.00", again.Target(untouched).Text("consumed"));
        Assert.Equal((1, "25.00"), (again.Target(untouched).GetProperty("coverage").GetProperty("undefinedNatureCount").GetInt32(), again.Target(untouched).GetProperty("coverage").GetProperty("undefinedNatureAmount").GetString()));
    }

    // ---- Composição, painel e isolamento ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CompositionUsesTheSameListAsTheSummaryPaginatesInTheBackendAndIsIsolatedByWorkspace()
    {
        await ArrangeAsync("comp");
        for (var i = 1; i <= 5; i++) await TransactionAsync(_food, $"{i}0.00", "Fixed", $"Despesa {i}");
        var budget = await BudgetAsync(Competence());
        var target = await AddTargetAsync(budget, new { groupingType = "FixedExpenses", plannedAmount = "1000.00" });

        var summary = (await ExecutionAsync(budget)).Target(target);
        var first = await CompositionAsync(budget, target, 1, 2);
        var last = await CompositionAsync(budget, target, 3, 2);

        Assert.Equal(("150.00", 5, 2, 1), (summary.Text("consumed"), first.GetProperty("totalCount").GetInt32(), first.GetProperty("items").GetArrayLength(), last.GetProperty("items").GetArrayLength()));
        Assert.Equal(summary.Text("consumed"), first.GetProperty("amounts").GetProperty("consumed").GetString()); // mesmo total que o resumo
        var item = first.GetProperty("items")[0];
        Assert.Equal(("Expense", "Fixed", "Alimentação"), ("Expense", item.GetProperty("expenseNature").GetString(), item.GetProperty("categoryPath").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{Base}/budgets/{budget}/targets/{target}/composition", _outsider)).StatusCode);
    }

    [Fact]
    public async Task TheOverviewBudgetBlockShowsFixedCardsAndCategoriesSeparatelyWithoutAGlobalTotal()
    {
        await ArrangeAsync("ov");
        var card = await CardAsync("Cartão");
        await PurchaseAsync(card, _media, "50.00", "Fixed");
        var budget = await BudgetAsync(Competence());
        await AddTargetAsync(budget, new { name = "Mídia", groupingType = "Category", categoryId = _media, plannedAmount = "100.00" });
        await AddTargetAsync(budget, new { groupingType = "FixedExpenses", plannedAmount = "600.00" });
        await AddTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "5000.00" });

        using var overview = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Base}/financial-dashboard/overview?scope=all", _owner)).Content.ReadAsStringAsync());
        var block = overview.RootElement.GetProperty("budget");

        Assert.Equal(("50.00", "600.00"), (block.GetProperty("fixed").GetProperty("consumed").GetString(), block.GetProperty("fixed").GetProperty("planned").GetString()));
        Assert.Equal(("50.00", "5000.00", "AllCards"), (block.GetProperty("cards").GetProperty("consumed").GetString(), block.GetProperty("cards").GetProperty("planned").GetString(), block.GetProperty("cards").GetProperty("group").GetString()));
        Assert.Equal(("1", "50.00"), (block.GetProperty("categories").GetProperty("count").GetInt32().ToString(), block.GetProperty("categories").GetProperty("consumed").GetString()));
        Assert.False(block.TryGetProperty("total", out _)); // nenhum "total do orçamento" somando perspectivas
    }

    [Fact]
    public async Task TheDatabaseAllowsOneAggregateTargetPerBudgetAndRequiresACategoryOnlyForCategoryTargets()
    {
        await ArrangeAsync("db");
        var budget = await BudgetAsync(Competence());
        var category = await AddTargetAsync(budget, new { name = "Alimentação", groupingType = "Category", categoryId = _food, plannedAmount = "10.00" });
        var owner = await UserAsync();
        await using var db = Db();
        db.FinancialBudgetTargets.Add(FinancialBudgetTarget.CreateAggregate(_family.Id, budget, "Todos os cartões", 1m, BudgetTargetGroupingType.AllCards, owner, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        db.FinancialBudgetTargets.Add(FinancialBudgetTarget.CreateAggregate(_family.Id, budget, "Outra", 1m, BudgetTargetGroupingType.AllCards, owner, DateTimeOffset.UtcNow));

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var scope = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_budget_targets SET category_id = NULL, category_type = NULL WHERE id = {category}"));

        Assert.Equal("23505", ((Npgsql.PostgresException)duplicate.InnerException!).SqlState);
        Assert.Equal("23514", scope.SqlState);
    }

    [Fact]
    public async Task TheMigrationRevertsWhenOnlyCategoryTargetsExistAndRefusesToDiscardFixedOrCardTargets()
    {
        const string previous = "20261030153959_AddCategoryHierarchyAndExpenseNature";
        await ArrangeAsync("mig");
        var budget = await BudgetAsync(Competence());
        await AddTargetAsync(budget, new { name = "Alimentação", groupingType = "Category", categoryId = _food, plannedAmount = "10.00" });
        await using var db = Db();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(previous); // só metas de categoria: volta ao esquema anterior sem perder nada
        await migrator.MigrateAsync();
        Assert.Equal(1, await db.FinancialBudgetTargets.CountAsync());

        await AddTargetAsync(budget, new { groupingType = "AllCards", plannedAmount = "5000.00" });
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(previous));
        Assert.Contains("Down recusado", refused.MessageText);
        Assert.Equal(2, await db.FinancialBudgetTargets.CountAsync()); // nada foi descartado
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------------------

    private async Task<Guid> UserAsync()
    {
        await using var db = Db();
        return await db.FinancialWorkspaces.Where(w => w.Id == _family.Id).Select(w => w.OwnerUserId).SingleAsync();
    }

    private async Task<Guid> CategoryAsync(string name, Guid? parent = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/categories", _owner, parent is null ? new { name, type = "Expense" } : (object)new { name, parentCategoryId = parent });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> CardAsync(string name)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/credit-cards", _owner,
            new { name, issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _account });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task PurchaseAsync(Guid card, Guid category, string amount, string? nature = null, int installments = 1, string description = "Compra")
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/credit-cards/{card}/purchases", _owner, new
        {
            description, purchaseDate = FirstDay(), totalAmount = amount, installmentCount = installments, categoryId = category, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = FirstDay(), expenseNature = nature,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task<Guid> TransactionAsync(Guid category, string amount, string? nature, string description = "Despesa")
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/transactions", _owner,
            new { accountId = _account, categoryId = category, type = "Expense", description, amount, transactionDate = Today, expenseNature = nature });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task<Guid> BudgetAsync(string competence)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets", _owner, new { competence });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private Task<HttpResponseMessage> PostTargetAsync(Guid budget, object body) => SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets/{budget}/targets", _owner, body);

    private async Task<Guid> AddTargetAsync(Guid budget, object body)
    {
        var response = await PostTargetAsync(budget, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // A meta nova é a última criada por tipo; localiza pelo corpo enviado (tipo e categoria).
        var sent = JsonSerializer.SerializeToElement(body);
        var grouping = sent.GetProperty("groupingType").GetString();
        var category = sent.TryGetProperty("categoryId", out var c) ? c.GetGuid() : (Guid?)null;
        return json.RootElement.GetProperty("targets").EnumerateArray()
            .Single(t => t.GetProperty("groupingType").GetString() == grouping && !t.GetProperty("isArchived").GetBoolean()
                && (category is null || t.GetProperty("category").GetProperty("id").GetGuid() == category)).GetProperty("id").GetGuid();
    }

    private async Task<Execution> ExecutionAsync(Guid budget)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Base}/budgets/{budget}/execution", _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new Execution(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement);
    }

    private async Task<JsonElement> CompositionAsync(Guid budget, Guid target, int page = 1, int pageSize = 50)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Base}/budgets/{budget}/targets/{target}/composition?page={page}&pageSize={pageSize}", _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private sealed record IdDto(Guid Id);

    private sealed class Execution(JsonElement root)
    {
        public JsonElement Root { get; } = root;
        public ExecutionTarget Target(Guid id) => new(Root.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("targetId").GetGuid() == id));
    }

    private readonly struct ExecutionTarget(JsonElement element)
    {
        public string Text(string name) => element.GetProperty(name).GetString()!;
        public JsonElement GetProperty(string name) => element.GetProperty(name);
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// T10.1: Dashboard Planejado × Projetado × Realizado, com PostgreSQL real e sem Pluggy nem OpenAI. As leituras nunca gravam; o planejado é o que o
/// usuário cadastrou (previsões, orçamento, metas) e a ausência dele é "não definido", nunca zero.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PlanActualDashboardTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month(DateOnly date) => date.ToString("yyyy-MM");

    private sealed record Setup(string Session, WorkspaceDto Workspace, Guid Pf, Guid Pj, Guid Income, Guid Expense)
    {
        public string Root => $"/api/financial-workspaces/{Workspace.Id}";
    }

    private sealed record IdDto(Guid Id);

    private async Task<Setup> ArrangeAsync(string user, bool primary = true, string? pfOpening = "1000.00")
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var root = $"/api/financial-workspaces/{workspace.Id}";
        async Task<Guid> Create(string path, object body) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{root}/{path}", session, body))).Id;
        var openingDate = ThisMonth.AddMonths(-1);
        var pf = await Create("accounts", new { name = "Itaú PF", type = "Checking", financialScope = "PF", isPrimary = primary ? true : (bool?)null, openingBalance = pfOpening, openingBalanceDate = pfOpening is null ? (DateOnly?)null : openingDate });
        var pj = await Create("accounts", new { name = "Itaú PJ", type = "Checking", financialScope = "PJ", openingBalance = "500.00", openingBalanceDate = openingDate });
        var income = await Create("categories", new { name = "Receitas", type = "Income" });
        var expense = await Create("categories", new { name = "Despesas", type = "Expense" });
        return new(session, workspace, pf, pj, income, expense);
    }

    private async Task<JsonElement> DashboardAsync(Setup s, string? scope = null, Guid? account = null, DateOnly? month = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var query = new List<string>();
        if (month is { } m) query.Add($"competence={Month(m)}");
        if (scope is not null) query.Add($"scope={scope}");
        if (account is { } id) query.Add($"accountId={id}");
        var response = await SendAsync(HttpMethod.Get, $"{s.Root}/financial-dashboard/plan-actual" + (query.Count == 0 ? "" : "?" + string.Join('&', query)), s.Session);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return expected == HttpStatusCode.OK ? JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone() : default;
    }

    private async Task<HttpResponseMessage> PostAsync(Setup s, string path, object body) => await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/{path}", s.Session, body);
    private Task TransactionAsync(Setup s, Guid account, string type, string amount, DateOnly date) =>
        PostAsync(s, "transactions", new { accountId = account, categoryId = type == "Income" ? s.Income : s.Expense, type, amount, transactionDate = date, description = "Sintético" });
    private async Task<Guid> PlannedAsync(Setup s, Guid account, string type, string amount, DateOnly due) =>
        (await ReadAsync<IdDto>(await PostAsync(s, "planned-transactions", new { accountId = account, categoryId = type == "Income" ? s.Income : s.Expense, type, amount, dueDate = due, description = "Previsão" }))).Id;

    private static string Text(JsonElement element, params string[] path) { foreach (var part in path) element = element.GetProperty(part); return element.GetString()!; }
    private static bool IsNull(JsonElement element, params string[] path) { foreach (var part in path) element = element.GetProperty(part); return element.ValueKind == JsonValueKind.Null; }

    // ---- 1–3: escopo ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDefaultScopeIsThePrimaryAccountAndItIsIdentifiedInTheResponse()
    {
        var s = await ArrangeAsync("pa-primary");
        await TransactionAsync(s, s.Pf, "Income", "1000.00", Today);
        await TransactionAsync(s, s.Pj, "Income", "300.00", Today);

        var dashboard = await DashboardAsync(s);

        Assert.Equal(("primary", "primary", s.Pf.ToString(), "Itaú PF"), (Text(dashboard, "scope", "requested"), Text(dashboard, "scope", "kind"), Text(dashboard, "scope", "accountId"), Text(dashboard, "scope", "primaryAccountName")));
        Assert.Equal("1000.00", Text(dashboard, "income", "realized"));
        Assert.Equal(ThisMonth.ToString("yyyy-MM"), Text(dashboard, "competence"));
        // As duas contas ficam disponíveis para escolha, a principal primeiro, identificáveis como PF e PJ.
        var options = dashboard.GetProperty("scope").GetProperty("accounts").EnumerateArray().Select(a => (a.GetProperty("name").GetString(), a.GetProperty("scope").GetString(), a.GetProperty("isPrimary").GetBoolean())).ToArray();
        Assert.Equal([("Itaú PF", "PF", true), ("Itaú PJ", "PJ", false)], options);
    }

    [Fact]
    public async Task AllAccountsConsolidateWithoutCountingInternalTransfersAndASpecificAccountIsolatesItsOwnData()
    {
        var s = await ArrangeAsync("pa-all");
        await TransactionAsync(s, s.Pf, "Income", "1000.00", Today);
        await TransactionAsync(s, s.Pf, "Expense", "200.00", Today);
        await TransactionAsync(s, s.Pj, "Income", "300.00", Today);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, "transfers", new { sourceAccountId = s.Pf, destinationAccountId = s.Pj, amount = "400.00", transferDate = Today, description = "PF para PJ" })).StatusCode);

        var all = await DashboardAsync(s, "all");
        var pj = await DashboardAsync(s, "account", s.Pj);
        var pf = await DashboardAsync(s, "primary");

        Assert.Equal(("1300.00", "200.00"), (Text(all, "income", "realized"), Text(all, "expenses", "cashFlow", "realized")));
        Assert.Equal(("300.00", "0.00"), (Text(pj, "income", "realized"), Text(pj, "expenses", "cashFlow", "realized")));
        Assert.Equal(("1000.00", "200.00"), (Text(pf, "income", "realized"), Text(pf, "expenses", "cashFlow", "realized")));
        // A transferência PF→PJ é neutra no consolidado: o saldo realizado do conjunto é a soma das aberturas (1.500) mais as receitas (1.300) menos a despesa (200).
        Assert.Equal("2600.00", Text(all, "situation", "realizedBalance"));
        Assert.Equal(("Itaú PJ", "PJ"), (Text(pj, "scope", "accountName"), Text(pj, "scope", "accountScope")));
    }

    [Fact]
    public async Task WithoutAPrimaryAccountTheDashboardStartsInAllAccountsAndSaysSo()
    {
        var s = await ArrangeAsync("pa-no-primary", primary: false);
        await TransactionAsync(s, s.Pf, "Income", "100.00", Today);
        await TransactionAsync(s, s.Pj, "Income", "50.00", Today);

        var dashboard = await DashboardAsync(s, "primary");

        Assert.Equal(("primary", "all", true), (Text(dashboard, "scope", "requested"), Text(dashboard, "scope", "kind"), dashboard.GetProperty("scope").GetProperty("primaryMissing").GetBoolean()));
        Assert.Equal("150.00", Text(dashboard, "income", "realized"));
    }

    [Fact]
    public async Task ChangingTheMonthKeepsTheScopeAndAnInactiveAccountOnlyAppearsInTheMonthsWhereItHasHistory()
    {
        var s = await ArrangeAsync("pa-inactive");
        var previous = ThisMonth.AddMonths(-1);
        await TransactionAsync(s, s.Pj, "Income", "75.00", previous.AddDays(2));
        await using (var db = Db()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_accounts SET is_active = false WHERE id = {s.Pj}");

        var current = await DashboardAsync(s, "primary");
        var past = await DashboardAsync(s, "primary", month: previous);
        var pastPj = await DashboardAsync(s, "account", s.Pj, previous);

        string[] Names(JsonElement d) => d.GetProperty("scope").GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["Itaú PF"], Names(current));
        Assert.Equal(["Itaú PF", "Itaú PJ"], Names(past));
        Assert.Equal(("primary", Month(previous), "Past"), (Text(past, "scope", "kind"), Text(past, "competence"), Text(past, "periodKind")));
        Assert.Equal("75.00", Text(pastPj, "income", "realized"));
    }

    [Fact]
    public async Task InvalidRequestsAreRejectedAndOtherWorkspacesAreInvisible()
    {
        var s = await ArrangeAsync("pa-isolation");
        var other = await ArrangeAsync("pa-isolation-other");
        var outsider = await SendAsync(HttpMethod.Get, $"{s.Root}/financial-dashboard/plan-actual", other.Session);

        await DashboardAsync(s, "everything", expected: HttpStatusCode.BadRequest);
        await DashboardAsync(s, "account", expected: HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{s.Root}/financial-dashboard/plan-actual?competence=2026-1", s.Session)).StatusCode);
        // A conta de outro orçamento é tratada como inexistente, e o orçamento alheio nem responde.
        await DashboardAsync(s, "account", other.Pf, expected: HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    // ---- 4–8: receitas ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IncomeShowsPlannedProjectedAndReceivedWithoutInventingFutureSalary()
    {
        var s = await ArrangeAsync("pa-income");
        var received = await PlannedAsync(s, s.Pf, "Income", "500.00", Today);
        await PlannedAsync(s, s.Pf, "Income", "2000.00", Today);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, $"planned-transactions/{received}/realize", new { amount = "450.00", transactionDate = Today })).StatusCode);

        var income = (await DashboardAsync(s)).GetProperty("income");

        // Planejado original: as duas previsões ao valor previsto (a realização não o altera). Recebido: só o que entrou. Projetado: recebido + a previsão pendente.
        Assert.Equal(("Defined", "2500.00", "450.00", "2000.00", "2450.00"), (Text(income, "plannedState"), Text(income, "planned"), Text(income, "realized"), Text(income, "pending"), Text(income, "projected")));
        Assert.Equal(("-2050.00", "-50.00", "18.0", "98.0"), (Text(income, "realizedDifference"), Text(income, "projectedDifference"), Text(income, "executionPercent"), Text(income, "projectedPercent")));
    }

    [Fact]
    public async Task IncomeWithoutAnyPlanStillShowsWhatWasReceivedAndPlannedIsNotDefined()
    {
        var s = await ArrangeAsync("pa-income-noplan");
        await TransactionAsync(s, s.Pf, "Income", "321.45", Today);

        var income = (await DashboardAsync(s)).GetProperty("income");

        Assert.Equal(("NotDefined", "321.45", "321.45"), (Text(income, "plannedState"), Text(income, "realized"), Text(income, "projected")));
        Assert.True(IsNull(income, "planned") && IsNull(income, "realizedDifference") && IsNull(income, "executionPercent"));
        Assert.Contains("PlanNotDefined", income.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task CancelledPlansAndPlansOfOtherMonthsOrAccountsDoNotEnterThePlannedOriginal()
    {
        var s = await ArrangeAsync("pa-income-scope");
        var cancelled = await PlannedAsync(s, s.Pf, "Income", "700.00", Today);
        await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/planned-transactions/{cancelled}/cancel", s.Session);
        await PlannedAsync(s, s.Pf, "Income", "100.00", Today);
        await PlannedAsync(s, s.Pj, "Income", "40.00", Today);
        await PlannedAsync(s, s.Pf, "Income", "9.00", ThisMonth.AddMonths(1));

        var primary = (await DashboardAsync(s)).GetProperty("income");
        var all = (await DashboardAsync(s, "all")).GetProperty("income");
        var next = (await DashboardAsync(s, month: ThisMonth.AddMonths(1))).GetProperty("income");

        Assert.Equal("100.00", Text(primary, "planned"));
        Assert.Equal("140.00", Text(all, "planned"));
        Assert.Equal(("9.00", "Future"), (Text(next, "planned"), Text(await DashboardAsync(s, month: ThisMonth.AddMonths(1)), "periodKind")));
    }

    // ---- 9–13: despesas por consumo e por caixa, cartões ----------------------------------------------------------------------

    private async Task<(Guid Card, Guid Invoice, string Competence)> CardWithPurchaseAsync(Setup s, Guid payer, string name, string amount, int installments = 1, DateOnly? purchaseDate = null)
    {
        var card = (await ReadAsync<IdDto>(await PostAsync(s, "credit-cards", new { name, issuer = "Banco", creditLimit = "20000.00", closingDay = 5, dueDay = 15, defaultPaymentAccountId = payer }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, $"credit-cards/{card}/purchases", new { description = "Compra sintética", purchaseDate = purchaseDate ?? Today, totalAmount = amount, installmentCount = installments, categoryId = s.Expense })).StatusCode);
        // A fatura da primeira parcela, direto do banco: a competência é o mês do vencimento (a convenção dos orçamentos e da projeção de cartões).
        await using var db = Db();
        var invoice = await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.CreditCardId == card).OrderBy(i => i.DueDate).FirstAsync();
        return (card, invoice.Id, invoice.Competence);
    }

    private static DateOnly FirstDay(string competence) => DateOnly.ParseExact(competence + "-01", "yyyy-MM-dd");

    [Fact]
    public async Task CardPurchasesCountAsConsumptionInTheMonthOfTheirInvoiceAndNeverInTheCashFlowUntilThePaymentHappens()
    {
        var s = await ArrangeAsync("pa-cards");
        var (_, _, competence) = await CardWithPurchaseAsync(s, s.Pf, "Cartão PF", "600.00");

        var dashboard = await DashboardAsync(s, month: FirstDay(competence));
        var consumption = dashboard.GetProperty("expenses").GetProperty("consumption");
        var cash = dashboard.GetProperty("expenses").GetProperty("cashFlow");

        // Consumo: a compra aparece como confirmada na competência da fatura. Caixa: ela só aparece como obrigação pendente (a fatura ainda não foi paga).
        Assert.Equal(("600.00", "600.00"), (Text(dashboard, "cards", "confirmed"), Text(consumption, "realized")));
        Assert.Equal("0.00", Text(cash, "realized"));
        Assert.Equal("600.00", cash.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "PendingCardInvoices").GetProperty("amount").GetString());
        Assert.Equal("Consumption", Text(dashboard.GetProperty("expenses"), "primaryPerspective"));
    }

    [Fact]
    public async Task PayingTheInvoiceMovesCashButDoesNotDuplicateTheCardConsumption()
    {
        var s = await ArrangeAsync("pa-cards-pay");
        var (card, invoice, competence) = await CardWithPurchaseAsync(s, s.Pf, "Cartão PF", "600.00", purchaseDate: ThisMonth.AddMonths(-3).AddDays(9));
        var month = FirstDay(competence);
        await using (var db = Db()) await OfficialInvoiceFixture.CloseAsync(db, invoice);
        var before = await DashboardAsync(s, month: month);

        var payment = await PostAsync(s, $"credit-cards/{card}/invoices/{invoice}/payments", new { operationId = Guid.NewGuid(), financialAccountId = s.Pf, paymentDate = Today, amount = "600.00" });
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);
        var paymentMonth = await DashboardAsync(s, month: ThisMonth);
        var after = await DashboardAsync(s, month: month);

        // O caixa do mês do pagamento registra a saída; o consumo do cartão segue sendo a compra, uma única vez.
        Assert.Equal("600.00", Text(paymentMonth, "expenses", "cashFlow", "realized"));
        Assert.Equal("600.00", paymentMonth.GetProperty("expenses").GetProperty("cashFlow").GetProperty("components").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "CardInvoicePayments").GetProperty("amount").GetString());
        Assert.Equal("0.00", paymentMonth.GetProperty("expenses").GetProperty("consumption").GetProperty("components").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "BankExpenses").GetProperty("amount").GetString());
        Assert.Equal(Text(before, "cards", "confirmed"), Text(after, "cards", "confirmed"));
        Assert.Equal("600.00", Text(after, "cards", "confirmed"));
    }

    [Fact]
    public async Task APaymentFromAnotherAccountRespectsTheEffectiveDebitAccountWhileThePurchaseStaysWithThePayerOfTheCard()
    {
        var s = await ArrangeAsync("pa-cards-debit");
        var (card, invoice, competence) = await CardWithPurchaseAsync(s, s.Pf, "Cartão PF", "300.00", purchaseDate: ThisMonth.AddMonths(-3).AddDays(9));
        await using (var db = Db()) await OfficialInvoiceFixture.CloseAsync(db, invoice);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, $"credit-cards/{card}/invoices/{invoice}/payments", new { operationId = Guid.NewGuid(), financialAccountId = s.Pj, paymentDate = Today, amount = "300.00" })).StatusCode);

        var pj = await DashboardAsync(s, "account", s.Pj);
        var pf = await DashboardAsync(s, "primary");
        var pfCards = await DashboardAsync(s, "primary", month: FirstDay(competence));
        var pjCards = await DashboardAsync(s, "account", s.Pj, FirstDay(competence));

        Assert.Equal(("300.00", "0.00"), (Text(pj, "expenses", "cashFlow", "realized"), Text(pf, "expenses", "cashFlow", "realized")));
        Assert.Equal(("300.00", "0.00", false), (Text(pfCards, "cards", "confirmed"), Text(pjCards, "cards", "confirmed"), pjCards.GetProperty("cards").GetProperty("applicable").GetBoolean()));
    }

    [Fact]
    public async Task CardsAreGroupedByTheirPayerAccountNotByTheirOwnerAndAllAccountsSumsEveryCard()
    {
        var s = await ArrangeAsync("pa-cards-payers");
        var (_, _, competence) = await CardWithPurchaseAsync(s, s.Pf, "Cartão pago pela PF", "100.00");
        await CardWithPurchaseAsync(s, s.Pf, "Cartão PJ pago pela PF", "250.00");
        await CardWithPurchaseAsync(s, s.Pj, "Cartão da PJ", "40.00");
        var month = FirstDay(competence);

        var pf = await DashboardAsync(s, "primary", month: month);
        var pj = await DashboardAsync(s, "account", s.Pj, month);
        var all = await DashboardAsync(s, "all", month: month);

        Assert.Equal(("350.00", 2), (Text(pf, "cards", "confirmed"), pf.GetProperty("cards").GetProperty("cards").GetArrayLength()));
        Assert.Equal(("40.00", 1), (Text(pj, "cards", "confirmed"), pj.GetProperty("cards").GetProperty("cards").GetArrayLength()));
        Assert.Equal(("390.00", 3), (Text(all, "cards", "confirmed"), all.GetProperty("cards").GetProperty("cards").GetArrayLength()));
    }

    // ---- 14–17: meta "Todos os cartões" do orçamento (R03): única fonte do limite de cartões ----------------------------------------

    private string BudgetsUrl(Setup s) => $"{s.Root}/budgets";

    /// <summary>Define (cria ou revisa) a meta "Todos os cartões" do orçamento da competência; cria o orçamento do mês se faltar.</summary>
    private async Task<HttpResponseMessage> CardLimitAsync(Setup s, string competence, string amount)
    {
        var existing = await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, $"{BudgetsUrl(s)}?competence={competence}", s.Session));
        string budgetId;
        if (existing.GetArrayLength() > 0) budgetId = existing[0].GetProperty("id").GetString()!;
        else
        {
            var created = await SendWithCsrfAsync(HttpMethod.Post, BudgetsUrl(s), s.Session, new { competence });
            if (created.StatusCode != HttpStatusCode.Created) return created;
            budgetId = (await ReadAsync<JsonElement>(created)).GetProperty("id").GetString()!;
        }
        var target = existing.GetArrayLength() > 0
            ? existing[0].GetProperty("targets").EnumerateArray().FirstOrDefault(t => t.GetProperty("groupingType").GetString() == "AllCards" && !t.GetProperty("isArchived").GetBoolean())
            : default;
        return target.ValueKind == JsonValueKind.Object
            ? await SendWithCsrfAsync(HttpMethod.Put, $"{BudgetsUrl(s)}/{budgetId}/targets/{target.GetProperty("id").GetString()}", s.Session, new { plannedAmount = amount })
            : await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl(s)}/{budgetId}/targets", s.Session, new { groupingType = "AllCards", plannedAmount = amount });
    }

    [Fact]
    public async Task TheConsolidatedCardLimitIsComparedWithTheConsumptionOfAllCardsAndNeverGrowsWithTheSpending()
    {
        var s = await ArrangeAsync("pa-target");
        var (_, _, competence) = await CardWithPurchaseAsync(s, s.Pf, "Cartão PF", "4300.00");
        var month = FirstDay(competence);

        Assert.True((await CardLimitAsync(s, competence, "5000.00")).IsSuccessStatusCode);
        var within = (await DashboardAsync(s, "all", month: month)).GetProperty("cards");
        await CardWithPurchaseAsync(s, s.Pf, "Outro cartão PF", "1400.00");
        var over = (await DashboardAsync(s, "all", month: month)).GetProperty("cards");

        Assert.Equal(("Defined", "5000.00", "BudgetTarget", "4300.00", "700.00", "86.0"), (Text(within, "plannedState"), Text(within, "target"), Text(within, "targetSource"), Text(within, "confirmed"), Text(within, "remaining"), Text(within, "confirmedPercent")));
        // 5.700 de consumo contra o limite de 5.000: excesso de 700 e o limite continua 5.000.
        Assert.Equal(("5000.00", "5700.00", "0.00", "700.00", "700.00", "114.0"), (Text(over, "target"), Text(over, "projected"), Text(over, "remaining"), Text(over, "excessProjected"), Text(over, "excessConfirmed"), Text(over, "projectedPercent")));
    }

    [Fact]
    public async Task TheCardLimitBelongsToItsMonthlyBudgetAndRevisingItReplacesTheSingleTarget()
    {
        var s = await ArrangeAsync("pa-target-history");
        var thisMonth = Month(ThisMonth); var next = Month(ThisMonth.AddMonths(1));
        await CardLimitAsync(s, thisMonth, "5000.00");
        await CardLimitAsync(s, next, "4000.00");

        async Task<JsonElement> CardsAt(DateOnly month) => (await DashboardAsync(s, "all", month: month)).GetProperty("cards");
        var current = await CardsAt(ThisMonth); var following = await CardsAt(ThisMonth.AddMonths(1)); var later = await CardsAt(ThisMonth.AddMonths(2)); var before = await CardsAt(ThisMonth.AddMonths(-1));

        // Cada mês tem o seu orçamento: sem meta no mês, "não definido" (nada é herdado em silêncio).
        Assert.Equal(("5000.00", "4000.00", "NotDefined", "NotDefined"), (Text(current, "target"), Text(following, "target"), Text(later, "plannedState"), Text(before, "plannedState")));
        // Revisar o limite do mesmo mês atualiza a meta única, sem duplicá-la.
        await CardLimitAsync(s, thisMonth, "5200.00");
        await using var db = Db();
        Assert.Equal(1, await db.FinancialBudgetTargets.CountAsync(t => t.WorkspaceId == s.Workspace.Id && t.GroupingType == Saldo360.Domain.Budgets.BudgetTargetGroupingType.AllCards && !t.IsArchived
            && db.FinancialBudgets.Any(b => b.Id == t.BudgetId && b.Competence == ThisMonth)));
        Assert.Equal("5200.00", Text(await CardsAt(ThisMonth), "target"));
    }

    [Fact]
    public async Task TheCardLimitValidatesTheAmountTheTargetShapeAndTheWorkspace()
    {
        var s = await ArrangeAsync("pa-target-invalid");
        var other = await ArrangeAsync("pa-target-invalid-other");
        var thisMonth = Month(ThisMonth);
        Assert.True((await CardLimitAsync(s, thisMonth, "100.00")).IsSuccessStatusCode);
        var budgetId = (await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, $"{BudgetsUrl(s)}?competence={thisMonth}", s.Session)))[0].GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl(s)}/{budgetId}/targets", s.Session, new { groupingType = "AllCards", plannedAmount = "-5.00" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl(s)}/{budgetId}/targets", s.Session, new { groupingType = "AllCards", plannedAmount = "5.00" })).StatusCode); // só uma por orçamento
        Assert.Equal(HttpStatusCode.BadRequest, (await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl(s)}/{budgetId}/targets", s.Session, new { groupingType = "AllCards", plannedAmount = "5.00", categoryId = Guid.NewGuid() })).StatusCode); // sem categoria nem cartão individual
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"{BudgetsUrl(other)}/{budgetId}/targets", other.Session, new { groupingType = "AllCards", plannedAmount = "5.00" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, $"{s.Root}/financial-dashboard/card-target", s.Session, new { amount = "1.00" })).StatusCode); // a meta por conta pagadora deixou de existir
    }

    [Fact]
    public async Task TheConsolidatedLimitSumsAllPayersAndDoesNotApplyToASingleAccountScope()
    {
        var s = await ArrangeAsync("pa-target-all");
        var (_, _, competence) = await CardWithPurchaseAsync(s, s.Pf, "Cartão PF", "100.00");
        await CardWithPurchaseAsync(s, s.Pj, "Cartão PJ", "50.00");
        var month = FirstDay(competence);
        await CardLimitAsync(s, competence, "5000.00");

        var all = (await DashboardAsync(s, "all", month: month)).GetProperty("cards");
        var pf = (await DashboardAsync(s, "account", s.Pf, month: month)).GetProperty("cards");

        // Um limite só para todos os cartões (PF + PJ): 150 consumidos de 5.000. Numa conta específica não existe teto individual.
        Assert.Equal(("5000.00", "150.00", false), (Text(all, "target"), Text(all, "confirmed"), all.GetProperty("targetPartial").GetBoolean()));
        Assert.Equal("NotDefined", Text(pf, "plannedState"));
    }

    // ---- 18–19: orçamento e consumo planejado ----------------------------------------------------------------------------------

    [Fact]
    public async Task TheCategoryBudgetIsTheConsumptionPlanOfAllAccountsAndIsNotAttributableToASingleAccount()
    {
        var s = await ArrangeAsync("pa-budget");
        var budget = (await ReadAsync<IdDto>(await PostAsync(s, "budgets", new { competence = Month(ThisMonth) }))).Id;
        await PostAsync(s, $"budgets/{budget}/targets", new { name = "Despesas", plannedAmount = "1000.00", groupingType = "Category", categoryId = s.Expense });
        await TransactionAsync(s, s.Pf, "Expense", "250.00", Today);

        var all = (await DashboardAsync(s, "all")).GetProperty("expenses").GetProperty("consumption");
        var primary = (await DashboardAsync(s, "primary")).GetProperty("expenses").GetProperty("consumption");

        Assert.Equal(("Defined", "1000.00", "Budget", "250.00", "-750.00", "25.0"), (Text(all, "plannedState"), Text(all, "planned"), Text(all, "plannedSource"), Text(all, "realized"), Text(all, "realizedDifference"), Text(all, "executionPercent")));
        Assert.Equal("NotDefined", Text(primary, "plannedState"));
        Assert.Contains("PlanNotAttributable", primary.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("250.00", Text(primary, "realized"));
    }

    [Fact]
    public async Task PlannedExpensesAreComparedInCashFlowWithTheSameBasisOnBothSides()
    {
        var s = await ArrangeAsync("pa-cash");
        await PlannedAsync(s, s.Pf, "Expense", "300.00", Today);
        var done = await PlannedAsync(s, s.Pf, "Expense", "100.00", Today);
        await PostAsync(s, $"planned-transactions/{done}/realize", new { amount = "120.00", transactionDate = Today });

        var cash = (await DashboardAsync(s)).GetProperty("expenses").GetProperty("cashFlow");

        // Planejado em caixa = as duas previsões (400); realizado = o que saiu (120); projetado = realizado + a previsão pendente (420).
        Assert.Equal(("400.00", "120.00", "300.00", "420.00", "20.00"), (Text(cash, "planned"), Text(cash, "realized"), Text(cash, "pending"), Text(cash, "projected"), Text(cash, "projectedDifference")));
    }

    // ---- 20–22: situação financeira do mês ------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMonthSituationShowsTheRealizedBalanceTheProjectedClosingAndTheDateOfTheNegativeRisk()
    {
        var s = await ArrangeAsync("pa-situation", pfOpening: "100.00");
        await PlannedAsync(s, s.Pf, "Expense", "500.00", Today);

        var situation = (await DashboardAsync(s)).GetProperty("situation");

        Assert.Equal(("100.00", "-400.00", "500.00", Today.ToString("yyyy-MM-dd")), (Text(situation, "realizedBalance"), Text(situation, "projectedClosing"), Text(situation, "pendingExpense"), Text(situation, "negativeRiskDate")));
        Assert.Equal("-400.00", Text(situation, "minimumProjected"));
    }

    [Fact]
    public async Task AnAccountWithoutAnOpeningBalanceIsUnavailableNotZeroAndOtherIndicatorsRemain()
    {
        var s = await ArrangeAsync("pa-nobalance", pfOpening: null);
        await TransactionAsync(s, s.Pf, "Income", "80.00", Today);

        var dashboard = await DashboardAsync(s);
        var all = await DashboardAsync(s, "all");

        var situation = dashboard.GetProperty("situation");
        Assert.True(IsNull(situation, "realizedBalance") && IsNull(situation, "projectedClosing") && IsNull(situation, "partialRealizedBalance"));
        Assert.Equal((1, "Partial"), (situation.GetProperty("unavailableAccounts").GetInt32(), Text(situation, "state")));
        Assert.Equal("80.00", Text(dashboard, "income", "realized"));
        // No consolidado, o saldo de uma só conta nunca é apresentado como o total: ele fica indisponível e o parcial vem à parte.
        Assert.True(IsNull(all, "situation", "realizedBalance"));
        Assert.Equal("500.00", Text(all, "situation", "partialRealizedBalance"));
    }

    // ---- 23: leitura sem efeito colateral e precisão -------------------------------------------------------------------------------

    [Fact]
    public async Task ReadingTheDashboardChangesNothingAndKeepsCentsExact()
    {
        var s = await ArrangeAsync("pa-readonly");
        await TransactionAsync(s, s.Pf, "Income", "0.10", Today);
        await TransactionAsync(s, s.Pf, "Income", "0.20", Today);
        await PlannedAsync(s, s.Pf, "Expense", "0.30", Today);
        async Task<string> Snapshot()
        {
            await using var db = Db();
            return $"{await db.FinancialTransactions.CountAsync()}|{await db.FinancialPlannedTransactions.CountAsync()}|{await db.FinancialTransfers.CountAsync()}|{await db.FinancialBudgetTargets.CountAsync()}|{await db.FinancialAccounts.SumAsync(a => a.OpeningBalance)}";
        }
        var before = await Snapshot();

        var first = await DashboardAsync(s);
        var second = await DashboardAsync(s);

        Assert.Equal(before, await Snapshot());
        Assert.Equal(("0.30", "0.30", "0.00"), (Text(first, "income", "realized"), Text(first, "expenses", "cashFlow", "pending"), Text(first, "expenses", "cashFlow", "realized")));
        Assert.True(first.GetProperty("snapshotConsistent").GetBoolean());
        Assert.Equal(first.GetProperty("income").GetRawText(), second.GetProperty("income").GetRawText());
    }
}

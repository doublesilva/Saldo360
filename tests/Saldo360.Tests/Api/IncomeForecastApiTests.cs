using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// PROJ-01: previsão de receita PJ por horas, disponibilidade, repasses PJ → PF e recuperação do limite na projeção integrada de seis meses, com PostgreSQL real.
/// A estimativa só projeta depois de aprovada, nunca cria lançamento, é substituída pelo realizado e as leituras não gravam nada.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class IncomeForecastApiTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month(DateOnly date) => date.ToString("yyyy-MM");
    private static decimal Num(JsonElement element, params string[] path) { foreach (var part in path) element = element.GetProperty(part); return decimal.Parse(element.GetString()!, CultureInfo.InvariantCulture); }
    private static string Text(JsonElement element, params string[] path) { foreach (var part in path) element = element.GetProperty(part); return element.GetString()!; }

    private sealed record Setup(string Session, WorkspaceDto Workspace, Guid Pf, Guid Pj, Guid Income, Guid Expense)
    {
        public string Root => $"/api/financial-workspaces/{Workspace.Id}";
        public string Plan => $"{Root}/monthly-plan";
    }

    private sealed record IdDto(Guid Id);

    private async Task<Setup> ArrangeAsync(string user = "proj01", string pfOpening = "-5000.00")
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var root = $"/api/financial-workspaces/{workspace.Id}";
        async Task<Guid> Create(string path, object body) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{root}/{path}", session, body))).Id;
        var opening = ThisMonth.AddMonths(-1);
        var pf = await Create("accounts", new { name = "Itaú PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = pfOpening, openingBalanceDate = opening, overdraftLimit = "16100.00" });
        var pj = await Create("accounts", new { name = "Itaú PJ", type = "Checking", financialScope = "PJ", openingBalance = "500.00", openingBalanceDate = opening });
        var income = await Create("categories", new { name = "Receitas", type = "Income" });
        var expense = await Create("categories", new { name = "Despesas", type = "Expense" });
        return new(session, workspace, pf, pj, income, expense);
    }

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {body}");
        return string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private Task<JsonElement> SixMonthsAsync(Setup s, string? scope = null) =>
        JsonAsync(SendAsync(HttpMethod.Get, $"{s.Plan}/six-months" + (scope is null ? "" : $"?scope={scope}"), s.Session).GetAwaiter().GetResult());

    private Task<HttpResponseMessage> SaveForecastAsync(Setup s, bool active = true, string tax = "11.00", string rate = "100.00", string hours = "8", int day = 10, int offset = 1, string reserve = "0.00", Guid? account = null) =>
        SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/income-forecast", s.Session, new { accountId = account ?? s.Pj, hourlyRate = rate, hoursPerDay = hours, paymentDay = day, paymentOffsetMonths = offset, taxReservePercent = tax, operationalReserve = reserve, isActive = active });

    private Task<HttpResponseMessage> PostAsync(Setup s, string path, object body) => SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/{path}", s.Session, body);

    private async Task TransactionAsync(Setup s, Guid account, string type, string amount, DateOnly date, string description = "Sintético") =>
        await JsonAsync(await PostAsync(s, "transactions", new { accountId = account, categoryId = type == "Income" ? s.Income : s.Expense, type, amount, transactionDate = date, description }), HttpStatusCode.Created);

    private async Task PlannedAsync(Setup s, Guid account, string type, string amount, DateOnly due, string description = "Previsão") =>
        await JsonAsync(await PostAsync(s, "planned-transactions", new { accountId = account, categoryId = type == "Income" ? s.Income : s.Expense, type, amount, dueDate = due, description }), HttpStatusCode.Created);

    private async Task<Guid> PlannedTransferAsync(Setup s, decimal amount, DateOnly due) =>
        (await ReadAsync<IdDto>(await PostAsync(s, "planned-transfers", new { sourceAccountId = s.Pj, destinationAccountId = s.Pf, amount, dueDate = due, description = "Repasse PJ → PF" }))).Id;

    private static JsonElement Integrated(JsonElement plan, int index) => plan.GetProperty("integrated").GetProperty("months")[index];

    private async Task<(int Transactions, int Planned, int Transfers, int PlannedTransfers, int Snapshots)> CountsAsync()
    {
        await using var db = Db();
        return (await db.FinancialTransactions.CountAsync(), await db.FinancialPlannedTransactions.CountAsync(), await db.FinancialTransfers.CountAsync(),
            await db.FinancialPlannedTransfers.CountAsync(), await db.OverdraftRecoverySnapshots.CountAsync());
    }

    // ---- Aprovação e leitura sem gravação ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Forecast_NeverProjects_UntilApproved_AndNothingIsCreatedOrWrittenByReading()
    {
        var s = await ArrangeAsync();
        var draft = await JsonAsync(await SendAsync(HttpMethod.Get, $"{s.Plan}/income-forecast", s.Session));
        Assert.False(draft.GetProperty("exists").GetBoolean());
        Assert.Equal(("100.00", 10, 1, "11.00"), (Text(draft, "hourlyRate"), draft.GetProperty("paymentDay").GetInt32(), draft.GetProperty("paymentOffsetMonths").GetInt32(), Text(draft, "taxReservePercent")));
        Assert.Equal(s.Pj, draft.GetProperty("accountId").GetGuid()); // Itaú PJ sugerida
        var before = await CountsAsync();

        var none = await SixMonthsAsync(s);
        Assert.Equal("NotConfigured", Text(Integrated(none, 0), "income", "source"));
        Assert.Equal(0m, Num(Integrated(none, 0), "pj", "pendingIncome"));
        Assert.Contains("IncomeForecastNotConfigured", Integrated(none, 0).GetProperty("quality").EnumerateArray().Select(q => q.GetString()));

        await JsonAsync(await SaveForecastAsync(s, active: false)); // configurada, mas NÃO aprovada
        var unapproved = await SixMonthsAsync(s);
        Assert.False(unapproved.GetProperty("integrated").GetProperty("incomeForecastActive").GetBoolean());
        Assert.Equal(0m, Num(Integrated(unapproved, 0), "pj", "pendingIncome"));

        await JsonAsync(await SaveForecastAsync(s, active: true));
        var approved = await SixMonthsAsync(s);
        Assert.Equal("Forecast", Text(Integrated(approved, 0), "income", "source"));
        Assert.True(Num(Integrated(approved, 0), "pj", "pendingIncome") > 0m);

        var after = await CountsAsync();
        Assert.Equal(before, after); // nenhum lançamento, previsão, transferência ou snapshot foi criado
    }

    [Fact]
    public async Task Forecast_UsesTheBillableDaysOfTheWorkedMonth_AndPaymentFallsInTheNextMonth()
    {
        var s = await ArrangeAsync();
        await JsonAsync(await SaveForecastAsync(s));
        var plan = await SixMonthsAsync(s);

        var first = Integrated(plan, 0).GetProperty("income");
        var worked = ThisMonth.AddMonths(-1);
        Assert.Equal(Month(worked), Text(first, "workedCompetence"));
        Assert.Equal(new DateOnly(Today.Year, Today.Month, 10).ToString("yyyy-MM-dd"), Text(first, "paymentDate"));
        var days = first.GetProperty("billableDays").GetInt32();
        Assert.Equal(days, first.GetProperty("businessDays").GetInt32());
        Assert.Equal(days * 8m * 100m, Num(first, "expectedGross"));
        // Cada mês tem os seus dias úteis: o valor muda conforme a competência trabalhada.
        var gross = Enumerable.Range(0, 6).Select(i => Num(Integrated(plan, i), "income", "expectedGross")).Distinct().Count();
        Assert.True(gross >= 2);
    }

    [Fact]
    public async Task ManualAdjustments_ChangeDaysAmountAndDate_AndCanBeRemoved()
    {
        var s = await ArrangeAsync();
        await JsonAsync(await SaveForecastAsync(s));
        var worked = Month(ThisMonth.AddMonths(-1));

        var days = await JsonAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/income-forecast/months/{worked}", s.Session, new { billableDays = 10, isDismissed = false }));
        var month = days.GetProperty("months").EnumerateArray().Single(m => Text(m, "workedCompetence") == worked);
        Assert.Equal(("8000.00", 10, true), (Text(month, "grossAmount"), month.GetProperty("billableDays").GetInt32(), month.GetProperty("daysAdjusted").GetBoolean()));
        Assert.Equal(8000m, Num(Integrated(await SixMonthsAsync(s), 0), "income", "expectedGross"));

        var moved = new DateOnly(Today.Year, Today.Month, 20);
        await JsonAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/income-forecast/months/{worked}", s.Session, new { expectedAmount = "9999.90", paymentDate = moved, isDismissed = false }));
        var adjusted = Integrated(await SixMonthsAsync(s), 0).GetProperty("income");
        Assert.Equal(("9999.90", moved.ToString("yyyy-MM-dd")), (Text(adjusted, "expectedGross"), Text(adjusted, "paymentDate")));

        var restored = await JsonAsync(await SendWithCsrfAsync(HttpMethod.Delete, $"{s.Plan}/income-forecast/months/{worked}", s.Session));
        Assert.False(restored.GetProperty("months").EnumerateArray().Single(m => Text(m, "workedCompetence") == worked).GetProperty("amountAdjusted").GetBoolean());
        await JsonAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/income-forecast/months/{Month(ThisMonth.AddMonths(-1))}", s.Session, new { isDismissed = true }));
        Assert.Equal("Dismissed", Text(Integrated(await SixMonthsAsync(s), 0), "income", "source"));
    }

    [Fact]
    public async Task LinkedIncomeOnTheAccount_SettlesOnlyThatForecast_NeverBoth()
    {
        var s = await ArrangeAsync();
        await JsonAsync(await SaveForecastAsync(s, day: Today.Day));
        var before = Integrated(await SixMonthsAsync(s), 0);
        var expected = Num(before, "income", "expectedGross");
        Assert.Equal(expected, Num(before, "pj", "pendingIncome"));

        await TransactionAsync(s, s.Pj, "Income", "100.00", ThisMonth.AddDays(-5)); // histórico do pagador no mês anterior (evidência)
        await TransactionAsync(s, s.Pj, "Income", expected.ToString("0.00", CultureInfo.InvariantCulture), Today); // o recebimento aconteceu

        var after = Integrated(await SixMonthsAsync(s), 0);
        Assert.Equal("Realized", Text(after, "income", "source"));
        Assert.Equal(0m, Num(after, "pj", "pendingIncome")); // a estimativa deixou de projetar
        Assert.Equal(expected, Num(after, "income", "realizedIncome"));
        // O saldo projetado não duplica: abertura + realizado, sem a entrada estimada de novo.
        Assert.Equal(Num(after, "pj", "opening") + expected, Num(after, "pj", "closing") + Num(after, "pj", "pendingExpense") + Num(after, "transfers", "autoProjectedPjToPf"));
    }


    [Fact]
    public async Task TwoCompetencesPaidInTheSameMonth_NeverConsumeTheSameReceiptTwice_AndAreFlaggedAmbiguous()
    {
        var s = await ArrangeAsync();
        await JsonAsync(await SaveForecastAsync(s, day: Today.Day));
        await JsonAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/income-forecast/months/{Month(ThisMonth.AddMonths(-2))}", s.Session, new { paymentDate = Today, isDismissed = false }));
        var pendingBefore = Num(Integrated(await SixMonthsAsync(s), 0), "pj", "pendingIncome");
        await TransactionAsync(s, s.Pj, "Income", "100.00", ThisMonth.AddDays(-5));
        await TransactionAsync(s, s.Pj, "Income", "5000.00", Today); // um único depósito de pagador conhecido, duas competências com o mesmo mês de pagamento

        var month = Integrated(await SixMonthsAsync(s), 0);

        Assert.Equal(pendingBefore - 5000m, Num(month, "pj", "pendingIncome")); // consumido uma só vez
        Assert.Contains("IncomeReceiptAmbiguous", month.GetProperty("quality").EnumerateArray().Select(q => q.GetString()));
        Assert.False(month.GetProperty("complete").GetBoolean());
    }
    [Fact]
    public async Task APartialLinkedReceipt_LeavesTheResidualProjected_AndAnUnrelatedDepositSettlesNothing()
    {
        var s = await ArrangeAsync();
        await JsonAsync(await SaveForecastAsync(s, day: Today.Day));
        var expected = Num(Integrated(await SixMonthsAsync(s), 0), "income", "expectedGross");
        await TransactionAsync(s, s.Pj, "Income", "100.00", ThisMonth.AddDays(-5));
        var partial = Math.Round(expected * 0.4m, 2);
        await TransactionAsync(s, s.Pj, "Income", partial.ToString("0.00", CultureInfo.InvariantCulture), Today);
        await TransactionAsync(s, s.Pj, "Income", "50000.00", Today, "DEPOSITO DESCONHECIDO"); // sem pagador conhecido: não liquida nada

        var month = Integrated(await SixMonthsAsync(s), 0);

        Assert.Equal("PartiallyReceived", Text(month, "income", "source"));
        Assert.Equal(expected - partial, Num(month, "pj", "pendingIncome")); // só o residual segue a receber
        Assert.Contains("IncomeReceiptUnidentified", month.GetProperty("quality").EnumerateArray().Select(q => q.GetString()));
        Assert.False(month.GetProperty("complete").GetBoolean());
    }

    // ---- Tributos, despesas PJ e disponibilidade --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TaxReserve_ReducesAvailability_ButIsNeverDebitedFromTheBankBalance()
    {
        var s = await ArrangeAsync();
        await JsonAsync(await SaveForecastAsync(s, tax: "0.00"));
        var noTax = Integrated(await SixMonthsAsync(s), 0);
        await JsonAsync(await SaveForecastAsync(s, tax: "11.00", reserve: "1000.00"));
        var withTax = Integrated(await SixMonthsAsync(s), 0);

        var expectedTax = Math.Round(Num(withTax, "income", "expectedGross") * 0.11m, 2);
        Assert.Equal(expectedTax, Num(withTax, "pj", "taxReserve"));
        // Reserva não é débito: o saldo bancário ANTES do repasse projetado é igual; o repasse automático só deixa de levar o valor reservado (PROJ-06).
        Assert.Equal(Num(noTax, "pj", "closing") + Num(noTax, "transfers", "autoProjectedPjToPf"), Num(withTax, "pj", "closing") + Num(withTax, "transfers", "autoProjectedPjToPf"));
        Assert.True(Num(withTax, "pj", "closing") >= expectedTax + 1000m); // o repasse nunca consome tributos nem reserva operacional
        Assert.Equal(Num(withTax, "pj", "closing") - expectedTax - 1000m, Num(withTax, "pj", "available"));
        Assert.Equal(Math.Max(0m, Math.Floor(Num(withTax, "pj", "available") * 100m) / 100m), Num(withTax, "pj", "suggestedTransfer"));
    }

    [Fact]
    public async Task ExistingPjExpenses_AreReused_AndAPendingTaxObligationSuppressesTheReserve()
    {
        var s = await ArrangeAsync();
        await JsonAsync(await SaveForecastAsync(s));
        var due = new DateOnly(Today.Year, Today.Month, 28);
        if (due < Today) due = Today;
        await PlannedAsync(s, s.Pj, "Expense", "1900.00", due, "PRONAMPE");
        var plain = Integrated(await SixMonthsAsync(s), 0);
        Assert.Equal(1900m, Num(plain, "pj", "pendingExpense")); // a despesa já cadastrada é reaproveitada, sem duplicar
        Assert.False(plain.GetProperty("pj").GetProperty("taxObligationRegistered").GetBoolean());
        Assert.True(Num(plain, "pj", "taxReserve") > 0m);

        await PlannedAsync(s, s.Pj, "Expense", "1500.00", due, "DAS Simples Nacional");
        var withTax = Integrated(await SixMonthsAsync(s), 0);
        Assert.True(withTax.GetProperty("pj").GetProperty("taxObligationRegistered").GetBoolean());
        Assert.Equal(0m, Num(withTax, "pj", "taxReserve")); // obrigação cadastrada: sem dupla consideração
        Assert.Equal(3400m, Num(withTax, "pj", "pendingExpense"));
    }

    // ---- Transferências PJ → PF ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlannedTransfer_ReplacesPartOfTheAutomaticTransfer_AndLeavesTheConsolidatedUntouched()
    {
        var s = await ArrangeAsync();
        await JsonAsync(await SaveForecastAsync(s));
        var baseline = await SixMonthsAsync(s);
        var consolidatedBefore = await SixMonthsAsync(s, "all");
        var due = new DateOnly(Today.Year, Today.Month, 28);
        if (due < Today) due = Today.AddDays(1);

        await PlannedTransferAsync(s, 3000m, due);

        var plan = await SixMonthsAsync(s);
        Assert.Equal("3000.00", Text(Integrated(plan, 0), "transfers", "plannedPjToPf"));
        // PROJ-06: o repasse previsto SUBSTITUI parte do repasse automático (a mesma disponibilidade nunca vira duas transferências): o total para a PF e os saldos não mudam.
        Assert.Equal(Num(Integrated(baseline, 0), "transfers", "autoProjectedPjToPf") - 3000m, Num(Integrated(plan, 0), "transfers", "autoProjectedPjToPf"));
        Assert.Equal(Num(Integrated(baseline, 0), "pj", "closing"), Num(Integrated(plan, 0), "pj", "closing"));
        Assert.Equal(Num(Integrated(baseline, 0), "pf", "closing"), Num(Integrated(plan, 0), "pf", "closing"));
        Assert.Equal(Num(Integrated(baseline, 0), "pf", "transfersIn"), Num(Integrated(plan, 0), "pf", "transfersIn"));
        // Repasse não é receita nem despesa do PF: entradas e saídas ficam como antes.
        Assert.Equal((Num(Integrated(baseline, 0), "pf", "entries"), Num(Integrated(baseline, 0), "pf", "exits")), (Num(Integrated(plan, 0), "pf", "entries"), Num(Integrated(plan, 0), "pf", "exits")));
        // No consolidado familiar o repasse é neutro: mesmo saldo, mesmas receitas e despesas.
        var consolidated = await SixMonthsAsync(s, "all");
        for (var i = 0; i < 6; i++)
            Assert.Equal(
                (Text(consolidatedBefore.GetProperty("months")[i], "closingBalance"), Text(consolidatedBefore.GetProperty("months")[i], "projectedIncome"), Text(consolidatedBefore.GetProperty("months")[i], "projectedExpense")),
                (Text(consolidated.GetProperty("months")[i], "closingBalance"), Text(consolidated.GetProperty("months")[i], "projectedIncome"), Text(consolidated.GetProperty("months")[i], "projectedExpense")));
    }

    [Fact]
    public async Task RealizedTransfer_ReplacesThePlannedOne_AndIsCountedOnce()
    {
        var s = await ArrangeAsync();
        await JsonAsync(await SaveForecastAsync(s, active: false));
        var baseline = Integrated(await SixMonthsAsync(s), 0);
        var planned = await PlannedTransferAsync(s, 3000m, Today);

        var transfer = (await ReadAsync<IdDto>(await PostAsync(s, "transfers", new { sourceAccountId = s.Pj, destinationAccountId = s.Pf, amount = 3000m, transferDate = Today, description = "Repasse" }))).Id;
        await JsonAsync(await PostAsync(s, $"planned-transfers/{planned}/link", new { transferId = transfer }));

        var after = Integrated(await SixMonthsAsync(s), 0);
        Assert.Equal(("0.00", "3000.00"), (Text(after, "transfers", "plannedPjToPf"), Text(after, "transfers", "realizedPjToPf")));
        Assert.Equal(Num(baseline, "pf", "closing") + 3000m, Num(after, "pf", "closing")); // uma vez só, nunca 6.000
        Assert.Equal(Num(baseline, "pj", "closing") - 3000m, Num(after, "pj", "closing"));
    }

    // ---- Recuperação do limite PF -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OverdraftRecovery_FollowsTheProjection_WithoutCreatingSnapshots()
    {
        var s = await ArrangeAsync();
        var snapshotsBefore = (await CountsAsync()).Snapshots;
        var due = new DateOnly(Today.Year, Today.Month, 28);
        if (due < Today) due = Today.AddDays(1);
        await PlannedTransferAsync(s, 2000m, due);

        var plan = await SixMonthsAsync(s);
        var overdraft = Integrated(plan, 0).GetProperty("overdraft");
        var usedStart = Num(overdraft, "usedStart"); var usedEnd = Num(overdraft, "usedEnd");
        Assert.True(usedStart > 0m);
        Assert.Equal(usedStart - usedEnd, Num(overdraft, "recovery")); // LimiteUtilizadoInicial − LimiteUtilizadoFinal
        Assert.Equal(Math.Max(0m, 16100m - usedEnd), Num(overdraft, "available"));
        Assert.Equal(0m, Num(overdraft, "excess"));
        Assert.Equal("Provisional", Text(overdraft, "targetState"));
        Assert.Equal(snapshotsBefore, (await CountsAsync()).Snapshots); // leitura não fixa meta
        var firstOpening = Num(Integrated(plan, 0), "pf", "opening"); var lastClosing = Num(Integrated(plan, 5), "pf", "closing");
        Assert.Equal(Math.Max(0m, -firstOpening) - Math.Max(0m, -lastClosing), Num(plan.GetProperty("integrated"), "recoveryOverPeriod"));
    }

    [Fact]
    public async Task OverdraftExcess_IsShownSeparately_WhenTheProjectedBalanceExceedsTheLimit()
    {
        var s = await ArrangeAsync(pfOpening: "-17000.00");
        var overdraft = Integrated(await SixMonthsAsync(s), 0).GetProperty("overdraft");
        Assert.Equal("0.00", Text(overdraft, "available"));
        Assert.True(Num(overdraft, "excess") > 0m);
    }

    // ---- Cobertura incompleta --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MissingCardsAndIncome_AreNeverPresentedAsACompleteProjection()
    {
        var s = await ArrangeAsync();
        var plan = await SixMonthsAsync(s);
        var month = Integrated(plan, 0);
        Assert.False(month.GetProperty("complete").GetBoolean());
        var quality = month.GetProperty("quality").EnumerateArray().Select(q => q.GetString()).ToList();
        Assert.Contains("NoCardsRegistered", quality);
        Assert.Contains("IncomeForecastNotConfigured", quality);
        var coverage = plan.GetProperty("months")[0].GetProperty("coverage");
        Assert.False(coverage.GetProperty("dataComplete").GetBoolean());
        Assert.Contains("NoCardsRegistered", coverage.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));

        await JsonAsync(await SaveForecastAsync(s));
        var approved = Integrated(await SixMonthsAsync(s), 0).GetProperty("quality").EnumerateArray().Select(q => q.GetString()).ToList();
        Assert.DoesNotContain("IncomeForecastNotConfigured", approved);
        Assert.Contains("NoCardsRegistered", approved); // sem cartões cadastrados a cobertura continua parcial
    }

    // ---- Validação e isolamento -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidSettings_AreRejected_AndAnotherWorkspaceCannotSeeOrChangeThem()
    {
        var s = await ArrangeAsync();
        await JsonAsync(await SaveForecastAsync(s, rate: "0.00"), HttpStatusCode.BadRequest);
        await JsonAsync(await SaveForecastAsync(s, hours: "25"), HttpStatusCode.BadRequest);
        await JsonAsync(await SaveForecastAsync(s, tax: "101"), HttpStatusCode.BadRequest);
        await JsonAsync(await SaveForecastAsync(s, day: 0), HttpStatusCode.BadRequest);
        await JsonAsync(await SaveForecastAsync(s, offset: 4), HttpStatusCode.BadRequest);
        await JsonAsync(await SaveForecastAsync(s, account: Guid.NewGuid()), HttpStatusCode.BadRequest);
        await JsonAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/income-forecast/months/{Month(ThisMonth)}", s.Session, new { billableDays = 5, isDismissed = false }), HttpStatusCode.Conflict); // sem configuração

        await JsonAsync(await SaveForecastAsync(s));
        await JsonAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/income-forecast/months/{Month(ThisMonth)}", s.Session, new { billableDays = 32, isDismissed = false }), HttpStatusCode.BadRequest);
        var other = await ArrangeAsync("proj01-other");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{s.Plan}/income-forecast", other.Session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/income-forecast", other.Session, new { accountId = other.Pj, hourlyRate = "1.00", hoursPerDay = "1", paymentDay = 1, paymentOffsetMonths = 0, taxReservePercent = "0.00", isActive = true })).StatusCode);
    }

    // ---- Projeção consolidada (Entrega 2) ---------------------------------------------------------------------------------------------------------

    private async Task<JsonElement> ProjectionAsync(Setup s, int months, DateOnly? from = null) =>
        await JsonAsync(await SendAsync(HttpMethod.Get, $"{s.Plan}/projection?months={months}" + (from is { } f ? $"&from={Month(f)}" : ""), s.Session));

    private static JsonElement Scenario(JsonElement projection, string name) => projection.GetProperty("scenarios").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name).GetProperty("months");

    [Fact]
    public async Task TheProjection_IsIdenticalInTheCoincidingMonths_ForAnyHorizon_AndItsReadsWriteNothing()
    {
        var s = await ArrangeAsync("proj02");
        await JsonAsync(await SaveForecastAsync(s, day: 10));
        await PlannedAsync(s, s.Pf, "Expense", "1500.00", ThisMonth.AddMonths(3).AddDays(4));
        var before = await CountsAsync();

        var six = await ProjectionAsync(s, 6); var thirteen = await ProjectionAsync(s, 13); var twentyFour = await ProjectionAsync(s, 24);

        Assert.Equal((6, 13, 24), (Scenario(six, "Current").GetArrayLength(), Scenario(thirteen, "Current").GetArrayLength(), Scenario(twentyFour, "Current").GetArrayLength()));
        foreach (var scenario in new[] { "Current", "Ceilings" })
            for (var i = 0; i < 6; i++)
            {
                var a = Scenario(six, scenario)[i]; var b = Scenario(thirteen, scenario)[i]; var c = Scenario(twentyFour, scenario)[i];
                foreach (var other in new[] { b, c })
                {
                    Assert.Equal(Text(a, "pf", "closing"), Text(other, "pf", "closing"));
                    Assert.Equal(Text(a, "pj", "closing"), Text(other, "pj", "closing"));
                    Assert.Equal(Text(a, "transfers", "autoProjectedPjToPf"), Text(other, "transfers", "autoProjectedPjToPf"));
                    Assert.Equal(Text(a, "income", "expectedGross"), Text(other, "income", "expectedGross"));
                    Assert.Equal(Text(a, "overdraft", "cumulativeRecovery"), Text(other, "overdraft", "cumulativeRecovery"));
                }
            }
        for (var i = 6; i < 13; i++) Assert.Equal(Text(Scenario(thirteen, "Current")[i], "pf", "closing"), Text(Scenario(twentyFour, "Current")[i], "pf", "closing"));
        Assert.Equal(before, await CountsAsync()); // leitura não cria nem altera registros financeiros
    }

    [Fact]
    public async Task AProjectionStartingInAFutureMonth_CarriesTheBalanceOfTheMonthsBeforeIt_AndEachClosingFeedsTheNextOpening()
    {
        var s = await ArrangeAsync("proj03");
        await JsonAsync(await SaveForecastAsync(s, day: 10));
        var full = Scenario(await ProjectionAsync(s, 6), "Current");
        var future = Scenario(await ProjectionAsync(s, 3, ThisMonth.AddMonths(3)), "Current");

        for (var i = 0; i < 3; i++) Assert.Equal(Text(full[3 + i], "pf", "closing"), Text(future[i], "pf", "closing")); // não recomeça do zero
        Assert.Equal(Text(full[2], "pf", "closing"), Text(future[0], "pf", "opening"));
        for (var i = 1; i < 6; i++) Assert.Equal(Text(full[i - 1], "pf", "closing"), Text(full[i], "pf", "opening"));
    }

    [Fact]
    public async Task TheProjectionRejectsAHorizonOutsideOneToThirtySixMonths()
    {
        var s = await ArrangeAsync("proj04");

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{s.Plan}/projection?months=37", s.Session)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{s.Plan}/projection?months=0", s.Session)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{s.Plan}/projection?months=36", s.Session)).StatusCode);
    }

    private async Task<Guid> FoodCategoryAsync(Setup s) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/categories", s.Session, new { name = "Alimentação", type = "Expense" }))).Id;

    [Fact]
    public async Task TheFoodProvision_IsConfigurable_AdjustableByCompetence_AndOnlyEstimatesTheMissingDifference()
    {
        var s = await ArrangeAsync("proj05");
        var withoutCategories = await ProjectionAsync(s, 3);
        Assert.Contains("FoodProvisionCategoriesNotConfigured", withoutCategories.GetProperty("notes").EnumerateArray().Select(n => n.GetString())); // sem categoria alimentar: não aplica, sinaliza

        var food = await FoodCategoryAsync(s);
        var inferred = await ProjectionAsync(s, 3);
        var next = ThisMonth.AddMonths(1);
        Assert.Contains("FoodCategoriesInferred", inferred.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(2000m, Num(Scenario(inferred, "Current")[1], "pf", "cardConsumptionComplement")); // padrão inicial de R$ 2.000, nada reconhecido ainda

        await JsonAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/food-provision", s.Session, new { monthlyAmount = "2500.00", categoryIds = new[] { food } }));
        await JsonAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/food-provision/months/{Month(next)}", s.Session, new { monthlyAmount = "1500.00" }));
        var configured = await ProjectionAsync(s, 3);

        Assert.Equal(2500m, Num(Scenario(configured, "Current")[0], "pf", "cardConsumptionComplement"));
        Assert.Equal(1500m, Num(Scenario(configured, "Current")[1], "pf", "cardConsumptionComplement")); // ajuste da competência
        Assert.DoesNotContain("FoodCategoriesInferred", configured.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        var ceilings = Scenario(configured, "Ceilings")[1];
        Assert.True(Num(ceilings, "budget", "cardProjected") >= 1500m);

        var current = await JsonAsync(await SendAsync(HttpMethod.Get, $"{s.Plan}/food-provision", s.Session));
        Assert.Equal("2500.00", Text(current, "monthlyAmount"));
        await JsonAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/food-provision", s.Session, new { monthlyAmount = "100.00", categoryIds = new[] { Guid.NewGuid() } }), HttpStatusCode.BadRequest); // categoria que não é do orçamento
    }

    [Fact]
    public async Task AProjectedOverdraftUse_FlagsUnestimatedChargesInsteadOfAssumingZero()
    {
        var negative = await ArrangeAsync("proj06", pfOpening: "-5000.00");
        var positive = await ArrangeAsync("proj07", pfOpening: "900000.00");

        var withUse = Scenario(await ProjectionAsync(negative, 2), "Current")[0];
        var without = Scenario(await ProjectionAsync(positive, 2), "Current")[0];

        Assert.Contains("OverdraftChargesNotEstimated", withUse.GetProperty("quality").EnumerateArray().Select(q => q.GetString()));
        Assert.DoesNotContain("OverdraftChargesNotEstimated", without.GetProperty("quality").EnumerateArray().Select(q => q.GetString()));
    }
}

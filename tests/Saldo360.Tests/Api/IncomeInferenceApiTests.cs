using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transactions;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// PROJ-06: receita PJ inferida do histórico (sem configuração) e repasse PJ → PF projetado, com PostgreSQL real. Tudo é planejamento: nenhuma transferência, lançamento ou previsão
/// é criado; a receita inferida é sempre sinalizada; configuração explícita prevalece; o repasse nunca consome reservas nem duplica a disponibilidade; PF, PJ e consolidado fecham juntos.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class IncomeInferenceApiTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    /// <summary>Dia do pagamento previsto: sempre depois de hoje no mês corrente (até o dia 27), para o recebimento do mês não ficar vencido conforme o dia em que a suíte roda.</summary>
    private static int PaymentDay => Math.Min(28, Today.Day + 1);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static decimal Num(JsonElement element, params string[] path) { foreach (var part in path) element = element.GetProperty(part); return decimal.Parse(element.GetString()!, CultureInfo.InvariantCulture); }
    private sealed record IdDto(Guid Id);
    private sealed record Setup(string Session, WorkspaceDto Workspace, Guid Pf, Guid Pj, Guid Income, Guid Expense)
    {
        public string Plan => $"/api/financial-workspaces/{Workspace.Id}/monthly-plan";
    }

    private static readonly decimal[] History = [15000m, 16000m, 17000m, 15500m, 16500m, 15200m]; // 1..6 meses atrás; mediana = 15.750,00

    /// <summary>Conta PJ e PF com abertura há 8 meses; o histórico semeado deixa o saldo PJ de hoje exatamente em R$ 500 e o da PF em −R$ 5.000.</summary>
    private async Task<Setup> ArrangeAsync(string user, int historyMonths = 6, bool withInternalTransferNoise = true)
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var root = $"/api/financial-workspaces/{workspace.Id}";
        async Task<Guid> Create(string path, object body) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{root}/{path}", session, body))).Id;
        var openingDate = ThisMonth.AddMonths(-8);
        var historyIncome = History.Take(historyMonths).Sum() + 300m * historyMonths + 50m * historyMonths + (withInternalTransferNoise ? 5000m : 0m);
        var pf = await Create("accounts", new { name = "Itaú PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = withInternalTransferNoise ? "0.00" : "-5000.00", openingBalanceDate = openingDate, overdraftLimit = "16100.00" });
        var pj = await Create("accounts", new { name = "Itaú PJ", type = "Checking", financialScope = "PJ", openingBalance = (500m - historyIncome).ToString("0.00", CultureInfo.InvariantCulture), openingBalanceDate = openingDate });
        var income = await Create("categories", new { name = "Receitas", type = "Income" });
        var expense = await Create("categories", new { name = "Despesas", type = "Expense" });

        await using var db = Db();
        var owner = workspace.OwnerUserId; var now = DateTimeOffset.UtcNow;
        void Add(Guid account, Guid category, CategoryType type, string description, decimal amount, DateOnly date) =>
            db.FinancialTransactions.Add(FinancialTransaction.CreateFromPluggy(workspace.Id, account, category, type, description, amount, date, owner, now));
        for (var k = 1; k <= historyMonths; k++)
        {
            var month = ThisMonth.AddMonths(-k);
            Add(pj, income, CategoryType.Income, "PIX RECEBIDO CLIENTE ACME", History[k - 1], month.AddDays(9));           // faturamento operacional (dia 10)
            Add(pj, income, CategoryType.Income, "REND PAGO APLIC AUT MAIS", 300m, month.AddDays(27));                   // rendimento: não é faturamento
            Add(pj, income, CategoryType.Income, "EST DEBITO SEGURO 04/48", 50m, month.AddDays(4));                      // estorno: não é faturamento
        }
        if (withInternalTransferNoise && historyMonths >= 2)
        {
            var day = ThisMonth.AddMonths(-2).AddDays(13);
            Add(pj, income, CategoryType.Income, "PIX TRANSF DIEGO S14/09", 5000m, day);                                   // entrada que espelha uma saída da PF: transferência interna
            Add(pf, expense, CategoryType.Expense, "PIX ENVIADO PARA PJ", 5000m, day);
            // a PF terminou em −5.000: abertura 0 − 5.000
        }
        await db.SaveChangesAsync();
        return new(session, workspace, pf, pj, income, expense);
    }

    private async Task<JsonElement> SixMonthsAsync(Setup s, string? scope = null)
    {
        var response = await SendAsync(HttpMethod.Get, $"{s.Plan}/six-months" + (scope is null ? "" : $"?scope={scope}"), s.Session);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Months(JsonElement plan) => plan.GetProperty("integrated").GetProperty("months").EnumerateArray();
    private static string[] Quality(JsonElement month) => month.GetProperty("quality").EnumerateArray().Select(x => x.GetString()!).ToArray();

    private async Task<(int Transactions, int Planned, int Transfers, int PlannedTransfers, int Forecasts, int Snapshots)> CountsAsync()
    {
        await using var db = Db();
        return (await db.FinancialTransactions.CountAsync(), await db.FinancialPlannedTransactions.CountAsync(), await db.FinancialTransfers.CountAsync(), await db.FinancialPlannedTransfers.CountAsync(),
            await db.FinancialIncomeForecasts.CountAsync(), await db.OverdraftRecoverySnapshots.CountAsync());
    }

    private Task<HttpResponseMessage> SaveForecastAsync(Setup s) =>
        SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/income-forecast", s.Session, new { accountId = s.Pj, hourlyRate = "100.00", hoursPerDay = "8", paymentDay = PaymentDay, paymentOffsetMonths = 1, taxReservePercent = "11.00", operationalReserve = "0.00", isActive = true });

    [Fact]
    public async Task WithoutConfiguration_TheMedianOfTheValidMonthsIsProjected_FlaggedAsInferred_AndCoverageStaysPartial()
    {
        var s = await ArrangeAsync("proj06-inferred");
        var before = await CountsAsync();

        var plan = await SixMonthsAsync(s);

        var integrated = plan.GetProperty("integrated");
        Assert.True(integrated.GetProperty("incomeInferred").GetBoolean());
        Assert.True(integrated.GetProperty("incomeForecastActive").GetBoolean());
        var receipts = Months(plan).Where(m => m.GetProperty("income").GetProperty("source").GetString() == "Forecast").ToList();
        Assert.NotEmpty(receipts);
        Assert.All(receipts, m =>
        {
            Assert.Equal(15750.00m, Num(m, "income", "expectedGross"));          // mediana; rendimento, estorno e transferência interna fora
            Assert.True(m.GetProperty("income").GetProperty("inferred").GetBoolean());
            Assert.Null(m.GetProperty("income").GetProperty("hourlyRate").GetString()); // valor/hora não foi configurado: nunca inventado
            Assert.False(m.GetProperty("complete").GetBoolean());                  // cobertura parcial
            Assert.Contains("IncomeForecastInferred", Quality(m));
            Assert.Contains("TaxReserveNotConfigured", Quality(m));
        });
        Assert.Equal(before, await CountsAsync()); // inferir e projetar não grava nada
    }

    [Fact]
    public async Task ExplicitConfiguration_AlwaysTakesPriorityOverTheInference()
    {
        var s = await ArrangeAsync("proj06-configured");
        Assert.True((await SixMonthsAsync(s)).GetProperty("integrated").GetProperty("incomeInferred").GetBoolean());

        await SaveForecastAsync(s);
        var plan = await SixMonthsAsync(s);

        Assert.False(plan.GetProperty("integrated").GetProperty("incomeInferred").GetBoolean());
        var receipt = Months(plan).First(m => m.GetProperty("income").GetProperty("source").GetString() == "Forecast");
        Assert.Equal("100.00", receipt.GetProperty("income").GetProperty("hourlyRate").GetString()); // valor/hora configurado: 100 × 8 × dias úteis
        Assert.DoesNotContain("IncomeForecastInferred", Quality(receipt));
    }

    [Fact]
    public async Task WithTooLittleHistory_NothingIsInferred_AndTheMonthStaysIncomplete()
    {
        var s = await ArrangeAsync("proj06-short", historyMonths: 2, withInternalTransferNoise: false);

        var plan = await SixMonthsAsync(s);

        Assert.False(plan.GetProperty("integrated").GetProperty("incomeForecastActive").GetBoolean());
        Assert.All(Months(plan), m =>
        {
            Assert.Equal("NotConfigured", m.GetProperty("income").GetProperty("source").GetString());
            Assert.Equal(0m, Num(m, "transfers", "autoProjectedPjToPf"));
            Assert.Contains("IncomeForecastNotConfigured", Quality(m));
        });
    }

    [Fact]
    public async Task TheProjectedTransfer_LeavesThePjAtItsReservesAndReachesThePfExactly_AndTheConsolidatedIsNeutral()
    {
        var s = await ArrangeAsync("proj06-transfer");
        await SaveForecastAsync(s); // tributo 11%: reserva que a PJ nunca cede

        var plan = await SixMonthsAsync(s);
        var consolidated = await SixMonthsAsync(s, "all");

        var months = Months(plan).ToList();
        var accumulatedTransfers = 0m;
        for (var i = 0; i < months.Count; i++)
        {
            var m = months[i];
            var auto = Num(m, "transfers", "autoProjectedPjToPf");
            accumulatedTransfers += auto;
            Assert.True(Num(m, "pj", "closing") >= 0m, $"PJ negativa em {m.GetProperty("competence").GetString()}");                   // nunca negativa
            Assert.True(Num(m, "pj", "closing") >= Num(m, "pj", "taxReserve") - 0.001m || m.GetProperty("pj").GetProperty("taxObligationRegistered").GetBoolean()); // nunca consome o tributo do mês
            Assert.Equal(Num(m, "transfers", "plannedPjToPf") + Num(m, "transfers", "realizedPjToPf") + auto, Num(m, "pf", "transfersIn")); // entrada na PF = repasse da PJ
            // Consolidado: o repasse é neutro; PF + PJ fecham no mesmo saldo do consolidado, e o repasse nunca é receita nem despesa.
            Assert.Equal(Num(m, "pf", "closing") + Num(m, "pj", "closing"), decimal.Parse(consolidated.GetProperty("months")[i].GetProperty("closingBalance").GetString()!, CultureInfo.InvariantCulture));
            Assert.Equal(0m, Num(m, "pf", "entries"));
        }
        Assert.True(accumulatedTransfers > 0m);
        Assert.Equal(Num(months[0], "pf", "opening") + months.Sum(m => Num(m, "transfers", "autoProjectedPjToPf")), Num(months[^1], "pf", "closing")); // PF só recebe repasse (sem outras movimentações)
    }

    [Fact]
    public async Task InferredIncome_MakesTheDependentTransfersExplicit_AndItIsNeverPresentedAsGuaranteed()
    {
        var s = await ArrangeAsync("proj06-dependent");

        var plan = await SixMonthsAsync(s);

        var dependent = Months(plan).Where(m => m.GetProperty("transfers").GetProperty("dependsOnInferredIncome").GetBoolean()).ToList();
        Assert.NotEmpty(dependent);
        Assert.All(dependent, m => Assert.Contains("TransferDependsOnInferredIncome", Quality(m)));
        Assert.All(dependent, m => Assert.False(m.GetProperty("complete").GetBoolean()));
    }

    [Fact]
    public async Task APlannedTransferAndARealizedReceipt_NeverDuplicateTheSameAvailability()
    {
        var s = await ArrangeAsync("proj06-nodup");
        await SaveForecastAsync(s);
        var baseline = await SixMonthsAsync(s);
        var totalBefore = Months(baseline).Sum(m => Num(m, "transfers", "autoProjectedPjToPf") + Num(m, "transfers", "plannedPjToPf"));
        var due = Today.AddDays(1);

        var created = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{s.Workspace.Id}/planned-transfers", s.Session,
            new { sourceAccountId = s.Pj, destinationAccountId = s.Pf, amount = 400m, dueDate = due, description = "Repasse PJ → PF" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var after = await SixMonthsAsync(s);
        var totalAfter = Months(after).Sum(m => Num(m, "transfers", "autoProjectedPjToPf") + Num(m, "transfers", "plannedPjToPf"));
        Assert.Equal(totalBefore, totalAfter); // o previsto substitui parte do automático: a mesma disponibilidade não vira duas transferências
        Assert.Equal(Num(Months(baseline).Last(), "pf", "closing"), Num(Months(after).Last(), "pf", "closing"));
    }


    [Fact]
    public async Task TheDailyAgendaBalanceAndTheMonthlyPlanning_AgreeOnTheProjectedPfClosing()
    {
        var s = await ArrangeAsync("proj06-daily");
        var plan = await SixMonthsAsync(s);
        var last = Months(plan).Last();
        var competence = DateOnly.ParseExact(last.GetProperty("competence").GetString()! + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var through = competence.AddMonths(1).AddDays(-1);

        var response = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{s.Workspace.Id}/financial-calendar/balances?from={Today:yyyy-MM-dd}&through={through:yyyy-MM-dd}&accountId={s.Pf}", s.Session);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var days = JsonDocument.Parse(body).RootElement.GetProperty("days");
        var closing = decimal.Parse(days[days.GetArrayLength() - 1].GetProperty("closingBalance").GetString()!, CultureInfo.InvariantCulture);

        Assert.Equal(Num(last, "pf", "closing"), closing); // Agenda diária e planejamento mensal leem o mesmo plano de repasse
    }
    [Fact]
    public async Task ReadingNeverCreatesTransfersTransactionsOrForecasts_AndIsRepeatable()
    {
        var s = await ArrangeAsync("proj06-readonly");
        var before = await CountsAsync();

        var first = await SixMonthsAsync(s);
        var second = await SixMonthsAsync(s);

        Assert.Equal(first.GetProperty("integrated").GetRawText(), second.GetProperty("integrated").GetRawText()); // determinístico: o mesmo repasse a cada leitura
        Assert.Equal(before, await CountsAsync());
    }
}

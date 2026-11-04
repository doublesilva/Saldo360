using System.Net;
using System.Text.Json;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// PROJ-03: débito bancário pendente (Pluggy) na projeção de caixa. Coberto pela agenda não soma de novo; sem compromisso candidato entra uma vez; com candidato sem par seguro fica fora
/// do saldo e é sinalizado; efetivado depois deixa de ser pendente. Agenda, planejamento mensal e Plano × Realizado usam a mesma classificação.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private async Task<(Ctx C, Guid Account)> ArrangePendingBankAsync(string user, string? plannedDescription = null, decimal plannedAmount = 0m, string status = "PENDING")
    {
        var c = await ArrangeAsync(user, seed: false);
        Pluggy.TransactionsByAccount[BankA] = [BankTx("salary", MonthStart, 1000m, "SALARIO"), Tx("pending", BankA, DaysAgo(MonthStart), -40m, status, "PIX", "PADARIA CENTRAL")];
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, 1000m), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var account = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        if (plannedDescription is not null)
        {
            var category = await CreateCategoryAsync(c, "Mercado", "Expense");
            var planned = await PostAsync(c, $"/api/financial-workspaces/{c.Workspace}/planned-transactions",
                new { accountId = account, categoryId = category, type = "Expense", description = plannedDescription, amount = plannedAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), dueDate = MonthStart.ToString("yyyy-MM-dd") });
            Assert.Equal(HttpStatusCode.Created, planned.StatusCode);
        }
        await SyncAsync(c);
        return (c, account);
    }

    private async Task<JsonElement> CurrentMonthAsync(Ctx c, Guid account) =>
        (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/monthly-plan/six-months?from={MonthStart:yyyy-MM}&scope=account&accountId={account}")).GetProperty("months")[0];

    private static string[] MonthReasons(JsonElement month) => month.GetProperty("coverage").GetProperty("reasons").EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public async Task AnUncoveredPendingBankDebitEntersTheCashProjectionExactlyOnceAndNeverTheRealized()
    {
        var (c, account) = await ArrangePendingBankAsync("proj03-uncovered");

        var month = await CurrentMonthAsync(c, account);

        Assert.Equal(("0.00", "40.00", "40.00"), (Text(month, "realizedExpense"), Text(month, "knownObligations"), Text(month, "projectedExpense"))); // uma vez, nunca duas
        Assert.DoesNotContain("PendingBankAmbiguous", MonthReasons(month));
        var cash = (await PlanActualAsync(c, $"?scope=account&accountId={account}")).GetProperty("expenses").GetProperty("cashFlow");
        Assert.Equal(("0.00", "40.00", "40.00"), (Text(cash, "realized"), Text(cash, "pending"), Text(cash, "projected"))); // mesmo valor no Plano × Realizado
    }

    [Fact]
    public async Task APendingBankDebitWithAUniqueSafeMatchInTheAgendaIsNotAddedAgain()
    {
        var (c, account) = await ArrangePendingBankAsync("proj03-covered", "Padaria central", 40m);

        var month = await CurrentMonthAsync(c, account);

        Assert.Equal(("40.00", "40.00"), (Text(month, "knownObligations"), Text(month, "projectedExpense"))); // 40 (o compromisso), não 80
        Assert.DoesNotContain("PendingBankAmbiguous", MonthReasons(month));
        var cash = (await PlanActualAsync(c, $"?scope=account&accountId={account}")).GetProperty("expenses").GetProperty("cashFlow");
        Assert.Equal("40.00", Text(cash, "pending"));
    }

    [Fact]
    public async Task APendingBankDebitWithACandidateButNoSafeMatchStaysOutOfTheBalanceAndIsFlaggedAsAmbiguous()
    {
        var (c, account) = await ArrangePendingBankAsync("proj03-ambiguous", "Mercado semanal", 41m);

        var month = await CurrentMonthAsync(c, account);

        Assert.Equal(("41.00", "41.00"), (Text(month, "knownObligations"), Text(month, "projectedExpense"))); // só o compromisso da agenda; o débito não é despesa adicional
        Assert.Contains("PendingBankAmbiguous", MonthReasons(month));
        Assert.Equal("40.00", Text(month, "coverage", "pendingBankAmbiguous")); // continua visível no diagnóstico
        Assert.False(month.GetProperty("coverage").GetProperty("dataComplete").GetBoolean());
        var plan = await PlanActualAsync(c, $"?scope=account&accountId={account}");
        var components = plan.GetProperty("expenses").GetProperty("cashFlow").GetProperty("components").EnumerateArray().ToDictionary(x => x.GetProperty("code").GetString()!, x => x.GetProperty("amount").GetString()!);
        Assert.Equal(("0.00", "40.00"), (components["PendingBank"], components["PendingBankAmbiguous"]));
        Assert.Contains("PendingBankAmbiguous", plan.GetProperty("coverage").GetProperty("reasons").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task APendingDebitThatPostsLaterBecomesRealizedAndStopsBeingAFutureObligation()
    {
        var (c, account) = await ArrangePendingBankAsync("proj03-posted");
        Assert.Equal("40.00", Text(await CurrentMonthAsync(c, account), "knownObligations"));

        Pluggy.TransactionsByAccount[BankA] = [BankTx("salary", MonthStart, 1000m, "SALARIO"), Tx("pending", BankA, DaysAgo(MonthStart), -40m, "POSTED", "PIX", "PADARIA CENTRAL")];
        await SyncAsync(c);

        var month = await CurrentMonthAsync(c, account);
        Assert.Equal(("40.00", "0.00", "40.00"), (Text(month, "realizedExpense"), Text(month, "knownObligations"), Text(month, "projectedExpense"))); // 40 uma vez, agora realizado
    }

    [Fact]
    public async Task TheDailyAndMonthlyProjectionsAgreeOnTheClosingBalanceWithAnUncoveredPendingDebit()
    {
        var (c, account) = await ArrangePendingBankAsync("proj03-daily");
        var month = await CurrentMonthAsync(c, account);
        var end = MonthStart.AddMonths(1).AddDays(-1);

        var daily = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar/balances?from={MonthStart:yyyy-MM-dd}&through={end:yyyy-MM-dd}&accountId={account}");

        var lastDay = daily.GetProperty("days").EnumerateArray().Last();
        Assert.Equal(Text(month, "closingBalance"), lastDay.GetProperty("closingBalance").GetString());
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Accounts;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// T09.5: saldo informado pelo banco × saldo calculado. Sempre leitura: a diferença é informada e classificada, nunca ajustada. Cliente Pluggy simulado.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private static DateOnly MonthStart => new(Today.Year, Today.Month, 1);
    private static DateOnly PreviousMonthStart => MonthStart.AddMonths(-1);
    private static int DaysAgo(DateOnly date) => Today.DayNumber - date.DayNumber;

    private static PluggyTransaction BankTx(string id, DateOnly date, decimal amount, string description = "MERCADO") =>
        Tx(id, BankA, DaysAgo(date), amount, "POSTED", "PIX", description, category: amount > 0 ? "Salary" : "Groceries");

    private static PluggyAccount BankAccount(string id, string item, decimal balance, decimal? contracted = null, decimal? used = null) =>
        FakePluggyClient.Account(id, item, balance: balance) with { UpdatedAt = DateTimeOffset.UtcNow, Bank = new PluggyBankData("0001", balance, null, contracted ?? 500m, used ?? 0m, null) };

    /// <summary>Histórico limpo: +1000 e −250 no mês anterior e +100 no início do mês atual; saldo observado 5.000,00.</summary>
    private async Task<(Ctx C, Guid Account)> ArrangeCleanHistoryAsync(string user, decimal observed = 5000m)
    {
        var c = await ArrangeAsync(user, seed: false);
        Pluggy.TransactionsByAccount[BankA] = [BankTx("in", PreviousMonthStart.AddDays(3), 1000m, "SALARIO"), BankTx("out", PreviousMonthStart.AddDays(10), -250m), BankTx("cur", MonthStart, 100m, "SALARIO")];
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, observed), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var bank = await LinkBankAsync(c);
        var accountId = bank.GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);
        return (c, accountId);
    }

    private Task<JsonElement> ReconciliationAsync(Ctx c) => JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/balance-reconciliation");

    private static JsonElement AccountOf(JsonElement overview, Guid id) => overview.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == id);

    private static string Text(JsonElement element, params string[] path)
    {
        foreach (var part in path) element = element.GetProperty(part);
        return element.GetString()!;
    }

    // ---- 1, 4, 7, 13, 14: âncora confiável, reconstrução e consistência -------------------------------------------------------

    [Fact]
    public async Task AReliableBankReferenceWithFullHistoryReconstructsMonthOpeningsFromPostedMovementsAndShowsNoDivergence()
    {
        var (c, accountId) = await ArrangeCleanHistoryAsync("t95-clean");

        var account = AccountOf(await ReconciliationAsync(c), accountId);

        Assert.Equal("NoDivergence", Text(account, "status"));
        Assert.Equal(("5000.00", "CurrentBalance", "Reliable"), (Text(account, "bank", "amount"), Text(account, "bank", "kind"), Text(account, "bank", "reliability")));
        Assert.Equal(("5000.00", "0.00", "BalanceAnchor"), (Text(account, "calculated", "amount"), Text(account, "difference"), Text(account, "calculated", "openingSource")));
        Assert.Equal(("Full", 3), (Text(account, "coverage", "quality"), account.GetProperty("coverage").GetProperty("postedMovements").GetInt32()));
        Assert.Equal(PreviousMonthStart.AddDays(3).ToString("yyyy-MM-dd"), Text(account, "calculated", "calculableFrom"));

        // Abertura do mês atual: 5.000 − 100 (movimento de hoje em diante) = 4.900 pelos dois lados. O mês anterior começa antes do histórico: indisponível, nunca zero.
        var period = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance-reconciliation?from={PreviousMonthStart:yyyy-MM}&through={MonthStart:yyyy-MM}");
        var months = period.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal(2, months.Count);
        Assert.Equal((JsonValueKind.Null, "BeforeBankHistory", "BeforeCalculableStart"), (months[0].GetProperty("bankOpening").ValueKind, Text(months[0], "bankReason"), Text(months[0], "ledgerReason")));
        Assert.Equal(("4900.00", "4900.00", "0.00"), (Text(months[1], "bankOpening"), Text(months[1], "ledgerOpening"), Text(months[1], "difference")));
    }

    [Fact]
    public async Task ReadingIsIdempotentAndNeverAdjustsAnything()
    {
        var (c, accountId) = await ArrangeCleanHistoryAsync("t95-readonly");
        await using var db = Db();
        var before = (await db.FinancialTransactions.CountAsync(), await db.FinancialTransfers.CountAsync(), await db.FinancialAccountBalanceAnchors.CountAsync(),
            (await db.FinancialAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).OpeningBalance);

        var first = (await ReconciliationAsync(c)).GetRawText();
        var second = (await ReconciliationAsync(c)).GetRawText();
        await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance-reconciliation");
        Assert.Equal(first, second);
        Assert.Equal(before, (await db.FinancialTransactions.CountAsync(), await db.FinancialTransfers.CountAsync(), await db.FinancialAccountBalanceAnchors.CountAsync(),
            (await db.FinancialAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).OpeningBalance));
        // Nova sincronização com a mesma observação não cria nova âncora nem ajuste.
        await SyncAsync(c);
        Assert.Equal(before.Item3, await db.FinancialAccountBalanceAnchors.CountAsync());
        Assert.Equal(before.Item1, await db.FinancialTransactions.CountAsync());
    }

    // ---- 2, 8, 11: âncora provisória, pendentes e movimentos em revisão --------------------------------------------------------

    [Fact]
    public async Task MovementsInReviewLimitTheCalculableHistoryAreSummarizedAndPendingOnesNeverEnterTheBalance()
    {
        var c = await ArrangeAsync("t95-review"); // semente: pendente (D-7), transferência sem contraparte em revisão (D-6) e movimentos aplicados
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, 1500.75m), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var accountId = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);

        var account = AccountOf(await ReconciliationAsync(c), accountId);

        Assert.Equal(("MovementsInReview", "ReviewLimitsCalculableHistory"), (Text(account, "status"), Text(account, "statusReason")));
        Assert.Equal(("Provisional", "1500.75", "0.00"), (Text(account, "bank", "reliability"), Text(account, "calculated", "amount"), Text(account, "difference")));
        var review = account.GetProperty("review");
        Assert.Equal((1, "-20.00", 1, 0, 1), (review.GetProperty("count").GetInt32(), Text(review, "netAmount"), review.GetProperty("beforeLedgerStart").GetInt32(), review.GetProperty("insideLedgerRange").GetInt32(), review.GetProperty("pending").GetInt32()));
        Assert.Equal(D(5), Text(account, "calculated", "calculableFrom"));
        Assert.Equal("LimitedByReview", Text(account, "coverage", "quality"));
        // O histórico anterior existe no banco, mas o Saldo360 só o calcula depois de decidir o item: a reconstrução bancária o cobre (inclui o movimento em revisão).
        Assert.Equal(D(8), Text(account, "coverage", "bankSideFrom"));

        // O mês atual só tem abertura calculada se a revisão (D-6) não for datada dele em diante; senão ela é a movimentação que bloqueia.
        var month = (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance-reconciliation?from={MonthStart:yyyy-MM}&through={MonthStart:yyyy-MM}")).GetProperty("months")[0];
        Assert.Equal(Today.AddDays(-5) > MonthStart ? 1 : 0, month.GetProperty("blockingReview").GetInt32());

        // Resolvido o item, o histórico calculável se amplia e a conta deixa de ter ressalva.
        var posting = (await ReviewAsync(c)).GetProperty("items")[0].GetProperty("postingId").GetGuid();
        await PostAsync(c, $"{Integration(c.Workspace)}/review/{posting}/resolve", new { resolution = "AsExpense" });
        var resolved = AccountOf(await ReconciliationAsync(c), accountId);
        Assert.Equal(("NoDivergence", "Full", D(9)), (Text(resolved, "status"), Text(resolved, "coverage", "quality"), Text(resolved, "calculated", "calculableFrom")));
    }

    // ---- 12: divergência real, sem ajuste --------------------------------------------------------------------------------------

    [Fact]
    public async Task AWrongManualOpeningBalanceShowsARealDivergenceThatIsNeverAdjusted()
    {
        var (c, accountId) = await ArrangeCleanHistoryAsync("t95-diverge");
        // O usuário cadastra um saldo inicial que não bate com o banco: o manual prevalece sobre a referência bancária.
        var edit = await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}", c.Session, new
        {
            name = "Itaú corrente", type = "Checking", financialScope = "PF", isActive = true,
            openingBalance = 4000m, openingBalanceDate = PreviousMonthStart.AddDays(3).ToString("yyyy-MM-dd"),
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        await using var db = Db();
        var movements = await db.FinancialTransactions.CountAsync();

        var account = AccountOf(await ReconciliationAsync(c), accountId);

        Assert.Equal(("DivergenceFound", "UnexplainedDifference", "Manual"), (Text(account, "status"), Text(account, "statusReason"), Text(account, "calculated", "openingSource")));
        Assert.Equal(("5000.00", "4850.00", "150.00", "150.00"), (Text(account, "bank", "amount"), Text(account, "calculated", "amount"), Text(account, "difference"), Text(account, "unexplained")));
        Assert.Equal(movements, await db.FinancialTransactions.CountAsync()); // nenhum ajuste foi criado para zerar a diferença
        Assert.Equal(0, await db.FinancialTransactions.CountAsync(t => EF.Functions.ILike(t.Description, "%ajuste%")));
    }

    // ---- 6: cheque especial e natureza do saldo --------------------------------------------------------------------------------

    [Fact]
    public async Task TheOverdraftLimitIsShownApartAndNeverAddedAndAContradictoryNatureMakesTheReferenceInsufficient()
    {
        var c = await ArrangeAsync("t95-overdraft", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [BankTx("x", Today.AddDays(-3), -100m)];
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, -500m, 1000m, 500m), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var accountId = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);

        var overdrawn = await ReconciliationAsync(c);
        var account = AccountOf(overdrawn, accountId);
        Assert.Equal(("-500.00", "-500.00", "0.00"), (Text(account, "bank", "amount"), Text(account, "calculated", "amount"), Text(account, "difference")));
        Assert.Equal(("1000.00", "500.00"), (Text(account, "bank", "overdraft", "contractedLimit"), Text(account, "bank", "overdraft", "usedLimit")));
        Assert.Contains("não compõe o saldo", Text(account, "bank", "overdraft", "note"));
        Assert.Equal("-500.00", Text(overdrawn, "consolidated", "bank")); // o limite de 1.000 não vira dinheiro próprio

        // Saldo positivo com cheque especial em uso: a natureza do saldo é incerta, a referência não serve e o consolidado não finge estar completo.
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, 200m, 1000m, 100m) with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1) }, FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        await SyncAsync(c);
        var insufficient = await ReconciliationAsync(c);
        var unusable = AccountOf(insufficient, accountId);
        Assert.Equal(("InsufficientBankReference", "BalanceNatureAmbiguous", "Unreliable"), (Text(unusable, "status"), Text(unusable, "statusReason"), Text(unusable, "bank", "reliability")));
        Assert.False(insufficient.GetProperty("consolidated").GetProperty("complete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, insufficient.GetProperty("consolidated").GetProperty("bank").ValueKind);
    }

    // ---- 9, 10, 13: transferências, pagamento de cartão, PF/PJ separados e consolidado --------------------------------------------

    [Fact]
    public async Task TransfersAndCardBillPaymentsKeepEachAccountConsistentAndPfPjStayApart()
    {
        var c = await ArrangeAsync("t95-scopes", seed: false);
        var itemB = await LinkAsync(c.Client, c.Session, c.Workspace, ItemB, "Nubank");
        var out10 = PreviousMonthStart.AddDays(10);
        Pluggy.TransactionsByAccount[BankA] =
        [
            BankTx("seed", PreviousMonthStart.AddDays(2), 3000m, "SALARIO"),
            BankTx("own-out", out10, -500m) with { Description = "PIX ENVIADO", DescriptionRaw = "PIX ENVIADO", Category = "Transfer - PIX" },
            Tx("pay-bank", BankA, DaysAgo(PreviousMonthStart.AddDays(15)), -230m, operation: "PIX", description: "PAGAMENTO DE FATURA", category: null),
        ];
        Pluggy.TransactionsByAccount[BankB] = [Tx("own-in", BankB, DaysAgo(out10), 500m, operation: "PIX", description: "PIX RECEBIDO", category: "Transfer - PIX")];
        // Fatura do banco do mês anterior (fecha dia 8) com a compra de 230; o pagamento sai da conta no dia 16.
        Pluggy.BillsByAccount[CardA] = [TotalBill("bill-prev", PreviousMonthStart.AddDays(ClosingDay - 1), 230m)];
        Pluggy.TransactionsByAccount[CardA] = [CardTx("pay-card", "PAGAMENTO FATURA", 230m, PreviousMonthStart.AddDays(15), "") with { Type = "CREDIT", OperationType = "PAGAMENTO_FATURA", Category = null },
            CardTx("buy", "LOJA", 230m, PreviousMonthStart.AddDays(5), "bill-prev")];
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, 2270m), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        Pluggy.AccountsByItem[ItemB] = [BankAccount(BankB, ItemB, 800m)];
        var a = (await LinkBankAsync(c, extra: new { financialScope = "PF" })).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await LinkCardAsync(c, ClosingDay, DueDay);
        var bResponse = await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = itemB, pluggyAccountId = BankB, mode = "CreateNew", name = "Nubank PJ", financialScope = "PJ" });
        Assert.Equal(HttpStatusCode.Created, bResponse.StatusCode);
        var b = (await JsonOf(bResponse)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Root(c.Workspace)}/connections/{itemB}/sync")).StatusCode);

        var overview = await ReconciliationAsync(c);

        // Uma transferência (as duas pontas, cada uma na sua conta), um pagamento de fatura na data da saída e nenhuma compra de cartão na conta corrente.
        await using var db = Db();
        Assert.Equal(1, await db.FinancialTransfers.CountAsync(t => !t.IsCancelled));
        Assert.Equal(1, await db.FinancialCreditCardInvoicePayments.CountAsync());
        Assert.Equal(("0.00", "0.00"), (Text(AccountOf(overview, a), "difference"), Text(AccountOf(overview, b), "difference")));
        // Saldos de abertura do mês atual pelos dois lados: A = 2270 (observado) − nada no mês; B idem. A transferência não entra duas vezes no consolidado.
        var scopes = overview.GetProperty("byScope").EnumerateArray().ToDictionary(s => Text(s, "scope"));
        Assert.Equal(("2270.00", "800.00"), (Text(scopes["PF"], "bank"), Text(scopes["PJ"], "bank")));
        Assert.Equal(("3070.00", "3070.00", "0.00", true), (Text(overview, "consolidated", "bank"), Text(overview, "consolidated", "calculated"), Text(overview, "consolidated", "difference"), overview.GetProperty("consolidated").GetProperty("complete").GetBoolean()));
        var period = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{a}/balance-reconciliation?from={MonthStart:yyyy-MM}&through={MonthStart:yyyy-MM}");
        var month = period.GetProperty("months")[0];
        Assert.Equal(Text(month, "bankOpening"), Text(month, "ledgerOpening")); // os dois lados concordam após transferência e pagamento de fatura
    }

    // ---- 16: isolamento por workspace ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnotherWorkspaceNeverSeesTheAccountsAndInvalidPeriodsAreRejected()
    {
        var (c, accountId) = await ArrangeCleanHistoryAsync("t95-iso");
        var stranger = await SignInAsync("t95-stranger");
        var strangerWorkspace = await CreateWorkspaceAsync(stranger);
        var strangerCtx = new Ctx(stranger, strangerWorkspace.Id, c.Client, Guid.Empty);

        var own = await JsonGetAsync(strangerCtx, $"/api/financial-workspaces/{strangerWorkspace.Id}/accounts/balance-reconciliation");
        var foreign = await Send(c.Client, HttpMethod.Get, $"/api/financial-workspaces/{c.Workspace}/accounts/balance-reconciliation", stranger);
        var foreignAccount = await Send(c.Client, HttpMethod.Get, $"/api/financial-workspaces/{strangerWorkspace.Id}/accounts/{accountId}/balance-reconciliation", stranger);

        Assert.Equal(0, own.GetProperty("accounts").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignAccount.StatusCode);
        var url = $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance-reconciliation";
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(c, url + "?from=2026-13")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(c, url + "?from=2020-01&through=2026-10")).StatusCode);
    }

    // ---- 7, 14: Dashboard e Agenda -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DashboardShowsTheReferenceStateAndPfPjSubtotalsAndNeverAPartialTotalAsComplete()
    {
        var c = await ArrangeAsync("t95-dashboard"); // semente com item em revisão: referência provisória
        Pluggy.AccountsByItem[ItemA] = [BankAccount(BankA, ItemA, 1500.75m), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var accountId = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);
        // Segunda conta, PJ, sem nenhum saldo calculável (cadastrada à mão, sem saldo inicial).
        var manual = await Send(c.Client, HttpMethod.Post, $"/api/financial-workspaces/{c.Workspace}/accounts", c.Session, new { name = "Reserva PJ", type = "Savings", financialScope = "PJ" });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);

        var dashboard = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-dashboard?competence={Today:yyyy-MM}&months=3");
        var position = dashboard.GetProperty("position");

        var pf = position.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == accountId);
        Assert.Equal(("PF", "BalanceAnchor", "Provisional", "MovementsInReview", 1), (Text(pf, "scope"), Text(pf, "referenceSource"), Text(pf, "referenceReliability"), Text(pf, "reconciliationStatus"), pf.GetProperty("reviewCount").GetInt32()));
        Assert.Equal("1500.75", Text(pf, "realizedBalance"));
        Assert.True(position.GetProperty("hasReferenceCaveats").GetBoolean());
        // PF completo e PJ incompleto: o consolidado não é apresentado (nulo) e cada escopo diz o seu estado.
        var scopes = position.GetProperty("byScope").EnumerateArray().ToDictionary(s => Text(s, "scope"));
        Assert.Equal(("1500.75", true), (Text(scopes["PF"], "realizedBalance"), scopes["PF"].GetProperty("complete").GetBoolean()));
        Assert.Equal((JsonValueKind.Null, false), (scopes["PJ"].GetProperty("realizedBalance").ValueKind, scopes["PJ"].GetProperty("complete").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, position.GetProperty("realizedBalance").ValueKind);
    }

    [Fact]
    public async Task AgendaAccountBalanceAndProjectionStartFromTheSameCalculatedBalanceAsTheReconciliation()
    {
        var (c, accountId) = await ArrangeCleanHistoryAsync("t95-agenda");

        var reconciliation = AccountOf(await ReconciliationAsync(c), accountId);
        var balance = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance");
        var projection = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/projection?through={Today.AddMonths(2):yyyy-MM-dd}");
        var calendar = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar/balances?from={Today.AddDays(1):yyyy-MM-dd}&through={Today.AddDays(6):yyyy-MM-dd}&accountId={accountId}");

        var calculated = Text(reconciliation, "calculated", "amount");
        Assert.Equal(calculated, Text(balance, "balance"));
        Assert.Equal(calculated, Text(projection, "realizedBalance")); // o saldo projetado parte do mesmo saldo calculado
        Assert.Equal(calculated, Text(calendar, "openingBalance")); // e a Agenda abre com ele
    }
}

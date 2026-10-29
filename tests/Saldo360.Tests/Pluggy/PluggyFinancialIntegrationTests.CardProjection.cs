using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// T09.4: faturas por cartão, obrigação (total bancário x projeção), parcelas futuras e projeção de seis meses, sempre com o cliente Pluggy simulado.
/// Datas relativas ao dia de hoje: o cartão fecha dia 8 e vence dia 15; os testes calculam os ciclos com a mesma regra do sistema.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private const int ClosingDay = 8;
    private const int DueDay = 15;

    private static DateOnly LatestClosing => Today.Day >= ClosingDay ? new(Today.Year, Today.Month, ClosingDay) : new DateOnly(Today.Year, Today.Month, ClosingDay).AddMonths(-1);
    private static DateOnly NextClosing => LatestClosing.AddMonths(1);
    private static string Competence(DateOnly closing) => closing.AddDays(DueDay - ClosingDay).ToString("yyyy-MM");

    private static PluggyBill TotalBill(string id, DateOnly closing, decimal total, IEnumerable<(decimal Amount, DateOnly Date)>? payments = null) =>
        new(id, DateTimeOffset.Parse(closing.AddDays(DueDay - ClosingDay).ToString("yyyy-MM-dd") + "T00:00:00Z"), total, "BRL", null, null, [],
            DateTimeOffset.Parse(closing.ToString("yyyy-MM-dd") + "T00:00:00Z"),
            (payments ?? []).Select(p => new PluggyBillPayment(p.Amount, "BRL", DateTimeOffset.Parse(p.Date.ToString("yyyy-MM-dd") + "T00:00:00Z"), "DEBIT_ACCOUNT", null)).ToList());

    private static PluggyTransaction CardTx(string id, string description, decimal amount, DateOnly date, string billId, int? number = null, int? total = null, string status = "POSTED") =>
        Tx(id, CardA, 0, amount, status, "PAGAMENTO", description, card: new PluggyCardMetadata(number, total, null, null, billId.Length == 0 ? null : billId, "2222", null, null), type: "DEBIT", category: null) with
        { Date = DateTimeOffset.Parse(date.ToString("yyyy-MM-dd") + "T12:00:00Z") };

    private async Task<(Ctx C, Guid Card, Guid Bank, JsonElement CardLink)> ArrangeProjectionAsync(string user, decimal latestBankTotal, decimal paid, bool withPending)
    {
        var c = await ArrangeAsync(user, seed: false);
        var closings = Enumerable.Range(0, 4).Select(i => LatestClosing.AddMonths(-i)).ToList(); // 4 faturas fechadas: dia 8 / vencimento 15 (evidência forte)
        var payDate = Today; // sempre depois do fechamento e nunca no futuro
        Pluggy.BillsByAccount[CardA] = closings.Select((closing, i) => TotalBill($"bill-{i}", closing, i == 0 ? latestBankTotal : 100m, i == 0 && paid > 0 ? [(paid, payDate)] : null)).ToList();
        var transactions = new List<PluggyTransaction>
        {
            CardTx("c1", "LOJA", 150m, LatestClosing.AddDays(-12), "bill-0"),
            CardTx("c2", "TV", 80m, LatestClosing.AddDays(-9), "bill-0"),
            CardTx("old", "ANTIGA", 100m, closings[1].AddDays(-3), "bill-1"),
        };
        if (withPending)
        {
            transactions.Add(CardTx("s1", "SOFA 01/04", 100m, LatestClosing.AddDays(-6), "bill-0", 1, 4));
            transactions.Add(CardTx("s2", "SOFA 02/04", 100m, NextClosing.AddDays(-3), "", 2, 4, "PENDING"));
            transactions.Add(CardTx("s3", "SOFA 03/04", 100m, NextClosing.AddMonths(1).AddDays(-3), "", 3, 4, "PENDING"));
            transactions.Add(CardTx("cafe", "CAFE", 30m, Today.AddDays(1), "", status: "PENDING"));
        }
        Pluggy.TransactionsByAccount[CardA] = transactions;
        Pluggy.TransactionsByAccount[BankA] = paid > 0 ? [Tx("pay", BankA, 0, -paid, operation: "BOLETO", description: "PAGAMENTO", category: "Credit card payment") with { Date = DateTimeOffset.Parse(payDate.ToString("yyyy-MM-dd") + "T12:00:00Z") }] : [];
        var bank = await LinkBankAsync(c);
        var card = await LinkCardAsync(c, ClosingDay, DueDay);
        var bankId = bank.GetProperty("link").GetProperty("financialAccountId").GetGuid();
        var cardId = card.GetProperty("link").GetProperty("creditCardId").GetGuid();
        await SetPayerAsync(c, cardId, bankId);
        await SyncAsync(c);
        return (c, cardId, bankId, card);
    }

    private async Task SetPayerAsync(Ctx c, Guid cardId, Guid accountId)
    {
        var card = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}");
        var response = await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}", c.Session, new
        {
            name = card.GetProperty("name").GetString(), issuer = card.GetProperty("issuer").GetString(), lastFourDigits = card.GetProperty("lastFourDigits").GetString(),
            creditLimit = card.GetProperty("creditLimit").GetString(), closingDay = card.GetProperty("closingDay").GetInt32(), dueDay = card.GetProperty("dueDay").GetInt32(),
            defaultPaymentAccountId = accountId,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<JsonElement> OverviewAsync(Ctx c, string competence) => JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/invoice-overview?competence={competence}");

    private Task<JsonElement> ProjectionAsync(Ctx c, int months = 6) => JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/obligation-projection?from={Today:yyyy-MM}&months={months}");

    private static JsonElement InvoiceOf(JsonElement overview, string competence) =>
        overview.GetProperty("cards").EnumerateArray().Single().GetProperty("invoices").EnumerateArray().Single(i => i.GetProperty("competence").GetString() == competence);

    private static JsonElement MonthOf(JsonElement projection, string month) => projection.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("month").GetString() == month);

    private static string Money(JsonElement element, string property) => element.GetProperty(property).GetString()!;

    // ---- 1, 4, 6: fatura fechada pelo banco, divergente e com pagamento parcial -------------------------------------------------

    [Fact]
    public async Task AClosedDivergentInvoiceShowsTheBankTotalAsTheObligationWithThreeIndependentDimensions()
    {
        var (c, _, _, _) = await ArrangeProjectionAsync("t94-closed", latestBankTotal: 500m, paid: 200m, withPending: false);

        var overview = await OverviewAsync(c, Competence(LatestClosing));
        var invoice = InvoiceOf(overview, Competence(LatestClosing));

        // Fechamento, pagamento e conciliação separados; o total do banco é a obrigação, a composição local só concilia.
        Assert.Equal(("Closed", "BankTotal", false), (invoice.GetProperty("cycle").GetString(), invoice.GetProperty("basis").GetString(), invoice.GetProperty("provisional").GetBoolean()));
        Assert.Equal(("500.00", "500.00", "200.00", "300.00"), (Money(invoice, "bankTotal"), Money(invoice, "obligation"), Money(invoice, "paid"), Money(invoice, "remaining")));
        Assert.Equal("Partial", Money(invoice, "paymentState"));
        Assert.Equal("230.00", Money(invoice.GetProperty("components"), "composition"));
        Assert.Equal("270.00", Money(invoice, "difference"));
        Assert.Contains(Money(invoice, "reconciliation"), new[] { "Divergent", "Incomplete" });
        var card = overview.GetProperty("cards").EnumerateArray().Single();
        Assert.Equal(("Pluggy", "Itaú corrente"), (card.GetProperty("origin").GetString(), card.GetProperty("payer").GetProperty("name").GetString()));
    }

    [Fact]
    public async Task APaidInvoiceIsPaidOnceAndAnUnpaidClosedOneKeepsItsFullObligation()
    {
        var (c, _, _, _) = await ArrangeProjectionAsync("t94-paid", latestBankTotal: 230m, paid: 230m, withPending: false);

        var paid = InvoiceOf(await OverviewAsync(c, Competence(LatestClosing)), Competence(LatestClosing));
        var older = InvoiceOf(await OverviewAsync(c, Competence(LatestClosing.AddMonths(-1))), Competence(LatestClosing.AddMonths(-1)));

        Assert.Equal(("Paid", "0.00", "230.00"), (Money(paid, "paymentState"), Money(paid, "remaining"), Money(paid, "obligation")));
        Assert.Equal(("100.00", "Unpaid"), (Money(older, "obligation"), Money(older, "paymentState")));
        await using var db = Db();
        Assert.Equal(1, await db.FinancialCreditCardInvoicePayments.CountAsync(p => p.Status == FinancialCreditCardInvoicePaymentStatus.Active));
    }

    // ---- 2, 3, 10, 11: aberta, futura e parcelas ----------------------------------------------------------------------------

    [Fact]
    public async Task OpenAndFutureInvoicesAreProvisionalAndUseKnownAndEstimatedInstallmentsWithoutBeingRecordedAsRealized()
    {
        var (c, cardId, _, _) = await ArrangeProjectionAsync("t94-open", latestBankTotal: 500m, paid: 0m, withPending: true);
        var movementsBefore = (await CoreAsync(c.Workspace)).Count;

        var overview = await OverviewAsync(c, Competence(NextClosing));
        var open = InvoiceOf(overview, Competence(NextClosing));
        var future = InvoiceOf(await OverviewAsync(c, Competence(NextClosing.AddMonths(1))), Competence(NextClosing.AddMonths(1)));
        var last = InvoiceOf(await OverviewAsync(c, Competence(NextClosing.AddMonths(2))), Competence(NextClosing.AddMonths(2)));

        Assert.Equal(("Open", "ProjectedOpen", true), (Money(open, "cycle"), Money(open, "basis"), open.GetProperty("provisional").GetBoolean()));
        Assert.Equal(("30.00", "100.00", "130.00"), (Money(open.GetProperty("components"), "pendingKnown"), Money(open.GetProperty("components"), "knownInstallments"), Money(open, "obligation")));
        Assert.Equal(("ProjectedFuture", "100.00", "0.00"), (Money(future, "basis"), Money(future.GetProperty("components"), "knownInstallments"), Money(future.GetProperty("components"), "estimated")));
        Assert.Equal(("100.00", "100.00"), (Money(last.GetProperty("components"), "estimated"), Money(last, "obligation"))); // 04/04 estimada com 3 parcelas de evidência mensal

        var installments = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}/projected-installments");
        var chain = installments.GetProperty("chains").EnumerateArray().Single(x => x.GetProperty("total").GetInt32() == 4);
        Assert.Equal(("Estimated", 1), (chain.GetProperty("status").GetString(), chain.GetProperty("estimated").GetInt32()));
        Assert.Equal(new[] { "InInvoice", "Known", "Known", "Estimated" }, chain.GetProperty("installments").EnumerateArray().Select(i => i.GetProperty("origin").GetString()!).ToArray());
        // Leitura apenas: nenhum lançamento, compra ou parcela foi criado pela projeção.
        Assert.Equal(movementsBefore, (await CoreAsync(c.Workspace)).Count);
        await using var db = Db();
        Assert.DoesNotContain(await db.FinancialCreditCardPurchases.AsNoTracking().Select(p => p.Description).ToListAsync(), d => d.Contains("SOFA 04", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheEstimatedInstallmentLaterArrivesItReplacesTheEstimateWithoutDuplicating()
    {
        var (c, cardId, _, _) = await ArrangeProjectionAsync("t94-recognize", latestBankTotal: 500m, paid: 0m, withPending: true);
        var last = Competence(NextClosing.AddMonths(2));
        Assert.Equal("100.00", Money(InvoiceOf(await OverviewAsync(c, last), last).GetProperty("components"), "estimated"));

        Pluggy.TransactionsByAccount[CardA] = [.. Pluggy.TransactionsByAccount[CardA], CardTx("s4", "SOFA 04/04", 100m, NextClosing.AddMonths(2).AddDays(-3), "", 4, 4, "PENDING")];
        await SyncAsync(c);

        var after = InvoiceOf(await OverviewAsync(c, last), last);
        Assert.Equal(("0.00", "100.00", "100.00"), (Money(after.GetProperty("components"), "estimated"), Money(after.GetProperty("components"), "knownInstallments"), Money(after, "obligation")));
        var chain = (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}/projected-installments")).GetProperty("chains").EnumerateArray().Single(x => x.GetProperty("total").GetInt32() == 4);
        Assert.Equal(("Complete", 0, 4), (chain.GetProperty("status").GetString(), chain.GetProperty("estimated").GetInt32(), chain.GetProperty("installments").GetArrayLength()));
    }

    // ---- 17, 18: projeção de seis meses ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSixMonthProjectionSeparatesConfirmedOpenFutureAndEstimatedAmountsPerCardAndPayer()
    {
        var (c, cardId, bankId, _) = await ArrangeProjectionAsync("t94-six", latestBankTotal: 500m, paid: 0m, withPending: true);

        var projection = await ProjectionAsync(c);

        Assert.Equal(6, projection.GetProperty("items").GetArrayLength());
        var open = MonthOf(projection, Competence(NextClosing));
        var future = MonthOf(projection, Competence(NextClosing.AddMonths(1)));
        var estimated = MonthOf(projection, Competence(NextClosing.AddMonths(2)));
        Assert.Equal(("130.00", "0.00", "130.00"), (Money(open, "openInvoicesProjected"), Money(open, "confirmedInvoices"), Money(open, "obligation")));
        Assert.Equal(("100.00", "0.00"), (Money(future, "futureInstallments"), Money(future, "estimatedCommitments")));
        Assert.Equal(("0.00", "100.00", "100.00"), (Money(estimated, "futureInstallments"), Money(estimated, "estimatedCommitments"), Money(estimated, "obligation")));
        // Por cartão e por conta pagadora: o cartão é do Itaú PF e a pagadora é a conta configurada.
        var byCard = open.GetProperty("byCard").EnumerateArray().Single();
        Assert.Equal((cardId, bankId, "130.00"), (byCard.GetProperty("cardId").GetGuid(), byCard.GetProperty("payerAccountId").GetGuid(), Money(byCard, "obligation")));
        var byPayer = open.GetProperty("byPayer").EnumerateArray().Single();
        Assert.Equal((bankId, "130.00", 1), (byPayer.GetProperty("accountId").GetGuid(), Money(byPayer, "remaining"), byPayer.GetProperty("cardIds").GetArrayLength()));
        // Dados incompletos aparecem como cobertura, não somem do total.
        Assert.Equal(open.GetProperty("coverage").GetProperty("complete").GetBoolean(), open.GetProperty("coverage").GetProperty("reasons").GetArrayLength() == 0);
        Assert.Equal(1, open.GetProperty("cards").GetInt32());
    }

    // ---- 19: Dashboard e Agenda sem duplicação ----------------------------------------------------------------------------------

    [Fact]
    public async Task AgendaAndAccountProjectionUseTheSameObligationsAsTheCardProjectionWithoutDoubleCounting()
    {
        var (c, _, bankId, _) = await ArrangeProjectionAsync("t94-agenda", latestBankTotal: 500m, paid: 200m, withPending: true);
        var projection = await ProjectionAsync(c);
        var through = NextClosing.AddMonths(4).AddDays(DueDay - ClosingDay);
        var expected = projection.GetProperty("items").EnumerateArray().Where(i => string.CompareOrdinal(i.GetProperty("month").GetString(), through.ToString("yyyy-MM")) <= 0)
            .Sum(i => decimal.Parse(Money(i, "remaining"), System.Globalization.CultureInfo.InvariantCulture))
            + decimal.Parse(Money(projection, "carryover"), System.Globalization.CultureInfo.InvariantCulture);

        var balances = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar/balances?from={Today:yyyy-MM-dd}&through={through:yyyy-MM-dd}");
        var account = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{bankId}/projection?through={through:yyyy-MM-dd}");

        // Fatura fechada vale 500 - 200 pagos = 300 (não a composição de 230); mais o provisório aberto e as parcelas futuras: uma vez só.
        Assert.Equal(expected, decimal.Parse(Money(balances, "creditCardOutstanding"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected, decimal.Parse(Money(account, "creditCardOutstanding"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(balances.GetProperty("creditCardProjectionComplete").GetBoolean());
        Assert.True(expected >= 300m);
    }

    // ---- 14, 20: idempotência e isolamento ------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadingTheProjectionIsIdempotentAndSyncingAgainChangesNothing()
    {
        var (c, _, _, _) = await ArrangeProjectionAsync("t94-idem", latestBankTotal: 500m, paid: 200m, withPending: true);
        var first = (await ProjectionAsync(c)).GetRawText();
        await using var db = Db();
        var counts = (await db.FinancialTransactions.CountAsync(), await db.FinancialCreditCardPurchases.CountAsync(), await db.FinancialCreditCardInvoices.CountAsync(), await db.FinancialCreditCardInvoicePayments.CountAsync());

        await SyncAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");

        Assert.Equal(first, (await ProjectionAsync(c)).GetRawText());
        Assert.Equal(first, (await ProjectionAsync(c)).GetRawText());
        Assert.Equal(counts, (await db.FinancialTransactions.CountAsync(), await db.FinancialCreditCardPurchases.CountAsync(), await db.FinancialCreditCardInvoices.CountAsync(), await db.FinancialCreditCardInvoicePayments.CountAsync()));
    }

    [Fact]
    public async Task AnotherWorkspaceNeverSeesTheCardsAndInvalidParametersAreRejected()
    {
        var (c, cardId, _, _) = await ArrangeProjectionAsync("t94-iso", latestBankTotal: 500m, paid: 0m, withPending: true);
        var stranger = await SignInAsync("t94-stranger");
        var strangerWorkspace = await CreateWorkspaceAsync(stranger);

        var mine = await Send(c.Client, HttpMethod.Get, $"/api/financial-workspaces/{strangerWorkspace.Id}/credit-cards/invoice-overview?competence={Today:yyyy-MM}", c.Session);
        var theirs = await JsonGetAsync(new Ctx(stranger, strangerWorkspace.Id, c.Client, Guid.Empty), $"/api/financial-workspaces/{strangerWorkspace.Id}/credit-cards/obligation-projection");
        var foreignCard = await Send(c.Client, HttpMethod.Get, $"/api/financial-workspaces/{strangerWorkspace.Id}/credit-cards/{cardId}/projected-installments", stranger);

        Assert.Equal(HttpStatusCode.NotFound, mine.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignCard.StatusCode);
        Assert.All(theirs.GetProperty("items").EnumerateArray(), m => Assert.Equal(("0.00", 0), (Money(m, "obligation"), m.GetProperty("cards").GetInt32())));
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/obligation-projection?months=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/obligation-projection?months=13")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/invoice-overview?competence=2026-13")).StatusCode);
    }

    // ---- 22: histórico incompleto ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutBankHistoryTheKnownAmountsStayVisibleAndTheCoverageSaysWhatIsMissing()
    {
        var c = await ArrangeAsync("t94-nohistory", seed: false);
        Pluggy.BillsByAccount[CardA] = [];
        Pluggy.TransactionsByAccount[CardA] = [CardTx("only", "UNICA", 77m, Today.AddDays(1), "", status: "PENDING")];
        var cardLink = await LinkCardAsync(c, ClosingDay, DueDay);
        await SyncAsync(c);

        var overview = await OverviewAsync(c, Competence(NextClosing));
        var card = overview.GetProperty("cards").EnumerateArray().Single();

        Assert.Equal("77.00", Money(InvoiceOf(overview, Competence(NextClosing)), "obligation"));
        var reasons = card.GetProperty("coverage").GetProperty("reasons").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("NoBankHistory", reasons);
        Assert.Contains("MissingPayerAccount", reasons);
        Assert.False(card.GetProperty("coverage").GetProperty("complete").GetBoolean());
        Assert.False(card.GetProperty("schedule").GetProperty("strong").GetBoolean());
        _ = cardLink;
    }
}

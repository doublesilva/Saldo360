using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Transactions;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// Devolução bancária: crédito confirmado como devolução de uma despesa (origem PluggyRefund + vínculo auditável). Entrada de caixa que soma no saldo uma vez, não
/// é receita, abate o consumo no mês do crédito e preserva a despesa original. API real sobre PostgreSQL descartável; cliente Pluggy simulado.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private const string MarketplacePay = "Pagamento de Pix QR Code Pix Marketplace";
    private const string MarketplaceBack = "Pix devolvido Pix Marketplace";

    private Task<HttpResponseMessage> RefundActionAsync(Ctx c, string action, Guid posting, Guid? original = null) =>
        PostAsync(c, $"{Integration(c.Workspace)}/review/{posting}/refund/{action}", original is null ? new { } : new { originalTransactionId = original });

    private async Task<Guid> TransactionIdOfAsync(string externalTransactionId)
    {
        await using var db = Db();
        return await (from posting in db.PluggyPostings join imported in db.PluggyImportedTransactions on posting.ImportedTransactionId equals imported.Id
                      where imported.PluggyTransactionId == externalTransactionId && posting.FinancialTransactionId != null select posting.FinancialTransactionId!.Value).SingleAsync();
    }

    private async Task<decimal> RefundsAsync(Ctx c) =>
        decimal.Parse((await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={D(60)}&through={D(0)}")).GetProperty("summary").GetProperty("realizedRefunds").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);

    private async Task<decimal> BalanceAsync(Ctx c, Guid account) =>
        decimal.Parse((await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{account}/balance")).GetProperty("balance").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private async Task SetOpeningBalanceAsync(Ctx c, Guid account)
    {
        var edit = await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/accounts/{account}", c.Session,
            new { name = "Itaú PF", type = "Checking", financialScope = "PF", isActive = true, openingBalance = 1000m, openingBalanceDate = D(60) });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
    }

    private async Task<IReadOnlyList<BudgetFact>> FactsAsync(Guid workspace)
    {
        await using var db = Db();
        return await Saldo360.Infrastructure.Budgets.BudgetFactReader.ReadAsync(db, workspace, Today.AddDays(-90), Today.AddDays(1), default);
    }

    /// <summary>PF com uma despesa (Pix QR Code) e créditos na mesma conta; os créditos descritos como devolução vão para revisão.</summary>
    private async Task<(Pair P, Guid Account)> ArrangeRefundAsync(string user, int expenseDaysAgo, decimal expense, params (string Id, int DaysAgo, decimal Amount, string Description)[] credits)
    {
        var transactions = new List<PluggyTransaction> { Tx("pay", BankA, expenseDaysAgo, -expense, operation: "PIX", description: MarketplacePay, category: "Shopping") };
        transactions.AddRange(credits.Select(x => Tx(x.Id, BankA, x.DaysAgo, x.Amount, operation: "PIX", description: x.Description, category: "Shopping")));
        var p = await ArrangePairAsync(user, [.. transactions], [], linkBusiness: false);
        await SyncAsync(p.C);
        await SetOpeningBalanceAsync(p.C, p.PersonalAccount);
        return (p, p.PersonalAccount);
    }

    [Fact]
    public async Task AFullRefundIncreasesTheBalanceOnceIsNotRevenueReducesTheConsumptionInTheMonthOfTheCreditAndKeepsTheOriginalExpense()
    {
        var (p, account) = await ArrangeRefundAsync("t-refund-full", 40, 55.99m, ("back", 2, 55.99m, MarketplaceBack));
        var c = p.C;
        var category = await CreateCategoryAsync(c, "Compras", "Expense");
        var originalId = await TransactionIdOfAsync("pay");
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/transactions/{originalId}/category", c.Session, new { categoryId = category })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/transactions/{originalId}/expense-nature", c.Session, new { expenseNature = "Fixed" })).StatusCode);
        Assert.Equal(944.01m, await BalanceAsync(c, account)); // a suspeita de devolução ainda não é saldo: o crédito está em revisão
        var review = await ReviewAsync(c);
        var item = review.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("Refund", "OutOfTotals"), (item.GetProperty("reviewKind").GetString(), item.GetProperty("impact").GetString()));
        var candidate = item.GetProperty("refundCandidates").EnumerateArray().Single(x => x.GetProperty("transactionId").GetGuid() == originalId);
        Assert.Equal(("55.99", true), (candidate.GetProperty("refundable").GetString(), candidate.GetProperty("canConfirm").GetBoolean()));
        var creditPosting = await PostingIdAsync("back");

        var confirm = await RefundActionAsync(c, "confirm", creditPosting, originalId);

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal("Done", (await JsonOf(confirm)).GetProperty("outcome").GetString());
        await using var db = Db();
        var refund = await db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Origin == FinancialTransactionOrigin.PluggyRefund);
        Assert.Equal((CategoryType.Income, 55.99m, false, account), (refund.Type, refund.Amount, refund.IsCancelled, refund.AccountId));
        var original = await db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == originalId);
        Assert.Equal((55.99m, CategoryType.Expense, false, category), (original.Amount, original.Type, original.IsCancelled, original.CategoryId)); // valor, data e histórico preservados
        Assert.Equal(Today.AddDays(-40), original.TransactionDate);
        var link = await db.FinancialBankRefunds.AsNoTracking().SingleAsync();
        Assert.Equal((refund.Id, originalId, 55.99m, true), (link.RefundTransactionId, link.OriginalTransactionId, link.Amount, link.IsActive));
        Assert.NotEqual(Guid.Empty, link.ConfirmedByUserId);
        Assert.Equal(PluggyPostingKind.BankRefund, (await db.PluggyPostings.AsNoTracking().SingleAsync(x => x.Id == creditPosting)).Kind);
        Assert.Equal(1, await db.PluggyImportedTransactions.CountAsync(t => t.PluggyTransactionId == "back")); // a evidência importada permanece

        Assert.Equal(1000m, await BalanceAsync(c, account)); // o crédito soma UMA vez no saldo: 1000 − 55,99 + 55,99
        var realized = await RealizedAsync(c);
        Assert.Equal((0m, 0m, 55.99m), (realized.Income, realized.Expense, await RefundsAsync(c))); // fora das receitas, abatida da despesa
        var facts = await FactsAsync(c.Workspace);
        var refundFact = facts.Single(f => f.Origin == BudgetFactOrigin.BankRefund);
        Assert.Equal((-55.99m, category, ExpenseNature.Fixed, originalId, Today.AddDays(-2)), (refundFact.Amount, refundFact.CategoryId, refundFact.Nature, refundFact.RelatedRecordId, refundFact.Date));
        var expenseFact = facts.Single(f => f.Origin == BudgetFactOrigin.Transaction);
        Assert.Equal((55.99m, Today.AddDays(-40)), (expenseFact.Amount, expenseFact.Date));
        Assert.Equal(1, facts.Count(f => f.Origin == BudgetFactOrigin.BankRefund)); // um único fato negativo
        // O consumo do mês do gasto (anterior) não é alterado; o do mês do crédito é reduzido.
        var spendMonth = new DateOnly(expenseFact.Date.Year, expenseFact.Date.Month, 1); var creditMonth = new DateOnly(refundFact.Date.Year, refundFact.Date.Month, 1);
        Assert.NotEqual(spendMonth, creditMonth);
        Assert.Equal(55.99m, facts.Where(f => new DateOnly(f.Date.Year, f.Date.Month, 1) == spendMonth).Sum(f => f.Amount));
        Assert.Equal(-55.99m, facts.Where(f => new DateOnly(f.Date.Year, f.Date.Month, 1) == creditMonth).Sum(f => f.Amount));

        // A lista de lançamentos mostra o crédito (não o esconde por deixar de ser receita) com o vínculo; a despesa informa o total devolvido.
        var list = (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/transactions?page=1&pageSize=50")).GetProperty("items").EnumerateArray().ToList();
        var listedRefund = list.Single(i => i.GetProperty("origin").GetString() == "PluggyRefund");
        Assert.Equal(originalId, listedRefund.GetProperty("refundOf").GetProperty("originalTransactionId").GetGuid());
        Assert.Equal("55.99", list.Single(i => i.GetProperty("id").GetGuid() == originalId).GetProperty("refundedAmount").GetString());

        // Sincronização e reprocessamento posteriores não duplicam nada.
        await SyncAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");
        Assert.Equal(1, await db.FinancialTransactions.CountAsync(t => t.Origin == FinancialTransactionOrigin.PluggyRefund && !t.IsCancelled));
        Assert.Equal(1, await db.FinancialBankRefunds.CountAsync(l => l.CancelledAt == null));
        Assert.Equal(1000m, await BalanceAsync(c, account));
        Assert.Equal(1, (await FactsAsync(c.Workspace)).Count(f => f.Origin == BudgetFactOrigin.BankRefund));
    }

    [Fact]
    public async Task PartialAndMultipleRefundsAreAllowedUpToTheOriginalAmountAndAnyExcessIsRefused()
    {
        var (p, _) = await ArrangeRefundAsync("t-refund-multi", 40, 100m, ("r1", 6, 30m, MarketplaceBack), ("r2", 5, 50m, MarketplaceBack), ("r3", 4, 20m, MarketplaceBack), ("r4", 3, 1m, MarketplaceBack));
        var c = p.C; var originalId = await TransactionIdOfAsync("pay");

        foreach (var id in new[] { "r1", "r2", "r3" })
            Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(c, "confirm", await PostingIdAsync(id), originalId)).StatusCode);

        var fourth = await RefundActionAsync(c, "confirm", await PostingIdAsync("r4"), originalId);
        Assert.Equal(HttpStatusCode.BadRequest, fourth.StatusCode);
        Assert.Contains("ExceedsRefundable", await fourth.Content.ReadAsStringAsync());
        await using var db = Db();
        Assert.Equal(100m, await db.FinancialBankRefunds.Where(l => l.CancelledAt == null).SumAsync(l => l.Amount)); // nunca excede o valor elegível
        var facts = await FactsAsync(c.Workspace);
        Assert.Equal(3, facts.Count(f => f.Origin == BudgetFactOrigin.BankRefund));
        Assert.Equal(0m, facts.Sum(f => f.Amount)); // consumo líquido da despesa devolvida por inteiro
        // O excedente que ficou em revisão continua fora dos totais (não vira receita) e a despesa já não oferece saldo devolvível.
        var review = await ReviewAsync(c);
        var rest = review.GetProperty("items").EnumerateArray().Single();
        var candidate = rest.GetProperty("refundCandidates").EnumerateArray().SingleOrDefault(x => x.GetProperty("transactionId").GetGuid() == originalId);
        Assert.Equal(JsonValueKind.Undefined, candidate.ValueKind);
        Assert.Equal((0m, 0m), await RealizedAsync(c));
    }

    [Fact]
    public async Task ACreditLargerThanTheRefundableBalanceIsOfferedAsBlockedAndRefusedOnConfirmation()
    {
        var (p, _) = await ArrangeRefundAsync("t-refund-excess", 40, 100m, ("big", 3, 120m, MarketplaceBack), ("part", 4, 40m, MarketplaceBack));
        var c = p.C; var originalId = await TransactionIdOfAsync("pay");
        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(c, "confirm", await PostingIdAsync("part"), originalId)).StatusCode);

        var review = await ReviewAsync(c);
        var big = review.GetProperty("items").EnumerateArray().Single();
        var candidate = big.GetProperty("refundCandidates").EnumerateArray().Single(x => x.GetProperty("transactionId").GetGuid() == originalId);
        Assert.Equal(("60.00", false, "ExceedsRefundable"), (candidate.GetProperty("refundable").GetString(), candidate.GetProperty("canConfirm").GetBoolean(), candidate.GetProperty("blockedReason").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await RefundActionAsync(c, "confirm", await PostingIdAsync("big"), originalId)).StatusCode);
        await using var db = Db();
        Assert.Equal(1, await db.FinancialBankRefunds.CountAsync());
    }

    [Fact]
    public async Task RepeatingAConfirmationIsIdempotentAndACreditRefundsOnlyOneExpense()
    {
        var (p, account) = await ArrangeRefundAsync("t-refund-idem", 40, 80m, ("back", 3, 30m, MarketplaceBack));
        var c = p.C; var originalId = await TransactionIdOfAsync("pay");
        var posting = await PostingIdAsync("back");
        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(c, "confirm", posting, originalId)).StatusCode);
        var balance = await BalanceAsync(c, account);

        var again = await RefundActionAsync(c, "confirm", posting, originalId);

        Assert.Equal("AlreadyDone", (await JsonOf(again)).GetProperty("outcome").GetString());
        await using var db = Db();
        Assert.Equal((1, 1), (await db.FinancialBankRefunds.CountAsync(), await db.FinancialTransactions.CountAsync(t => t.Origin == FinancialTransactionOrigin.PluggyRefund)));
        Assert.Equal(balance, await BalanceAsync(c, account));
        // Outra despesa para o mesmo crédito: conflito (desfazer antes).
        var other = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await RefundActionAsync(c, "confirm", posting, other)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentConfirmationsNeverExceedTheRefundableBalance()
    {
        var (p, _) = await ArrangeRefundAsync("t-refund-race", 40, 100m, ("a", 5, 60m, MarketplaceBack), ("b", 4, 60m, MarketplaceBack));
        var c = p.C; var originalId = await TransactionIdOfAsync("pay");

        var results = await Task.WhenAll(RefundActionAsync(c, "confirm", await PostingIdAsync("a"), originalId), RefundActionAsync(c, "confirm", await PostingIdAsync("b"), originalId));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.BadRequest], results.Select(r => r.StatusCode).Order());
        await using var db = Db();
        Assert.Equal(60m, await db.FinancialBankRefunds.Where(l => l.CancelledAt == null).SumAsync(l => l.Amount));
        Assert.Equal(1, await db.FinancialTransactions.CountAsync(t => t.Origin == FinancialTransactionOrigin.PluggyRefund && !t.IsCancelled));
    }

    [Fact]
    public async Task ACreditAlreadyRegisteredAsIncomeIsReclassifiedAtomicallyWithoutDuplicatingTheBalanceAndTheImportedEvidenceIsKept()
    {
        var (p, account) = await ArrangeRefundAsync("t-refund-legacy", 12, 55.99m, ("back", 2, 55.99m, "Pix recebido Pix Marketplace"));
        var c = p.C;
        Assert.Equal((55.99m, 55.99m), await RealizedAsync(c)); // estado herdado: a devolução entrou como receita
        Assert.Equal(1000m, await BalanceAsync(c, account));
        // O banco passa a descrever o crédito como devolução; o efeito aplicado é preservado, mas a suspeita fica sinalizada na revisão.
        Pluggy.TransactionsByAccount[BankA] = [.. Pluggy.TransactionsByAccount[BankA].Select(t => t.Id == "back" ? t with { Description = MarketplaceBack, DescriptionRaw = "PIX DEVOLVIDO" } : t)];
        await SyncAsync(c);
        Assert.Equal((55.99m, 55.99m), await RealizedAsync(c));
        var flagged = (await ReviewAsync(c)).GetProperty("items").EnumerateArray().Single();
        Assert.Equal((true, "InTotals", "Refund"), (flagged.GetProperty("accounted").GetBoolean(), flagged.GetProperty("impact").GetString(), flagged.GetProperty("reviewKind").GetString()));
        var originalId = await TransactionIdOfAsync("pay");
        var posting = await PostingIdAsync("back");

        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(c, "confirm", posting, originalId)).StatusCode);

        Assert.Equal((0m, 0m), await RealizedAsync(c)); // saiu das receitas e abateu a despesa
        Assert.Equal(1000m, await BalanceAsync(c, account)); // exatamente um efeito positivo no saldo
        await using var db = Db();
        Assert.Equal(1, await db.FinancialTransactions.CountAsync(t => t.IsCancelled && t.Type == CategoryType.Income)); // a receita substituída fica no histórico
        Assert.Equal(1, await db.FinancialTransactions.CountAsync(t => !t.IsCancelled && t.Origin == FinancialTransactionOrigin.PluggyRefund));
        Assert.Equal(2, await db.PluggyImportedTransactions.CountAsync()); // evidência importada preservada
        Assert.Empty(Reasons(await ReviewAsync(c)));
        await SyncAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");
        Assert.Equal((0m, 0m), await RealizedAsync(c));
        Assert.Equal(1000m, await BalanceAsync(c, account));
        Assert.Equal(1, await db.FinancialBankRefunds.CountAsync());
    }

    [Fact]
    public async Task UndoingARefundRemovesTheConsumptionReductionKeepsTheRealCreditAndNeverRecreatesTheLink()
    {
        var (p, account) = await ArrangeRefundAsync("t-refund-undo", 12, 55.99m, ("back", 2, 55.99m, "Pix recebido Pix Marketplace"));
        var c = p.C; var originalId = await TransactionIdOfAsync("pay"); var posting = await PostingIdAsync("back");
        // Sem descrição de devolução o crédito é receita comum; o usuário ainda assim pode vinculá-lo (decisão explícita dele).
        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(c, "confirm", posting, originalId)).StatusCode);
        Assert.Equal((0m, 0m), await RealizedAsync(c));

        var undo = await RefundActionAsync(c, "undo", posting);

        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(1000m, await BalanceAsync(c, account)); // o dinheiro real continua exatamente uma vez no saldo
        Assert.Equal((55.99m, 55.99m), await RealizedAsync(c)); // volta à interpretação anterior (receita comum)
        Assert.DoesNotContain(await FactsAsync(c.Workspace), f => f.Origin == BudgetFactOrigin.BankRefund); // sem redução de consumo
        await using var db = Db();
        Assert.Equal((0, 1), (await db.FinancialBankRefunds.CountAsync(l => l.CancelledAt == null), await db.FinancialBankRefunds.CountAsync(l => l.CancelledAt != null)));
        await SyncAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");
        Assert.Equal(0, await db.FinancialBankRefunds.CountAsync(l => l.CancelledAt == null)); // o motor nunca recria o vínculo sozinho
        Assert.Equal(1000m, await BalanceAsync(c, account));
        Assert.Equal(HttpStatusCode.BadRequest, (await RefundActionAsync(c, "undo", posting)).StatusCode); // já não é uma devolução

    }

    [Fact]
    public async Task UndoingARefundWhoseDescriptionStillSuggestsAReturnSendsTheCreditToExplicitDecisionAndNeverDeletesIt()
    {
        var (p, _) = await ArrangeRefundAsync("t-refund-undo-hint", 12, 20m, ("back", 2, 20m, MarketplaceBack));
        var original = await TransactionIdOfAsync("pay"); var posting = await PostingIdAsync("back");
        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(p.C, "confirm", posting, original)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(p.C, "undo", posting)).StatusCode);

        Assert.Equal(["PossibleRefundByDescription"], Reasons(await ReviewAsync(p.C)));
        await using var db = Db();
        Assert.Equal(1, await db.PluggyImportedTransactions.CountAsync(t => t.PluggyTransactionId == "back"));
        Assert.Equal((0, 1), (await db.FinancialBankRefunds.CountAsync(l => l.CancelledAt == null), await db.FinancialBankRefunds.CountAsync(l => l.CancelledAt != null)));
    }

    [Fact]
    public async Task ARefundCreditCannotBeATransferAndATransferCreditCannotBeARefund()
    {
        var p = await ArrangePairAsync("t-refund-exclusive", [Tx("pay", BankA, 12, -300m, operation: "PIX", description: MarketplacePay, category: "Shopping"),
            Tx("pf-in", BankA, 4, 300m, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX")],
            [Tx("pj-out", BankB, 4, -300m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")]);
        await SyncBothAsync(p);
        Assert.Equal(1, await ActiveTransfersAsync());
        var originalId = await TransactionIdOfAsync("pay");

        var refused = await RefundActionAsync(p.C, "confirm", await PostingIdAsync("pf-in"), originalId);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        // E o contrário: depois de virar devolução, o crédito não pode ser ponta de transferência.
        await LinkActionAsync(p.C, "undo", await PostingIdAsync("pf-in"));
        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(p.C, "confirm", await PostingIdAsync("pf-in"), originalId)).StatusCode);
        var transfer = await LinkActionAsync(p.C, "confirm", await PostingIdAsync("pj-out"), await PostingIdAsync("pf-in"));
        Assert.Equal(HttpStatusCode.Conflict, transfer.StatusCode);
        Assert.Equal(0, await ActiveTransfersAsync());
    }

    [Fact]
    public async Task ARefundIsRefusedForAnotherWorkspaceAnExpenseAfterTheCreditAndANonConsumptionExpense()
    {
        var (p, _) = await ArrangeRefundAsync("t-refund-invalid", 12, 50m, ("back", 2, 50m, MarketplaceBack), ("early", 20, 10m, MarketplaceBack));
        var c = p.C; var originalId = await TransactionIdOfAsync("pay");
        var other = await SignInAsync("t-refund-invalid-other");
        var otherWorkspace = await CreateWorkspaceAsync(other);
        var intruder = new Ctx(other, otherWorkspace.Id, c.Client, c.Connection);

        Assert.Equal(HttpStatusCode.NotFound, (await RefundActionAsync(intruder, "confirm", await PostingIdAsync("back"), originalId)).StatusCode);
        // Despesa posterior ao crédito (a despesa "pay" é de 12 dias atrás; o crédito "early" é de 20 dias atrás).
        var late = await RefundActionAsync(c, "confirm", await PostingIdAsync("early"), originalId);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Contains("OriginalAfterCredit", await late.Content.ReadAsStringAsync());
        // A própria devolução não é despesa devolvível.
        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(c, "confirm", await PostingIdAsync("back"), originalId)).StatusCode);
        var refundTx = await TransactionIdOfAsync("back");
        var chained = await RefundActionAsync(c, "confirm", await PostingIdAsync("early"), refundTx);
        Assert.Equal(HttpStatusCode.BadRequest, chained.StatusCode);
        await using var db = Db();
        Assert.Equal(1, await db.FinancialBankRefunds.CountAsync());
    }

    [Fact]
    public async Task ASourceChangeOfAConfirmedRefundCancelsTheLinkAndReevaluatesTheCreditInsteadOfKeepingAStaleReduction()
    {
        var (p, account) = await ArrangeRefundAsync("t-refund-change", 12, 55.99m, ("back", 2, 55.99m, MarketplaceBack));
        var c = p.C; var originalId = await TransactionIdOfAsync("pay");
        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(c, "confirm", await PostingIdAsync("back"), originalId)).StatusCode);

        Pluggy.TransactionsByAccount[BankA] = [.. Pluggy.TransactionsByAccount[BankA].Select(t => t.Id == "back" ? t with { Amount = 50m } : t)];
        await SyncAsync(c);

        await using var db = Db();
        Assert.Equal(0, await db.FinancialBankRefunds.CountAsync(l => l.CancelledAt == null));
        Assert.Equal(0, await db.FinancialTransactions.CountAsync(t => t.Origin == FinancialTransactionOrigin.PluggyRefund && !t.IsCancelled));
        Assert.DoesNotContain(await FactsAsync(c.Workspace), f => f.Origin == BudgetFactOrigin.BankRefund);
        Assert.Equal(["PossibleRefundByDescription"], Reasons(await ReviewAsync(c))); // o crédito de 50 volta a ser uma suspeita para o usuário decidir
        Assert.Equal(944.01m, await BalanceAsync(c, account));
    }

    [Fact]
    public async Task TheMonthlyProjectionAndTheAccountBalanceAgreeWithTheRefundRule()
    {
        var (p, account) = await ArrangeRefundAsync("t-refund-agenda", 3, 55.99m, ("back", 2, 55.99m, MarketplaceBack));
        var c = p.C; var originalId = await TransactionIdOfAsync("pay");
        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(c, "confirm", await PostingIdAsync("back"), originalId)).StatusCode);

        var projection = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar/monthly-projection?from={Today:yyyy-MM}&months=1&accountId={account}");
        var month = projection.GetProperty("months")[0];

        // Se o gasto e a devolução caírem no mesmo mês: receita 0 e despesa líquida 0; o saldo reflete as duas pontas de caixa.
        if (Today.AddDays(-3).Month == Today.Month)
            Assert.Equal(("0.00", "0.00"), (month.GetProperty("realizedIncome").GetString(), month.GetProperty("realizedExpense").GetString()));
        Assert.Equal(1000m, await BalanceAsync(c, account));
        var day = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar/balances?from={Today:yyyy-MM-dd}&through={Today.AddDays(1):yyyy-MM-dd}&accountId={account}");
        Assert.Equal("1000.00", day.GetProperty("openingBalance").GetString());
    }
}

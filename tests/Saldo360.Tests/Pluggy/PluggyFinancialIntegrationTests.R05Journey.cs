using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// R05: jornadas integradas da sincronização bancária (simulada) até a apresentação financeira — contas, lançamentos, Agenda, orçamento, Visão Geral e
/// automação —, sempre pela API real sobre PostgreSQL real. Dados sintéticos; nenhuma chamada externa.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private static string R05Month(DateOnly from) => $"from={from.AddDays(-40):yyyy-MM-dd}&through={Today:yyyy-MM-dd}";
    private static decimal Dec(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private Task<JsonElement> R05OverviewAsync(Ctx c, string competence) => JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-dashboard/overview?scope=all&competence={competence}");

    private async Task<JsonElement> R05PutAsync(Ctx c, string url, object body)
    {
        var response = await Send(c.Client, HttpMethod.Put, url, c.Session, body);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await JsonOf(response);
    }

    // ---- Cenários A e B: sincronização, repetição, transferência interna, classificação manual ---------------------------------------------

    [Fact]
    public async Task AB_SyncIsIdempotentTransfersDoNotInflateTotalsAndAManualClassificationSurvivesNewSyncs()
    {
        var c = await ArrangeAsync("r05-ab", seed: false);
        var itemB = await LinkAsync(c.Client, c.Session, c.Workspace, ItemB, "Nubank");
        Pluggy.TransactionsByAccount[BankA] =
        [
            Tx("sal", BankA, 8, 3000m, operation: "OUTROS", description: "SALARIO EMPRESA", category: "Salary"),
            Tx("mer", BankA, 6, -120.5m, operation: "CARTAO", description: "Mercado Bairro", category: "Groceries"),
            Tx("zeta", BankA, 4, -15m, operation: "CARTAO", description: "Loja Zeta", category: "Mystery Category"),
            Tx("out", BankA, 5, -50m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "PARA NUBANK", category: "Transfers"),
        ];
        Pluggy.TransactionsByAccount[BankB] = [Tx("in", BankB, 5, 50m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "DE ITAU", category: "Transfers")];
        await LinkBankAsync(c);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = itemB, pluggyAccountId = BankB, mode = "CreateNew", name = "Nubank", financialScope = "PF" })).StatusCode);

        // A: sincroniza, repete e confirma que nada duplica; a transferência interna não entra em receitas nem despesas.
        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Root(c.Workspace)}/connections/{itemB}/sync")).StatusCode);
        var firstCount = (await CoreAsync(c.Workspace)).Count;
        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Root(c.Workspace)}/connections/{itemB}/sync")).StatusCode);
        var core = await CoreAsync(c.Workspace);
        Assert.Equal((3, firstCount), (core.Count, core.Count));
        await using (var db = Db())
        {
            Assert.Equal(1, await db.FinancialTransfers.CountAsync(t => !t.IsCancelled));
            Assert.Equal(5, await db.PluggyImportedTransactions.CountAsync(t => t.WorkspaceId == c.Workspace)); // 4 do Itaú + 1 do Nubank, uma só vez
        }
        var calendar = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?{R05Month(Today)}");
        Assert.Equal(("3000.00", "135.50"), (calendar.GetProperty("summary").GetProperty("realizedIncome").GetString(), calendar.GetProperty("summary").GetProperty("realizedExpense").GetString()));

        // B: categoriza só localmente; o que não tem categoria segura vira pendência com a mesma contagem na Visão Geral e na lista de revisão.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Integration(c.Workspace)}/categorization/run")).StatusCode);
        var competence = Today.ToString("yyyy-MM");
        var pendingOverview = (await R05OverviewAsync(c, competence)).GetProperty("classification");
        var review = await JsonGetAsync(c, $"{Integration(c.Workspace)}/classification-review?kind=category");
        Assert.Equal(3, review.GetProperty("total").GetInt32()); // sem categoria local correspondente nada é criado: salário, mercado e "Loja Zeta" ficam em revisão
        Assert.Equal(review.GetProperty("total").GetInt32(), pendingOverview.GetProperty("needsReview").GetInt32());
        var zeta = review.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("description").GetString() == "Loja Zeta");
        Assert.Equal("Loja Zeta", zeta.GetProperty("description").GetString());
        var recordId = zeta.GetProperty("recordId").GetGuid();
        var food = await CreateCategoryAsync(c, "Mercado e lojas", "Expense");

        // O usuário corrige categoria e tipo; a nova sincronização (com o valor mudado no banco) e a categorização forçada preservam as duas escolhas.
        await R05PutAsync(c, $"/api/financial-workspaces/{c.Workspace}/transactions/{recordId}/category", new { categoryId = food });
        await R05PutAsync(c, $"/api/financial-workspaces/{c.Workspace}/transactions/{recordId}/expense-nature", new { expenseNature = "Fixed" });
        Pluggy.TransactionsByAccount[BankA] = [.. Pluggy.TransactionsByAccount[BankA].Select(t => t.Id == "zeta" ? t with { Amount = -16m } : t)];
        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Integration(c.Workspace)}/categorization/run", new { force = true })).StatusCode);

        var kept = (await CoreAsync(c.Workspace)).Single(t => t.Id == recordId);
        Assert.Equal((16m, food, ExpenseNature.Fixed), (kept.Amount, kept.CategoryId, kept.Nature)); // o valor segue o banco; a classificação é do usuário
        Assert.Equal(3, (await CoreAsync(c.Workspace)).Count);
        var after = (await R05OverviewAsync(c, competence)).GetProperty("classification");
        Assert.Equal(2, after.GetProperty("needsReview").GetInt32()); // o atalho reflete a decisão; salário e mercado seguem em revisão (sem categoria local)
        Assert.Equal(2, (await JsonGetAsync(c, $"{Integration(c.Workspace)}/classification-review?kind=category")).GetProperty("total").GetInt32());
    }

    // ---- Cenários C e D: despesa fixa no cartão em três perspectivas, compra parcelada e pagamento da fatura ---------------------------------

    [Fact]
    public async Task CD_AnImportedFixedCardExpenseCountsInCategoryFixedAndAllCardsWithoutBecomingThreeExpensesAndTheInvoicePaymentNeverRaisesConsumption()
    {
        var (c, cardId, bankId, _) = await ArrangeProjectionAsync("r05-cd", latestBankTotal: 500m, paid: 0m, withPending: false);
        var competence = Competence(LatestClosing);
        var root = $"/api/financial-workspaces/{c.Workspace}";
        var category = await CreateCategoryAsync(c, "Compras do cartão", "Expense");

        // O usuário classifica a compra importada "LOJA": categoria e tipo fixo, só desta compra.
        Guid purchaseId;
        await using (var db = Db()) purchaseId = await db.FinancialCreditCardPurchases.Where(p => p.WorkspaceId == c.Workspace && p.Description == "LOJA").Select(p => p.Id).SingleAsync();
        await R05PutAsync(c, $"{root}/credit-cards/{cardId}/purchases/{purchaseId}/category", new { categoryId = category });
        await R05PutAsync(c, $"{root}/credit-cards/{cardId}/purchases/{purchaseId}/expense-nature", new { expenseNature = "Fixed" });
        await SyncAsync(c); // a sincronização seguinte não desfaz nenhuma das duas escolhas

        var budgetResponse = await Send(c.Client, HttpMethod.Post, $"{root}/budgets", c.Session, new { competence });
        Assert.Equal(HttpStatusCode.Created, budgetResponse.StatusCode);
        var budget = (await JsonOf(budgetResponse)).GetProperty("id").GetGuid();
        async Task<Guid> Target(object body, string grouping)
        {
            var created = await Send(c.Client, HttpMethod.Post, $"{root}/budgets/{budget}/targets", c.Session, body);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            return (await JsonOf(created)).GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("groupingType").GetString() == grouping && !t.GetProperty("isArchived").GetBoolean()).GetProperty("id").GetGuid();
        }
        var cardsTarget = await Target(new { groupingType = "AllCards", plannedAmount = "5000.00" }, "AllCards");
        var fixedTarget = await Target(new { groupingType = "FixedExpenses", plannedAmount = "600.00" }, "FixedExpenses");
        var categoryTarget = await Target(new { name = "Compras do cartão", groupingType = "Category", categoryId = category, plannedAmount = "1000.00" }, "Category");

        async Task<JsonElement> Run(Guid target) => (await JsonGetAsync(c, $"{root}/budgets/{budget}/execution")).GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("targetId").GetGuid() == target);
        var fixedRun = await Run(fixedTarget);
        var categoryRun = await Run(categoryTarget);
        var cardsRun = await Run(cardsTarget);
        // Mesma despesa nas três perspectivas: 150,00 nas fixas e na categoria; "Todos os cartões" a contém uma única vez junto das demais compras da fatura.
        Assert.Equal(("150.00", "150.00"), (fixedRun.GetProperty("consumed").GetString(), categoryRun.GetProperty("consumed").GetString()));
        var cardsComposition = await JsonGetAsync(c, $"{root}/budgets/{budget}/targets/{cardsTarget}/composition?page=1&pageSize=50");
        Assert.Single(cardsComposition.GetProperty("items").EnumerateArray(), i => i.GetProperty("description").GetString() == "LOJA");
        Assert.True(Dec(cardsRun.GetProperty("consumed").GetString()!) > 150m);
        var summary = (await JsonGetAsync(c, $"{root}/budgets/{budget}/execution")).GetProperty("categories");
        Assert.Equal(("150.00", 1), (summary.GetProperty("consumed").GetString(), summary.GetProperty("targetCount").GetInt32())); // perspectivas não são somadas num total global
        await using (var db = Db()) Assert.Equal(1, await db.FinancialCreditCardPurchases.CountAsync(p => p.WorkspaceId == c.Workspace && p.Description == "LOJA"));

        // Compra parcelada: só a parcela da competência entra no consumo da fatura (as demais não são a mesma despesa de novo).
        Assert.Equal(0, cardsComposition.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("description").GetString()!.StartsWith("SOFA", StringComparison.Ordinal) && i.GetProperty("description").GetString()!.Contains("(2/", StringComparison.Ordinal)));

        // Pagamento: antes, a fatura é obrigação de caixa inteira; depois, o restante cai e o consumo do orçamento NÃO muda.
        var overviewBefore = await R05OverviewAsync(c, competence);
        var invoiceBefore = InvoiceOf(await OverviewAsync(c, competence), competence);
        Assert.Equal(("500.00", "0.00", "500.00"), (Money(invoiceBefore, "obligation"), Money(invoiceBefore, "paid"), Money(invoiceBefore, "remaining")));
        var cardsConsumedBefore = cardsRun.GetProperty("consumed").GetString();
        var cashObligationBefore = R05CashObligation(overviewBefore);

        Pluggy.BillsByAccount[CardA] = [.. Pluggy.BillsByAccount[CardA].Select(b => b.Id == "bill-0" ? TotalBill("bill-0", LatestClosing, 500m, [(200m, Today)]) : b)];
        Pluggy.TransactionsByAccount[BankA] = [Tx("pay", BankA, 0, -200m, operation: "BOLETO", description: "PAGAMENTO", category: "Credit card payment") with { Date = DateTimeOffset.Parse(Today.ToString("yyyy-MM-dd") + "T12:00:00Z") }];
        await SyncAsync(c);
        await SyncAsync(c); // repetir a sincronização não duplica a compra nem o pagamento

        var invoiceAfter = InvoiceOf(await OverviewAsync(c, competence), competence);
        Assert.Equal(("500.00", "200.00", "300.00", "Partial"), (Money(invoiceAfter, "obligation"), Money(invoiceAfter, "paid"), Money(invoiceAfter, "remaining"), Money(invoiceAfter, "paymentState")));
        var cardsRunAfter = await Run(cardsTarget);
        Assert.Equal(cardsConsumedBefore, cardsRunAfter.GetProperty("consumed").GetString()); // pagamento não é consumo
        Assert.Equal("150.00", (await Run(fixedTarget)).GetProperty("consumed").GetString());
        var overviewAfter = await R05OverviewAsync(c, competence);
        Assert.Equal(200m, cashObligationBefore - R05CashObligation(overviewAfter)); // o dashboard mostra só o restante a pagar: 200 a menos, sem duplicar
        await using (var db = Db())
        {
            var payment = await db.FinancialCreditCardInvoicePayments.AsNoTracking().SingleAsync(p => p.Status == Saldo360.Domain.CreditCards.FinancialCreditCardInvoicePaymentStatus.Active);
            // A saída bancária acontece uma vez só: um lançamento de 200,00 na conta pagadora, ligado ao pagamento da fatura.
            Assert.Equal(1, await db.FinancialTransactions.CountAsync(t => t.Id == payment.TransactionId && t.AccountId == bankId && t.Amount == 200m && !t.IsCancelled));
            Assert.Equal(1, await db.FinancialCreditCardPurchases.CountAsync(p => p.WorkspaceId == c.Workspace && p.Description == "LOJA"));
        }
    }

    /// <summary>O que o dashboard mostra ainda a pagar das faturas: as próximas (por vencimento) mais as já vencidas.</summary>
    private static decimal R05CashObligation(JsonElement overview)
    {
        var payments = overview.GetProperty("commitments").GetProperty("cardPayments");
        Assert.Equal("Complete", payments.GetProperty("state").GetString());
        return Dec(payments.GetProperty("total").GetString()!) + Dec(payments.GetProperty("overdueRemaining").GetString()!);
    }

    // ---- Cenário G: falhas e dados incompletos ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task G_IncompleteDataIsExplainedAndNeverBecomesAConfirmedZero()
    {
        var c = await ArrangeAsync("r05-g", seed: false);
        var root = $"/api/financial-workspaces/{c.Workspace}";
        Pluggy.TransactionsByAccount[BankA] = [Tx("g1", BankA, 5, -30m, operation: "CARTAO", description: "Loja sem tipo", category: "Mystery Category")];
        await LinkBankAsync(c);
        // Sincronização com falha parcial na conta: concluída com pendências no histórico, nada marcado como ausente.
        Pluggy.Failures[$"transactions:{BankA}"] = new PluggyException(PluggyFailureKind.Unavailable, 503);
        var partial = await JsonOf(await PostAsync(c, $"{Root(c.Workspace)}/connections/{c.Connection}/sync"));
        Assert.Equal("CompletedWithErrors", partial.GetProperty("status").GetString());
        var history = await JsonGetAsync(c, $"{Integration(c.Workspace)}/automation/runs");
        var run = history.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "Sync");
        Assert.Equal("CompletedWithPending", run.GetProperty("state").GetString());
        Assert.True(run.GetProperty("sync").GetProperty("errors").GetInt32() > 0);
        Pluggy.Failures.Clear();
        await SyncAsync(c);

        // IA desligada: pedir a IA não é um erro do servidor e a categorização local segue disponível.
        var ai = await PostAsync(c, $"{Integration(c.Workspace)}/categorization/run", new { useAi = true, confirmExternal = true });
        Assert.Equal(HttpStatusCode.OK, ai.StatusCode);
        Assert.Equal("AiUnavailable", (await JsonOf(ai)).GetProperty("aiStoppedBecause").GetString()); // só a etapa local foi feita, e o motivo é dito
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Integration(c.Workspace)}/categorization/run")).StatusCode);
        var automation = await JsonGetAsync(c, $"{Integration(c.Workspace)}/automation");
        Assert.False(automation.GetProperty("server").GetProperty("ai").GetProperty("available").GetBoolean());
        Assert.Equal("AiUnavailable", automation.GetProperty("categorization").GetProperty("state").GetString());

        // Natureza indefinida e competência estimada ficam sinalizadas na meta de fixas, sem inflá-la; saldo indisponível nunca vira zero.
        var competence = Today.ToString("yyyy-MM");
        var budget = (await JsonOf(await Send(c.Client, HttpMethod.Post, $"{root}/budgets", c.Session, new { competence }))).GetProperty("id").GetGuid();
        var created = await JsonOf(await Send(c.Client, HttpMethod.Post, $"{root}/budgets/{budget}/targets", c.Session, new { groupingType = "FixedExpenses", plannedAmount = "600.00" }));
        var target = created.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("groupingType").GetString() == "FixedExpenses").GetProperty("id").GetGuid();
        var run2 = (await JsonGetAsync(c, $"{root}/budgets/{budget}/execution")).GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("targetId").GetGuid() == target);
        Assert.Equal("0.00", run2.GetProperty("consumed").GetString());
        Assert.True(run2.GetProperty("coverage").GetProperty("undefinedNatureCount").GetInt32() >= 1); // despesa sem tipo: pendência explícita, fora das fixas
        var accounts = (await R05OverviewAsync(c, competence)).GetProperty("accounts").GetProperty("items").EnumerateArray().ToList();
        Assert.All(accounts, a =>
        {
            if (!a.GetProperty("balanceAvailable").GetBoolean()) { Assert.Equal(JsonValueKind.Null, a.GetProperty("balance").ValueKind); Assert.NotEqual(JsonValueKind.Null, a.GetProperty("unavailableReason").ValueKind); }
        });
    }
}

using System.Globalization;
using System.Net;
using System.Text.Json;
using Saldo360.Domain.Common;
using Saldo360.Domain.Debts;
using Saldo360.Tests.Support;
using Xunit.Abstractions;

namespace Saldo360.Tests.Api;

/// <summary>
/// T13.2 (Fase B, somente leitura): conciliação do Razão Financeiro (financial-entries) com o orçamento PF existente (<c>pfBudgetControl</c>) para a MESMA competência e escopo PF.
/// Não substitui nenhum cálculo: classifica cada linha nos componentes do orçamento (realizado, comprometido, estimado; cartões × fora dos cartões), exclui o que não é consumo
/// (transferências, pagamentos de fatura/dívida, receitas, cancelados, PJ) e relata, por componente, a diferença e a origem que a explica.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialEntriesReconciliationTests(PostgresFixture postgres, ITestOutputHelper output) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month => ThisMonth.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _pf, _pj, _expense, _income, _card;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task<Guid> PostAsync(string path, object body)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/{path}", _session, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{path}: {await response.Content.ReadAsStringAsync()}");
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task ArrangeAsync(string user)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        var opening = ThisMonth.AddMonths(-1);
        _pf = await PostAsync("accounts", new { name = "Conta PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "9000.00", openingBalanceDate = opening });
        _pj = await PostAsync("accounts", new { name = "Conta PJ", type = "Checking", financialScope = "PJ", openingBalance = "9000.00", openingBalanceDate = opening });
        _expense = await PostAsync("categories", new { name = "Despesas", type = "Expense" });
        _income = await PostAsync("categories", new { name = "Receitas", type = "Income" });
        _card = await PostAsync("credit-cards", new { name = "Nubank", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _pf });
        Task<Guid> Tx(Guid account, Guid category, string type, string description, string amount) =>
            PostAsync("transactions", new { accountId = account, categoryId = category, type, description, amount, transactionDate = Today });
        await Tx(_pf, _expense, "Expense", "Mercado PF", "100.00");
        await Tx(_pf, _income, "Income", "Salário", "500.00");
        await Tx(_pj, _expense, "Expense", "Despesa PJ", "40.00");
        await PostAsync("planned-transactions", new { accountId = _pf, categoryId = _expense, type = "Expense", amount = "200.00", dueDate = ThisMonth.AddMonths(1).AddDays(-1), description = "Aluguel previsto" });
        await PostAsync("planned-transfers", new { sourceAccountId = _pf, destinationAccountId = _pj, amount = 500m, dueDate = ThisMonth.AddMonths(1).AddDays(-2), description = "Repasse" });
        await PostAsync("transfers", new { sourceAccountId = _pf, destinationAccountId = _pj, amount = 50m, transferDate = Today, description = "Transferência feita" });
        await PostAsync("recurrences", new { accountId = _pf, categoryId = _expense, type = "Expense", description = "Internet", amount = "120.00", isEstimated = false, frequency = "Monthly", interval = 1, startDate = ThisMonth.AddDays(27), dayOfMonth = 28 });
        await PostAsync($"credit-cards/{_card}/purchases", new { description = "Mercado no cartão", purchaseDate = ThisMonth, totalAmount = "300.00", installmentCount = 1, categoryId = _expense, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth, expenseNature = (string?)null });
        await PostAsync($"credit-cards/{_card}/purchases", new { description = "Geladeira", purchaseDate = ThisMonth, totalAmount = "300.00", installmentCount = 3, categoryId = _expense, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth, expenseNature = (string?)null });
        await PostAsync("recurrences", new { creditCardId = _card, categoryId = _expense, type = "Expense", description = "Streaming", amount = "39.90", isEstimated = false, frequency = "Monthly", interval = 1, startDate = ThisMonth.AddDays(1), dayOfMonth = 2 });
        var debt = await PostAsync("debts", new { name = "Financiamento", debtType = "Other", responsibleAccountId = _pf, originalPrincipal = "5000.00", contractDate = ThisMonth });
        await using var db = Db();
        db.FinancialDebtInstallments.Add(FinancialDebtInstallment.Create(_workspace.Id, debt, new(1, ThisMonth.AddDays(20), 1000m, null, null, null, null), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> JsonAsync(string url)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Root}/{url}", _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static decimal Dec(JsonElement element, string name) => decimal.Parse(element.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    /// <summary>Componentes do orçamento calculados a partir das linhas do Razão (PF). Cada linha cai em exatamente um balde, ou numa exclusão nomeada.</summary>
    private sealed record Components(decimal Realized, decimal Committed, decimal Estimated, decimal CardsRealized, decimal CardsCommitted, decimal CardsEstimated, IReadOnlyDictionary<string, decimal> Excluded);

    private static Components Classify(IEnumerable<JsonElement> items)
    {
        decimal realized = 0, committed = 0, estimated = 0, cardsRealized = 0, cardsCommitted = 0, cardsEstimated = 0;
        var excluded = new Dictionary<string, decimal>();
        void Exclude(string reason, decimal amount) => excluded[reason] = excluded.GetValueOrDefault(reason) + amount;
        foreach (var e in items)
        {
            var amount = Dec(e, "amount"); var type = Str(e, "type"); var source = Str(e, "source"); var origin = Str(e, "origin"); var status = Str(e, "status");
            if (type == "Transfer") { Exclude("Transferência", amount); continue; }
            if (type == "Payment") { Exclude("Pagamento de fatura/dívida (caixa)", amount); continue; }
            if (type == "Income" && Str(e, "budgetImpact") != "Reduces") { Exclude("Receita", amount); continue; }
            var signed = Str(e, "budgetImpact") == "Reduces" ? -amount : amount;
            switch (source)
            {
                case "Transaction": realized += signed; break;
                case "CardPurchase" or "CardAdjustment": cardsRealized += signed; break;
                case "CardProjected" when origin is "EstimatedInstallment" or "Recurring" or "ManualRecurring": cardsEstimated += signed; break;
                case "CardProjected": cardsCommitted += signed; break;
                case "Planned" or "Recurrence" or "Debt": if (status is "Pending" or "Overdue" or "NeedsReview") committed += signed; else realized += signed; break;
                default: Exclude("Não classificado: " + source, amount); break;
            }
        }
        return new(realized, committed, estimated, cardsRealized, cardsCommitted, cardsEstimated, excluded);
    }

    [Fact]
    public async Task TheLedgerComponentsReconcileWithThePfBudgetAndEveryDifferenceIsExplained()
    {
        await ArrangeAsync("recon-pf");
        var pfScope = await JsonAsync($"financial-entries?competence={Month}&scope=pf&pageSize=100");
        var items = pfScope.GetProperty("items").EnumerateArray().ToList();
        var pf = (await JsonAsync($"financial-dashboard/overview?competence={Month}&scope=all")).GetProperty("pfBudgetControl");
        var total = pf.GetProperty("total"); var cards = pf.GetProperty("cards"); var outside = pf.GetProperty("outsideCards");
        var ledger = Classify(items);

        var report = new List<string> { $"Competência {Month} · escopo PF · {items.Count} linhas no Razão" };
        void Line(string component, decimal ledgerValue, decimal budgetValue) =>
            report.Add($"{component,-26} Razão {ledgerValue,10:0.00} | Orçamento {budgetValue,10:0.00} | diferença {ledgerValue - budgetValue,10:0.00}");
        Line("Cartões realizado", ledger.CardsRealized, Dec(cards, "realized"));
        Line("Cartões comprometido", ledger.CardsCommitted, Dec(cards, "committed"));
        Line("Cartões estimado", ledger.CardsEstimated, Dec(cards, "estimated"));
        Line("Fora dos cartões realizado", ledger.Realized, Dec(outside, "realized"));
        Line("Fora dos cartões comprom.", ledger.Committed, Dec(outside, "committed"));
        Line("Total projetado", ledger.Realized + ledger.Committed + ledger.CardsRealized + ledger.CardsCommitted + ledger.CardsEstimated, Dec(total, "projected"));
        foreach (var (reason, amount) in ledger.Excluded.OrderBy(x => x.Key)) report.Add($"Excluído do orçamento: {reason} = {amount:0.00}");
        output.WriteLine(string.Join(Environment.NewLine, report));
        // Diferença explicável: assinaturas cadastradas à mão num cartão aparecem no Razão (calculadas pela fatura) mas o orçamento PF ainda não as considera; só os detectados pelo Pluggy entram no motor do cartão.
        var manualSubscriptions = items.Where(e => Str(e, "origin") == "ManualRecurring").Sum(e => Dec(e, "amount"));
        report.Add($"Explicado: assinaturas manuais de cartão ainda fora do orçamento PF = {manualSubscriptions:0.00}");
        var unexplained = ledger.CardsEstimated - Dec(cards, "estimated") - manualSubscriptions;
        report.Add($"Diferença não explicada em cartões estimados = {unexplained:0.00}");
        output.WriteLine(string.Join(Environment.NewLine, report));

        Assert.Equal((Dec(cards, "realized"), Dec(cards, "committed")), (ledger.CardsRealized, ledger.CardsCommitted));
        Assert.Equal((Dec(outside, "realized"), Dec(outside, "committed")), (ledger.Realized, ledger.Committed));
        Assert.True(unexplained == 0m, string.Join(Environment.NewLine, report));
        Assert.Equal(["Receita", "Transferência"], ledger.Excluded.Keys.Order(StringComparer.Ordinal));   // nada é consumo orçamentário além dos baldes acima
        Assert.Equal(items.Count, items.Select(e => Str(e, "id")).Distinct().Count());
    }
}

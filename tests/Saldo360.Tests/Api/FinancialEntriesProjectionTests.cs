using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Domain.Debts;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// T13.2: competências futuras do Lançamentos pela API real (Postgres): parcelas de cartão, assinatura de cartão, recorrência bancária, financiamento longo e transferência prevista,
/// cada movimento lógico UMA vez e nada materializado pela consulta.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Transactions")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialEntriesProjectionTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static DateOnly Target => ThisMonth.AddMonths(2);
    private const int LoanInstallments = 400;

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _pf, _pj, _debt;
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
        _pf = await PostAsync("accounts", new { name = "Conta PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "5000.00", openingBalanceDate = opening });
        _pj = await PostAsync("accounts", new { name = "Conta PJ", type = "Checking", financialScope = "PJ", openingBalance = "5000.00", openingBalanceDate = opening });
        var expense = await PostAsync("categories", new { name = "Despesas", type = "Expense" });
        var income = await PostAsync("categories", new { name = "Receitas", type = "Income" });
        var card = await PostAsync("credit-cards", new { name = "Nubank", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _pf });
        // Compra parcelada em 3 vezes a partir deste mês: a 3ª parcela cai na competência-alvo (fatura que vence daqui a 2 meses).
        await PostAsync($"credit-cards/{card}/purchases", new { description = "Geladeira", purchaseDate = ThisMonth, totalAmount = "300.00", installmentCount = 3, categoryId = expense, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth, expenseNature = (string?)null });
        await PostAsync("recurrences", new { creditCardId = card, categoryId = expense, type = "Expense", description = "Streaming", amount = "39.90", isEstimated = false, frequency = "Monthly", interval = 1, startDate = ThisMonth.AddDays(1), dayOfMonth = 2 });
        await PostAsync("recurrences", new { accountId = _pf, categoryId = income, type = "Income", description = "Salário", amount = "7000.00", isEstimated = false, frequency = "Monthly", interval = 1, startDate = ThisMonth.AddDays(4), dayOfMonth = 5 });
        await PostAsync("planned-transfers", new { sourceAccountId = _pf, destinationAccountId = _pj, amount = 500m, dueDate = Target.AddDays(9), description = "Repasse" });
        _debt = await PostAsync("debts", new { name = "Financiamento", debtType = "Other", responsibleAccountId = _pf, originalPrincipal = "400000.00", contractDate = ThisMonth });
        await using var db = Db();
        for (var number = 1; number <= LoanInstallments; number++)
            db.FinancialDebtInstallments.Add(FinancialDebtInstallment.Create(_workspace.Id, _debt, new(number, ThisMonth.AddMonths(number - 1).AddDays(14), 1000m, null, null, null, null), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<List<JsonElement>> ItemsAsync(DateOnly competence, string query = "")
    {
        var response = await SendAsync(HttpMethod.Get, $"{Root}/financial-entries?competence={competence:yyyy-MM}&pageSize=100{query}", _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task AFutureCompetenceShowsEachInstallmentSubscriptionRecurrenceDebtAndTransferExactlyOnceAndWritesNothing()
    {
        await ArrangeAsync("entries-futuro");
        await using var before = Db();
        var counts = (await before.FinancialDebtInstallments.CountAsync(), await before.FinancialPlannedTransactions.CountAsync(), await before.FinancialRecurrenceMaterializations.CountAsync(), await before.FinancialCreditCardPurchases.CountAsync());

        var items = await ItemsAsync(Target);

        Assert.Equal(items.Count, items.Select(e => Str(e, "id")).Distinct().Count());
        var bySource = items.GroupBy(e => Str(e, "source")).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Single(bySource["Debt"]);                       // 1 das 400 parcelas do financiamento: só a da competência consultada
        Assert.Single(bySource["Recurrence"]);                 // salário projetado (virtual)
        Assert.Single(bySource["Transfer"]);                   // transferência prevista, uma linha
        Assert.Equal(["Geladeira", "Streaming"], items.Where(e => Str(e, "source") is "CardPurchase" or "CardProjected").Select(e => Str(e, "description")).Select(d => d.Split(" (")[0]).Order(StringComparer.Ordinal));
        var subscription = items.Single(e => Str(e, "source") == "CardProjected");
        Assert.Equal(("ManualRecurring", "CardForecast", "Pending", Target.ToString("yyyy-MM")), (Str(subscription, "origin"), Str(subscription, "stage"), Str(subscription, "status"), Str(subscription, "competence")));
        Assert.Contains("Committed", subscription.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        var installment = items.Single(e => Str(e, "source") == "CardPurchase");
        Assert.Equal(("CardLaunched", "100.00"), (Str(installment, "stage"), Str(installment, "amount")));   // compra lançada no cartão; nada diz que a fatura foi paga

        await using var after = Db();
        Assert.Equal(counts, (await after.FinancialDebtInstallments.CountAsync(), await after.FinancialPlannedTransactions.CountAsync(), await after.FinancialRecurrenceMaterializations.CountAsync(), await after.FinancialCreditCardPurchases.CountAsync()));
    }

    [Fact]
    public async Task ADistantCompetenceProjectsRecurrencesWithoutMaterializingAndTheLongLoanContributesOneInstallment()
    {
        await ArrangeAsync("entries-distante");
        var distant = ThisMonth.AddMonths(30);
        await using var before = Db();
        var planned = await before.FinancialPlannedTransactions.CountAsync();

        var items = await ItemsAsync(distant);

        Assert.Equal(new[] { "CardProjected", "Debt", "Recurrence" }, items.Select(e => Str(e, "source")).Order(StringComparer.Ordinal));
        Assert.Contains("parcela 31", Str(items.Single(e => Str(e, "source") == "Debt"), "description"));
        await using var after = Db();
        Assert.Equal(planned, await after.FinancialPlannedTransactions.CountAsync());
        Assert.Equal(LoanInstallments, await after.FinancialDebtInstallments.CountAsync());
    }

    [Fact]
    public async Task MaterializingTheSubscriptionReplacesItsProjectionWithTheRegisteredCardPurchase()
    {
        await ArrangeAsync("entries-materializa");
        var recurrence = (await ReadAsync<List<JsonElement>>(await SendAsync(HttpMethod.Get, $"{Root}/recurrences", _session))).Single(r => r.GetProperty("description").GetString() == "Streaming").GetProperty("id").GetGuid();
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/recurrences/{recurrence}/materialize", _session, new { from = Target.AddDays(1), through = Target.AddDays(2) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var streaming = (await ItemsAsync(Target)).Where(e => Str(e, "description").StartsWith("Streaming", StringComparison.Ordinal)).ToList();

        Assert.Equal(("CardPurchase", "CardLaunched"), (Str(Assert.Single(streaming), "source"), Str(streaming[0], "stage")));
    }
}

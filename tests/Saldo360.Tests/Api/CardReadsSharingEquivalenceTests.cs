using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Leituras de cartões e faturas do cálculo PF: o caminho atual (cada conta PF lê tudo de novo) e o agrupado (uma leitura por requisição, recorte por conta em memória) precisam entregar
/// os MESMOS valores, estados e motivos de cobertura em <c>budget-summary</c> e <c>pfBudgetControl</c>. PostgreSQL real; os dois caminhos leem o mesmo banco.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CardReadsSharingEquivalenceTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month => ThisMonth.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private readonly List<Guid> _accounts = [];
    private Guid _expense;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task ArrangeAsync(string user, int pfAccounts)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        _expense = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Despesas", type = "Expense" }))).Id;
        for (var i = 0; i < pfAccounts; i++)
            _accounts.Add((await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
                new { name = $"PF {i + 1}", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = ThisMonth.AddMonths(-1), overdraftLimit = "1000.00" }))).Id);
        await using var db = Db();
        db.FinancialMonthlyAggregateTargets.Add(FinancialMonthlyAggregateTarget.Create(_workspace.Id, ThisMonth, MonthlyAggregateTargetType.TotalExpenses, 20000m, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task ExpenseAsync(Guid account, string amount, string description = "Efetivo") =>
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transactions", _session,
            new { accountId = account, categoryId = _expense, type = "Expense", description, amount, transactionDate = Today })).StatusCode);

    private async Task<Guid> CardAsync(string name, Guid? payer)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards", _session, new { name, issuer = "Banco", creditLimit = "10000.00", closingDay = 27, dueDay = 28, defaultPaymentAccountId = payer });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task PurchaseAsync(Guid card, string amount, int installments = 1)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards/{card}/purchases", _session, new
        {
            description = "Compra", purchaseDate = ThisMonth, totalAmount = amount, installmentCount = installments, categoryId = _expense, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = ThisMonth, expenseNature = (string?)null,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task PayInvoiceAsync(Guid card, Guid fromAccount, string amount)
    {
        Guid invoice;
        await using (var db = Db())
        {
            invoice = (await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.CreditCardId == card).OrderBy(i => i.ClosingDate).FirstAsync()).Id;
            await OfficialInvoiceFixture.CloseAsync(db, invoice); // só fatura fechada recebe pagamento
        }
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards/{card}/invoices/{invoice}/payments", _session,
            new { operationId = Guid.NewGuid(), financialAccountId = fromAccount, paymentDate = Today, amount });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task DeactivateAsync(Guid account, string name) =>
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{Root}/accounts/{account}", _session,
            new { name, type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = ThisMonth.AddMonths(-1), isActive = false })).StatusCode);

    private sealed record Run(string Summary, string PfControl, (int Database, int Reused) Reads);

    /// <summary>Lê o resumo PF e o overview completo num escopo de requisição próprio, com o reaproveitamento ligado ou desligado.</summary>
    private async Task<Run> ReadBothAsync(bool shared)
    {
        using var scope = Factory.Services.CreateScope();
        var sharing = scope.ServiceProvider.GetRequiredService<ICardFactsSharing>();
        sharing.Enabled = shared;
        var service = scope.ServiceProvider.GetRequiredService<DashboardOverviewService>();
        var summary = await service.GetBudgetSummaryAsync(_workspace.Id, _workspace.OwnerUserId, Month, CancellationToken.None);
        var afterSummary = sharing.Reads;
        var overview = await service.GetAsync(_workspace.Id, _workspace.OwnerUserId, Month, "all", null, CancellationToken.None);
        return new(JsonSerializer.Serialize(summary.Value), JsonSerializer.Serialize(overview.Value!.PfBudgetControl), afterSummary);
    }

    private async Task<(Run Current, Run Grouped)> CompareAsync()
    {
        var current = await ReadBothAsync(false);
        var grouped = await ReadBothAsync(true);
        Assert.Equal((0, 0), current.Reads);
        Assert.Equal(current.Summary, grouped.Summary);
        Assert.Equal(current.PfControl, grouped.PfControl);
        // Totais, estado e motivos de cobertura (notas) do resumo coincidem com os do pfBudgetControl.
        using var summary = JsonDocument.Parse(grouped.Summary); using var pf = JsonDocument.Parse(grouped.PfControl);
        Assert.Equal(pf.RootElement.GetProperty("State").GetString(), summary.RootElement.GetProperty("State").GetString());
        Assert.Equal(pf.RootElement.GetProperty("Total").GetRawText(), summary.RootElement.GetProperty("Total").GetRawText());
        Assert.Equal(pf.RootElement.GetProperty("Notes").GetRawText(), summary.RootElement.GetProperty("Notes").GetRawText());
        return (current, grouped);
    }

    [Fact]
    public async Task SeveralPfAccountsWithCardsReadTheCardFactsOnceAndKeepTheSameTotals()
    {
        await ArrangeAsync("cr-varias", 3);
        for (var i = 0; i < 3; i++) await ExpenseAsync(_accounts[i], $"{100 + i * 50}.00");
        await PurchaseAsync(await CardAsync("Cartão 1", _accounts[0]), "400.00");
        await PurchaseAsync(await CardAsync("Cartão 2", _accounts[1]), "900.00", 3);
        await PurchaseAsync(await CardAsync("Cartão 3", _accounts[2]), "250.00");

        var (_, grouped) = await CompareAsync();

        Assert.Equal(1, grouped.Reads.Database); // todas as contas PF do resumo usam a mesma leitura dos cartões
        Assert.True(grouped.Reads.Reused >= 3, $"reaproveitadas: {grouped.Reads.Reused}");
    }

    [Fact]
    public async Task APaidInvoiceKeepsTheSameConsumptionAndTotalsInBothPaths()
    {
        await ArrangeAsync("cr-paga", 2);
        var card = await CardAsync("Cartão pago", _accounts[0]); await PurchaseAsync(card, "400.00");
        var before = await CompareAsync();
        await PayInvoiceAsync(card, _accounts[0], "400.00");
        var after = await CompareAsync();

        using var beforeTotal = JsonDocument.Parse(before.Grouped.Summary); using var afterTotal = JsonDocument.Parse(after.Grouped.Summary);
        // Pagar a fatura não cria consumo novo: o consumo do cartão continua só nas compras (nunca duplicado pelo pagamento).
        Assert.Equal(beforeTotal.RootElement.GetProperty("Total").GetProperty("Realized").GetRawText(), afterTotal.RootElement.GetProperty("Total").GetProperty("Realized").GetRawText());
        Assert.Equal(beforeTotal.RootElement.GetProperty("Total").GetProperty("Projected").GetRawText(), afterTotal.RootElement.GetProperty("Total").GetProperty("Projected").GetRawText());
    }

    [Fact]
    public async Task ACardWithoutAPayerKeepsThePartialCoverageAndItsReasonInBothPaths()
    {
        await ArrangeAsync("cr-sem-pagador", 2);
        await PurchaseAsync(await CardAsync("Com pagador", _accounts[0]), "300.00");
        await PurchaseAsync(await CardAsync("Sem pagador", null), "500.00");

        var (_, grouped) = await CompareAsync();

        using var summary = JsonDocument.Parse(grouped.Summary);
        Assert.Equal("Partial", summary.RootElement.GetProperty("State").GetString());
        Assert.Contains("MissingPayerAccount", grouped.PfControl);
    }

    [Fact]
    public async Task AnInactiveAccountWithHistoryStillCountsAndTheTotalsMatchInBothPaths()
    {
        await ArrangeAsync("cr-inativa", 3);
        await ExpenseAsync(_accounts[2], "321.00", "Histórico");
        await PurchaseAsync(await CardAsync("Cartão da inativa", _accounts[2]), "180.00");
        await PurchaseAsync(await CardAsync("Cartão ativo", _accounts[0]), "220.00");
        var active = await CompareAsync();
        await DeactivateAsync(_accounts[2], "PF 3");
        var inactive = await CompareAsync();

        using var before = JsonDocument.Parse(active.Grouped.Summary); using var after = JsonDocument.Parse(inactive.Grouped.Summary);
        // A conta inativa com histórico continua no orçamento PF: o realizado não perde os 321,00 nem o cartão dela.
        Assert.Equal(before.RootElement.GetProperty("Total").GetProperty("Realized").GetRawText(), after.RootElement.GetProperty("Total").GetProperty("Realized").GetRawText());
    }

    [Fact]
    public async Task DisablingTheSharingRestoresOneReadPerCall()
    {
        await ArrangeAsync("cr-desligado", 2);
        await PurchaseAsync(await CardAsync("Cartão", _accounts[0]), "100.00");

        Assert.Equal((0, 0), (await ReadBothAsync(false)).Reads);
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

// T14.1: a recuperação do cheque especial é calculada sobre a CONTA-ALVO. Ajustes do cenário só a alteram quando atribuídos a ela; receita na conta PJ não recupera o limite PF sem uma
// transferência prevista; transferências são neutras no consolidado; o cenário nunca grava lançamentos nem altera a meta oficial.
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Scenarios")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialPlanningRecoveryTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static readonly DateOnly Today = BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static readonly DateOnly First = new DateOnly(Today.Year, Today.Month, 1).AddMonths(1);       // primeira competência futura: tudo é previsto
    private string _session = null!;
    private Guid _workspace, _pf, _pj, _income, _expense;

    private async Task ArrangeAsync(string user, string pfOpening = "-1000.00", string pjOpening = "5000.00")
    {
        _session = await SignInAsync(user);
        _workspace = (await CreateWorkspaceAsync(_session, "Família Recuperação")).Id;
        var opening = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1);
        _pf = await CreateAsync("accounts", new { name = "Itaú PF", type = "Checking", financialScope = "PF", openingBalance = pfOpening, openingBalanceDate = opening, overdraftLimit = "2000.00" });
        _pj = await CreateAsync("accounts", new { name = "Itaú PJ", type = "Checking", financialScope = "PJ", openingBalance = pjOpening, openingBalanceDate = opening });
        _income = await CreateAsync("categories", new { name = "Salário", type = "Income" });
        _expense = await CreateAsync("categories", new { name = "Aluguel", type = "Expense" });
    }

    private async Task<Guid> CreateAsync(string path, object body)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_workspace}/{path}", _session, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private Task PlannedAsync(Guid account, string type, string amount, DateOnly due) =>
        CreateAsync("planned-transactions", new { accountId = account, categoryId = type == "Income" ? _income : _expense, type, description = type == "Income" ? "Receita prevista" : "Despesa prevista", amount, dueDate = due });

    private Task TransferAsync(string amount, DateOnly due) =>
        CreateAsync("planned-transfers", new { sourceAccountId = _pj, destinationAccountId = _pf, amount = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), dueDate = due, description = "Repasse PJ → PF" });

    private async Task<Guid> ScenarioAsync(int months = 3, bool withTarget = true)
    {
        var id = await CreateAsync("planning-scenarios", new { name = "Recuperação", startMonth = First.ToString("yyyy-MM"), months, recoveryAccountId = withTarget ? _pf : (Guid?)null });
        return id;
    }

    private async Task ParameterAsync(Guid scenario, int offset, string income, string expense, Guid? account, string percentage = "80.00")
    {
        var response = await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{_workspace}/planning-scenarios/{scenario}/parameters/{First.AddMonths(offset):yyyy-MM}", _session,
            new { expectedAdditionalIncome = income, expectedAdditionalExpense = expense, contingencyReserve = "0.00", recoveryTargetPercentage = percentage, adjustmentAccountId = account });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<JsonElement> ProjectionAsync(Guid scenario, int month)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{_workspace}/planning-scenarios/{scenario}/projection", _session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items")[month].Clone();
    }

    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task WithoutAdjustmentsTheRecoveryFollowsTheAccountsOwnPlannedFlow()
    {
        await ArrangeAsync("t141-base");
        await PlannedAsync(_pf, "Income", "1500.00", First.AddDays(4));
        await PlannedAsync(_pf, "Expense", "400.00", First.AddDays(9));
        var month = await ProjectionAsync(await ScenarioAsync(), 0);

        Assert.Equal(("1000.00", "0.00", "2000.00"), (S(month, "openingNegativeBalance"), S(month, "closingNegativeBalance"), S(month, "contractedLimit")));
        Assert.Equal("1000.00", S(month, "projectedRecovery"));
        Assert.Equal("Recovered", S(month, "recoveryStatus"));
        Assert.Equal("100.00", S(month, "targetClosingBalance"));
        Assert.Equal("1500.00", S(month, "targetPendingIncome"));
        Assert.Equal("0.00", S(month, "targetAdjustment"));
        Assert.Equal(S(month, "projectedRecovery"), S(month, "baseProjectedRecovery"));
    }

    [Fact]
    public async Task AdditionalIncomeAssignedToThePfAccountRecalculatesTheRecoveryAndTheOverdraftUse()
    {
        await ArrangeAsync("t141-pf");
        await PlannedAsync(_pf, "Income", "300.00", First.AddDays(4));
        var scenario = await ScenarioAsync();
        await ParameterAsync(scenario, 0, "500.00", "0.00", _pf);
        var month = await ProjectionAsync(scenario, 0);

        Assert.Equal(("1000.00", "200.00", "800.00"), (S(month, "openingNegativeBalance"), S(month, "closingNegativeBalance"), S(month, "projectedRecovery")));
        Assert.Equal("700.00", S(month, "baseClosingNegativeBalance"));
        Assert.Equal("300.00", S(month, "baseProjectedRecovery"));
        Assert.Equal(("500.00", "500.00", "0.00"), (S(month, "targetAdjustment"), S(month, "targetCumulativeAdjustment"), S(month, "unattributedAdjustment")));
        Assert.Equal("Recovering", S(month, "recoveryStatus"));
        Assert.Equal("BaseProjection", S(month, "recoveryTargetSource"));
        Assert.Equal("240.00", S(month, "recoveryTarget"));             // meta = 80% da capacidade da projeção-base (300), não da simulação
        Assert.Equal("560.00", S(month, "recoveryDeviation"));
    }

    [Fact]
    public async Task AdditionalIncomeOnThePjAccountDoesNotRecoverThePfLimitWithoutATransfer()
    {
        await ArrangeAsync("t141-pj");
        var scenario = await ScenarioAsync();
        await ParameterAsync(scenario, 0, "2000.00", "0.00", _pj);
        var month = await ProjectionAsync(scenario, 0);

        Assert.Equal(("1000.00", "0.00", "2000.00"), (S(month, "closingNegativeBalance"), S(month, "projectedRecovery"), S(month, "unattributedAdjustment")));
        Assert.Equal("Stable", S(month, "recoveryStatus"));
        Assert.Equal("2000.00", S(month, "cumulativeAdjustment"));       // o consolidado enxerga a receita; a conta PF não
        Assert.Equal("0.00", S(month, "targetCumulativeAdjustment"));
    }

    [Fact]
    public async Task APlannedPjToPfTransferReducesThePfOverdraftAndIsNeutralInTheConsolidated()
    {
        await ArrangeAsync("t141-transfer");
        var scenario = await ScenarioAsync();
        var before = await ProjectionAsync(scenario, 0);
        await TransferAsync("600.00", First.AddDays(6));
        var after = await ProjectionAsync(scenario, 0);

        Assert.Equal("1000.00", S(before, "closingNegativeBalance"));
        Assert.Equal(("400.00", "600.00"), (S(after, "closingNegativeBalance"), S(after, "projectedRecovery")));
        Assert.Equal(S(before, "baseClosingBalance"), S(after, "baseClosingBalance"));              // consolidado: transferência não cria receita nem despesa
        await using var db = Db();
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());                            // nada real foi gravado pela simulação
    }

    [Fact]
    public async Task AnExtraordinaryExpenseOnTheTargetAccountWorsensTheOverdraftAcrossMonths()
    {
        await ArrangeAsync("t141-worse");
        var scenario = await ScenarioAsync(months: 2);
        await ParameterAsync(scenario, 0, "0.00", "300.00", _pf);
        await ParameterAsync(scenario, 1, "0.00", "200.00", _pf);
        var first = await ProjectionAsync(scenario, 0); var second = await ProjectionAsync(scenario, 1);

        Assert.Equal(("1300.00", "Worsening", "0.00"), (S(first, "closingNegativeBalance"), S(first, "recoveryStatus"), S(first, "projectedRecovery")));
        Assert.Equal(("1300.00", "1500.00", "-500.00", "Worsening"), (S(second, "openingNegativeBalance"), S(second, "closingNegativeBalance"), S(second, "targetCumulativeAdjustment"), S(second, "recoveryStatus")));
    }

    [Fact]
    public async Task ANegativeBalanceThatTurnsPositiveAndTheFullRecoveryAreReportedAsRecovered()
    {
        await ArrangeAsync("t141-recovered");
        var scenario = await ScenarioAsync(months: 2);
        await ParameterAsync(scenario, 0, "1800.00", "0.00", _pf);
        var first = await ProjectionAsync(scenario, 0); var second = await ProjectionAsync(scenario, 1);

        Assert.Equal(("1000.00", "0.00", "1000.00", "Recovered", "800.00"), (S(first, "openingNegativeBalance"), S(first, "closingNegativeBalance"), S(first, "projectedRecovery"), S(first, "recoveryStatus"), S(first, "targetClosingBalance")));
        Assert.Equal(("0.00", "0.00", "NoNegativeBalance"), (S(second, "openingNegativeBalance"), S(second, "closingNegativeBalance"), S(second, "recoveryStatus")));
        Assert.Equal("1000.00", S(second, "cumulativeRecovery"));
    }

    [Fact]
    public async Task AFixedOfficialTargetIsPreservedAndNeverRewrittenByASimulation()
    {
        await ArrangeAsync("t141-official");
        var scenario = await ScenarioAsync();
        await using (var db = Db())
        {
            db.OverdraftRecoverySnapshots.Add(Saldo360.Domain.Accounts.OverdraftRecoverySnapshot.CreateFixed(_workspace, _pf, First, -1000m, First, 0m, 0m, 0m, 500m, 0.80m, 400m, "Complete", "", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        await ParameterAsync(scenario, 0, "900.00", "0.00", _pf);
        var month = await ProjectionAsync(scenario, 0);

        Assert.Equal(("Official", "400.00", "400.00", "500.00"), (S(month, "recoveryTargetSource"), S(month, "officialRecoveryTarget"), S(month, "recoveryTarget"), S(month, "recoveryDeviation")));
        await using var check = Db();
        Assert.Equal(400m, (await check.OverdraftRecoverySnapshots.SingleAsync(x => x.AccountId == _pf)).RecoveryTarget);
        Assert.Equal(1, await check.OverdraftRecoverySnapshots.CountAsync());
    }

    [Fact]
    public async Task AScenarioWithoutATargetAccountDoesNotInventARecovery()
    {
        await ArrangeAsync("t141-notarget");
        var scenario = await ScenarioAsync(withTarget: false);
        await ParameterAsync(scenario, 0, "500.00", "0.00", _pf);
        var month = await ProjectionAsync(scenario, 0);

        Assert.Equal(("NotConfigured", JsonValueKind.Null), (S(month, "recoveryStatus"), month.GetProperty("projectedRecovery").ValueKind));
        Assert.Equal("0.00", S(month, "targetCumulativeAdjustment"));
    }

    [Fact]
    public async Task TheAdjustmentAccountMustBelongToTheFamily()
    {
        await ArrangeAsync("t141-foreign");
        var scenario = await ScenarioAsync();
        var response = await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{_workspace}/planning-scenarios/{scenario}/parameters/{First:yyyy-MM}", _session,
            new { expectedAdditionalIncome = "1.00", expectedAdditionalExpense = "0.00", contingencyReserve = "0.00", recoveryTargetPercentage = "80.00", adjustmentAccountId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Integração com cartões (Agenda): o planejamento usa o que ela já calcula, sem recontar ----------------------------------------------------------

    private async Task<Guid> CardAsync(Guid? payer) => await CreateAsync("credit-cards", new { name = "Cartão", issuer = "Banco", creditLimit = "30000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = payer });

    private async Task<Guid> PurchaseAsync(Guid card, string amount, string description = "Compra")
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_workspace}/credit-cards/{card}/purchases", _session, new
        {
            description, purchaseDate = Today, totalAmount = amount, installmentCount = 1, categoryId = _expense, amountMode = "Total", previouslyPaidInstallments = 0, firstPendingCompetence = First,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task APartiallyPaidInvoiceCountsOnlyTheRemainingAmountInTheTargetAccountsRecovery()
    {
        await ArrangeAsync("t141-partial", pfOpening: "-3000.00");
        var card = await CardAsync(_pf);
        await PurchaseAsync(card, "1000.00");
        Guid invoice;
        await using (var db = Db())
        {
            invoice = (await db.FinancialCreditCardInvoices.SingleAsync()).Id;
            await OfficialInvoiceFixture.CloseAsync(db, invoice);
        }
        var payment = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_workspace}/credit-cards/{card}/invoices/{invoice}/payments", _session,
            new { operationId = Guid.NewGuid(), financialAccountId = _pf, paymentDate = Today, amount = "400.00" });
        Assert.True(payment.IsSuccessStatusCode, await payment.Content.ReadAsStringAsync());
        var month = await ProjectionAsync(await ScenarioAsync(months: 1), 0);

        // −3.000 de abertura − 400 já pagos (saldo observado) − 600 ainda devidos = −4.000: nem os 400 de novo, nem os 1.000 integrais.
        Assert.Equal(("3400.00", "4000.00", "Worsening", "0.00"), (S(month, "openingNegativeBalance"), S(month, "closingNegativeBalance"), S(month, "recoveryStatus"), S(month, "projectedRecovery")));
        Assert.Equal("600.00", S(month, "targetPendingExpense"));
    }

    [Fact]
    public async Task AnInvoiceFinancingInstallmentIsOneObligationThatReducesThePayersBalanceOnceAndAffectsItsRecovery()
    {
        await ArrangeAsync("t141-financing", pfOpening: "-3000.00");
        var card = await CardAsync(_pf);
        var scenario = await ScenarioAsync(months: 1);
        var before = await ProjectionAsync(scenario, 0);
        var purchase = await PurchaseAsync(card, "1814.73", "Parcelamento de fatura");
        await using (var db = Db()) await db.Database.ExecuteSqlRawAsync("UPDATE financial_credit_card_purchases SET operation_kind = 'InvoiceFinancing' WHERE id = {0}", purchase);
        var month = await ProjectionAsync(scenario, 0);

        Assert.Equal("3000.00", S(before, "closingNegativeBalance"));
        Assert.Equal(("4814.73", "Worsening", "1814.73"), (S(month, "closingNegativeBalance"), S(month, "recoveryStatus"), S(month, "targetPendingExpense")));
        await using var check = Db();
        Assert.Equal(0, await check.FinancialTransactions.CountAsync());                          // nenhuma segunda saída financeira pelo mesmo financiamento
    }

    [Fact]
    public async Task WhenTheAgendaCannotAllocateTheCardObligationsTheRecoveryIsUnavailableNotZero()
    {
        await ArrangeAsync("t141-incomplete", pfOpening: "-3000.00");
        var card = await CardAsync(null);                                                        // sem conta pagadora: a Agenda não sabe de qual conta sai a fatura
        await PurchaseAsync(card, "800.00");
        var month = await ProjectionAsync(await ScenarioAsync(months: 1), 0);

        Assert.Equal(("IncompleteProjection", JsonValueKind.Null, JsonValueKind.Null), (S(month, "recoveryStatus"), month.GetProperty("projectedRecovery").ValueKind, month.GetProperty("closingNegativeBalance").ValueKind));
        Assert.False(month.GetProperty("recoveryProjectionComplete").GetBoolean());
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialDebtEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task Debt_PersistsContractValuesAndStateTransitions_WithoutFinancialEffects()
    {
        var owner = await SignInAsync("debt-owner");
        var family = await CreateFamilyAsync(owner, "Família Dívidas");
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/accounts", owner,
            new { name = "Conta pagamento", type = "Checking", financialScope = "PF", openingBalance = "1000.00", openingBalanceDate = "2026-01-01" });
        using var accountBody = JsonDocument.Parse(await account.Content.ReadAsStringAsync());
        var accountId = accountBody.RootElement.GetProperty("id").GetGuid();
        var create = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts", owner, new
        {
            name = "Financiamento veículo", description = "Contrato 123", debtType = "VehicleFinancing", lenderName = "Credora",
            responsibleAccountId = accountId, originalPrincipal = "30000.00", outstandingPrincipal = "25000.00", outstandingBalance = "27850.75",
            balanceReferenceDate = "2026-10-16", contractDate = "2025-01-10", firstDueDate = "2025-02-10", installmentCount = 48,
            paidInstallmentCount = 12, installmentAmount = "812.34", interestRate = "1.25", interestRatePeriod = "Monthly", amortizationMethod = "Price"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var debtId = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("30000.00", body.RootElement.GetProperty("originalPrincipal").GetString());
        Assert.Equal("25000.00", body.RootElement.GetProperty("outstandingPrincipal").GetString());
        Assert.Equal("27850.75", body.RootElement.GetProperty("outstandingBalance").GetString());
        Assert.Equal("Price", body.RootElement.GetProperty("amortizationMethod").GetString());

        var suspended = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts/{debtId}/suspend", owner);
        Assert.Equal("Suspended", JsonDocument.Parse(await suspended.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        var resumed = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts/{debtId}/resume", owner);
        Assert.Equal("Active", JsonDocument.Parse(await resumed.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        var settled = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts/{debtId}/settle", owner);
        Assert.Equal("Settled", JsonDocument.Parse(await settled.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        var archived = await SendWithCsrfAsync(HttpMethod.Delete, $"/api/families/{family.Id}/debts/{debtId}", owner);
        Assert.Equal("Archived", JsonDocument.Parse(await archived.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());

        await using var db = Db();
        Assert.Equal(1, await db.FinancialDebts.CountAsync());
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
        Assert.Equal(0, await db.FinancialPlannedTransactions.CountAsync());
    }

    [Fact]
    public async Task Debt_RejectsInvalidInstallmentsAndForeignAccount()
    {
        var owner = await SignInAsync("debt-validation-owner");
        var other = await SignInAsync("debt-validation-other");
        var family = await CreateFamilyAsync(owner, "Família Dívidas Validação");
        var otherFamily = await CreateFamilyAsync(other, "Outra Família Dívidas");
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{otherFamily.Id}/accounts", other,
            new { name = "Conta externa", type = "Checking", financialScope = "PF", openingBalance = "0.00", openingBalanceDate = "2026-01-01" });
        using var accountBody = JsonDocument.Parse(await account.Content.ReadAsStringAsync());
        var invalid = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts", owner, new
        {
            name = "Inválida", debtType = "PersonalLoan", responsibleAccountId = accountBody.RootElement.GetProperty("id").GetGuid(),
            originalPrincipal = "1000.00", contractDate = "2026-01-01", installmentCount = 2, paidInstallmentCount = 3
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Debt_InstallmentPayment_IsIdempotentAndCreatesOneBankTransaction()
    {
        var owner = await SignInAsync("debt-payment-owner");
        var family = await CreateFamilyAsync(owner, "Família Pagamentos");
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/accounts", owner,
            new { name = "Conta", type = "Checking", financialScope = "PF", openingBalance = "1000.00", openingBalanceDate = "2026-01-01" });
        using var accountBody = JsonDocument.Parse(await account.Content.ReadAsStringAsync());
        var accountId = accountBody.RootElement.GetProperty("id").GetGuid();
        var debt = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts", owner,
            new { name = "Empréstimo", debtType = "PersonalLoan", responsibleAccountId = accountId, originalPrincipal = "500.00", contractDate = "2026-01-01" });
        using var debtBody = JsonDocument.Parse(await debt.Content.ReadAsStringAsync());
        var debtId = debtBody.RootElement.GetProperty("id").GetGuid();
        var installment = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts/{debtId}/installments", owner,
            new { installmentNumber = 1, dueDate = "2026-11-10", scheduledAmount = "123.45", principalComponent = "100.00" });
        Assert.Equal(HttpStatusCode.Created, installment.StatusCode);
        using var installmentBody = JsonDocument.Parse(await installment.Content.ReadAsStringAsync());
        var installmentId = installmentBody.RootElement.GetProperty("id").GetGuid();
        var payload = new { operationId = "payment-op-1", financialAccountId = accountId, paymentDate = "2026-10-16", amount = "123.45", principalPaid = "100.00" };
        var first = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts/{debtId}/installments/{installmentId}/payments", owner, payload);
        var second = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts/{debtId}/installments/{installmentId}/payments", owner, payload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await using var db = Db();
        Assert.Equal(1, await db.FinancialDebtPayments.CountAsync());
        Assert.Equal(1, await db.FinancialTransactions.CountAsync(t => t.Origin == Saldo360.Domain.Transactions.FinancialTransactionOrigin.DebtInstallmentPayment));
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialDebtInstallmentReconciliationEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task Reconciliation_IsExplicitIdempotentAndCreatesNoBankTransaction()
    {
        var user = await SignInAsync("reconciliation-owner");
        var family = await CreateFamilyAsync(user, "Família Conciliação");
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/accounts", user,
            new { name = "Conta", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = "2026-01-01" });
        using var accountJson = JsonDocument.Parse(await account.Content.ReadAsStringAsync());
        var accountId = accountJson.RootElement.GetProperty("id").GetGuid();
        var category = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/categories", user,
            new { name = "Financiamento", type = "Expense" });
        using var categoryJson = JsonDocument.Parse(await category.Content.ReadAsStringAsync());
        var categoryId = categoryJson.RootElement.GetProperty("id").GetGuid();
        var debt = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts", user,
            new { name = "Financiamento", debtType = "VehicleFinancing", responsibleAccountId = accountId, originalPrincipal = "1000.00", contractDate = "2026-01-01" });
        using var debtJson = JsonDocument.Parse(await debt.Content.ReadAsStringAsync());
        var debtId = debtJson.RootElement.GetProperty("id").GetGuid();
        var installment = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/debts/{debtId}/installments", user,
            new { installmentNumber = 1, dueDate = "2026-11-10", scheduledAmount = "1000.00", categoryId });
        using var installmentJson = JsonDocument.Parse(await installment.Content.ReadAsStringAsync());
        var installmentId = installmentJson.RootElement.GetProperty("id").GetGuid();
        var planned = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/planned-transactions", user,
            new { accountId, categoryId, type = "Expense", amount = "1000.00", dueDate = "2026-11-10", description = "Parcela" });
        using var plannedJson = JsonDocument.Parse(await planned.Content.ReadAsStringAsync());
        var plannedId = plannedJson.RootElement.GetProperty("id").GetGuid();
        var url = $"/api/families/{family.Id}/debt-installment-reconciliations";
        var payload = new { debtInstallmentId = installmentId, plannedTransactionId = plannedId };
        var concurrent = await Task.WhenAll(
            SendWithCsrfAsync(HttpMethod.Post, url, user, payload),
            SendWithCsrfAsync(HttpMethod.Post, url, user, payload));
        var first = concurrent[0]; var second = concurrent[1];
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var reconciliationId = firstJson.RootElement.GetProperty("id").GetGuid();
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(reconciliationId, secondJson.RootElement.GetProperty("id").GetGuid());
        await using var db = Db();
        Assert.Equal(1, await db.FinancialDebtInstallmentReconciliations.CountAsync(r => r.RemovedAt == null));
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
        var removed = await SendWithCsrfAsync(HttpMethod.Delete, $"{url}/{reconciliationId}", user);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendWithCsrfAsync(HttpMethod.Delete, $"{url}/{reconciliationId}", user)).StatusCode);
    }
}

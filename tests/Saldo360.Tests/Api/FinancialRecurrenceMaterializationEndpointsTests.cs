using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialRecurrenceMaterializationEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task Materialization_IsIdempotent_AndDoesNotCreateBankTransactions()
    {
        var owner = await SignInAsync("recurrence-owner");
        var family = await CreateFamilyAsync(owner, "Família Recorrência");
        var account = await CreateAccountAsync(owner, family.Id, "Conta", "1000.00");
        var category = await CreateCategoryAsync(owner, family.Id, "Internet", "Expense");
        var baseUrl = $"/api/families/{family.Id}/recurrences";
        var created = await SendWithCsrfAsync(HttpMethod.Post, baseUrl, owner, new { accountId = account, categoryId = category, type = "Expense", description = "Internet", amount = "380.00", isEstimated = true, frequency = "Monthly", interval = 1, startDate = "2026-11-10", dayOfMonth = 10 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var recurrenceId = (await ReadAsync<IdDto>(created)).Id;

        var first = await SendWithCsrfAsync(HttpMethod.Post, $"{baseUrl}/{recurrenceId}/materialize", owner, new { from = "2026-11-01", through = "2027-01-31" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync())) Assert.Equal((3, 0), (body.RootElement.GetProperty("createdCount").GetInt32(), body.RootElement.GetProperty("existingCount").GetInt32()));

        var overlap = await SendWithCsrfAsync(HttpMethod.Post, $"{baseUrl}/{recurrenceId}/materialize", owner, new { from = "2026-12-01", through = "2027-02-28" });
        Assert.Equal(HttpStatusCode.OK, overlap.StatusCode);
        using (var body = JsonDocument.Parse(await overlap.Content.ReadAsStringAsync())) Assert.Equal((1, 2), (body.RootElement.GetProperty("createdCount").GetInt32(), body.RootElement.GetProperty("existingCount").GetInt32()));

        await using var db = Db();
        Assert.Equal(4, await db.FinancialPlannedTransactions.CountAsync());
        Assert.Equal(4, await db.FinancialRecurrenceMaterializations.CountAsync());
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
        var view = await SendAsync(HttpMethod.Get, $"{baseUrl}/{recurrenceId}/materializations?from=2026-11-01&through=2027-03-31", owner);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        using var materializations = JsonDocument.Parse(await view.Content.ReadAsStringAsync());
        Assert.Equal(5, materializations.RootElement.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, materializations.RootElement[4].GetProperty("plannedTransactionId").ValueKind);

        var calendar = await SendAsync(HttpMethod.Get, $"/api/families/{family.Id}/financial-calendar?from=2026-11-01&through=2027-03-31", owner);
        Assert.Equal(HttpStatusCode.OK, calendar.StatusCode);
        using var calendarBody = JsonDocument.Parse(await calendar.Content.ReadAsStringAsync());
        Assert.Equal(4, calendarBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.All(calendarBody.RootElement.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(recurrenceId, item.GetProperty("recurrenceId").GetGuid());
            Assert.True(item.GetProperty("occurrenceNumber").GetInt32() > 0);
        });
    }

    [Fact]
    public async Task Realization_PreservesRecurrenceIdentity_AndIsIdempotent()
    {
        var owner = await SignInAsync("recurrence-realization-owner");
        var family = await CreateFamilyAsync(owner, "Família Realização Recorrente");
        var account = await CreateAccountAsync(owner, family.Id, "Conta", "5000.00");
        var category = await CreateCategoryAsync(owner, family.Id, "Internet", "Expense");
        var baseUrl = $"/api/families/{family.Id}/recurrences";
        var created = await SendWithCsrfAsync(HttpMethod.Post, baseUrl, owner, new
        {
            accountId = account, categoryId = category, type = "Expense", description = "Internet",
            amount = "380.00", isEstimated = false, frequency = "Monthly", interval = 1,
            startDate = "2026-11-10", dayOfMonth = 10
        });
        var recurrenceId = (await ReadAsync<IdDto>(created)).Id;

        var materialized = await SendWithCsrfAsync(HttpMethod.Post, $"{baseUrl}/{recurrenceId}/materialize", owner,
            new { from = "2026-11-01", through = "2027-01-31" });
        Assert.Equal(HttpStatusCode.OK, materialized.StatusCode);
        using var materializedBody = JsonDocument.Parse(await materialized.Content.ReadAsStringAsync());
        var plannedId = materializedBody.RootElement.GetProperty("items")[0].GetProperty("plannedTransactionId").GetGuid();

        var realization = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{family.Id}/planned-transactions/{plannedId}/realize", owner,
            new { amount = "380.00", transactionDate = "2026-10-15" });
        Assert.Equal(HttpStatusCode.Created, realization.StatusCode);

        var repeated = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{family.Id}/planned-transactions/{plannedId}/realize", owner,
            new { amount = "380.00", transactionDate = "2026-10-15" });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);

        await using var db = Db();
        Assert.Equal(1, await db.FinancialTransactions.CountAsync(x => x.PlannedTransactionId == plannedId));
        Assert.Equal(3, await db.FinancialRecurrenceMaterializations.CountAsync(x => x.RecurrenceId == recurrenceId));

        var view = await SendAsync(HttpMethod.Get,
            $"{baseUrl}/{recurrenceId}/materializations?from=2026-11-01&through=2027-01-31", owner);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        using var viewBody = JsonDocument.Parse(await view.Content.ReadAsStringAsync());
        Assert.Equal("Realized", viewBody.RootElement[0].GetProperty("status").GetString());
        Assert.Equal(plannedId, viewBody.RootElement[0].GetProperty("plannedTransactionId").GetGuid());
    }

    private async Task<Guid> CreateAccountAsync(string session, Guid familyId, string name, string openingBalance)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/accounts", session, new { name, type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate = "2026-09-01" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await ReadAsync<IdDto>(response)).Id;
    }
    private async Task<Guid> CreateCategoryAsync(string session, Guid familyId, string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/categories", session, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await ReadAsync<IdDto>(response)).Id;
    }
    private sealed record IdDto(Guid Id);
}

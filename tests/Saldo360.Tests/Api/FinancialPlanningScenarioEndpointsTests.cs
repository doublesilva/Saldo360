using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialPlanningScenarioEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task Scenario_AndMonthlyParameters_ArePersistedWithoutFinancialEffects()
    {
        var owner = await SignInAsync("scenario-owner");
        var family = await CreateFamilyAsync(owner, "Família Cenários");
        var create = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/planning-scenarios", owner,
            new { name = "Realista", description = "Hipótese base", startMonth = "2026-11", months = 3 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var createdBody = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var scenarioId = createdBody.RootElement.GetProperty("id").GetGuid();

        var parameter = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/families/{family.Id}/planning-scenarios/{scenarioId}/parameters/2026-12", owner,
            new { expectedAdditionalIncome = "500.00", expectedAdditionalExpense = "237.45", contingencyReserve = "1000.00", recoveryTargetPercentage = "85.00", notes = "Reserva da competência" });
        Assert.Equal(HttpStatusCode.OK, parameter.StatusCode);
        using var parameterBody = JsonDocument.Parse(await parameter.Content.ReadAsStringAsync());
        Assert.Equal("237.45", parameterBody.RootElement.GetProperty("expectedAdditionalExpense").GetString());
        Assert.Equal("85.00", parameterBody.RootElement.GetProperty("recoveryTargetPercentage").GetString());

        var repeated = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/families/{family.Id}/planning-scenarios/{scenarioId}/parameters/2026-12", owner,
            new { expectedAdditionalIncome = "500.00", expectedAdditionalExpense = "237.45", contingencyReserve = "1000.00", recoveryTargetPercentage = "85.00", notes = "Atualizado" });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var list = await SendAsync(HttpMethod.Get, $"/api/families/{family.Id}/planning-scenarios/{scenarioId}/parameters", owner);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Single(listBody.RootElement.EnumerateArray());

        await using var db = Db();
        Assert.Equal(1, await db.FinancialPlanningScenarios.CountAsync());
        Assert.Equal(1, await db.FinancialPlanningMonthlyParameters.CountAsync());
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
        Assert.Equal(0, await db.FinancialPlannedTransactions.CountAsync());
        Assert.Equal(0, await db.FinancialBudgets.CountAsync());
    }

    [Fact]
    public async Task Scenario_ValidatesHorizonPercentAndFamilyIsolation_AndArchivesLogically()
    {
        var owner = await SignInAsync("scenario-validation-owner");
        var other = await SignInAsync("scenario-validation-other");
        var family = await CreateFamilyAsync(owner, "Família Protegida");
        var otherFamily = await CreateFamilyAsync(other, "Outra Família");
        var invalid = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/planning-scenarios", owner,
            new { name = "", startMonth = "2026-1", months = 13 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var create = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/planning-scenarios", owner,
            new { name = "Conservador", startMonth = "2027-01", months = 12 });
        using var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        var foreign = await SendAsync(HttpMethod.Get, $"/api/families/{otherFamily.Id}/planning-scenarios/{id}", other);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var parameter = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/families/{family.Id}/planning-scenarios/{id}/parameters/2027-01", owner,
            new { expectedAdditionalIncome = "0.00", expectedAdditionalExpense = "0.00", contingencyReserve = "0.00", recoveryTargetPercentage = "100.00" });
        Assert.Equal(HttpStatusCode.OK, parameter.StatusCode);
        var outOfRange = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/families/{family.Id}/planning-scenarios/{id}/parameters/2028-01", owner,
            new { expectedAdditionalIncome = "0.00", expectedAdditionalExpense = "0.00", contingencyReserve = "0.00", recoveryTargetPercentage = "100.01" });
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);

        var archived = await SendWithCsrfAsync(HttpMethod.Delete, $"/api/families/{family.Id}/planning-scenarios/{id}", owner);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var activeOnly = await SendAsync(HttpMethod.Get, $"/api/families/{family.Id}/planning-scenarios", owner);
        Assert.Empty(JsonDocument.Parse(await activeOnly.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
        var all = await SendAsync(HttpMethod.Get, $"/api/families/{family.Id}/planning-scenarios?includeArchived=true", owner);
        Assert.Single(JsonDocument.Parse(await all.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
    }
}

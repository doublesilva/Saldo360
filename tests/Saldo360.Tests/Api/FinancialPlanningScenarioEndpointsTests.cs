using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
public sealed class FinancialPlanningScenarioEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task Scenario_AndMonthlyParameters_ArePersistedWithoutFinancialEffects()
    {
        var owner = await SignInAsync("scenario-owner");
        var family = await CreateWorkspaceAsync(owner, "Família Cenários");
        var create = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/planning-scenarios", owner,
            new { name = "Realista", description = "Hipótese base", startMonth = "2026-11", months = 3 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var createdBody = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var scenarioId = createdBody.RootElement.GetProperty("id").GetGuid();

        var parameter = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/financial-workspaces/{family.Id}/planning-scenarios/{scenarioId}/parameters/2026-12", owner,
            new { expectedAdditionalIncome = "500.00", expectedAdditionalExpense = "237.45", contingencyReserve = "1000.00", recoveryTargetPercentage = "85.00", notes = "Reserva da competência" });
        Assert.Equal(HttpStatusCode.OK, parameter.StatusCode);
        using var parameterBody = JsonDocument.Parse(await parameter.Content.ReadAsStringAsync());
        Assert.Equal("237.45", parameterBody.RootElement.GetProperty("expectedAdditionalExpense").GetString());
        Assert.Equal("85.00", parameterBody.RootElement.GetProperty("recoveryTargetPercentage").GetString());

        var repeated = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/financial-workspaces/{family.Id}/planning-scenarios/{scenarioId}/parameters/2026-12", owner,
            new { expectedAdditionalIncome = "500.00", expectedAdditionalExpense = "237.45", contingencyReserve = "1000.00", recoveryTargetPercentage = "85.00", notes = "Atualizado" });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var list = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/planning-scenarios/{scenarioId}/parameters", owner);
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
        var family = await CreateWorkspaceAsync(owner, "Família Protegida");
        var otherFamily = await CreateWorkspaceAsync(other, "Outra Família");
        var foreignAccountResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{otherFamily.Id}/accounts", other,
            new { name = "Conta externa", type = "Checking", financialScope = "PF", openingBalance = "0.00", openingBalanceDate = "2026-01-01" });
        using var foreignAccountBody = JsonDocument.Parse(await foreignAccountResponse.Content.ReadAsStringAsync());
        var foreignAccountId = foreignAccountBody.RootElement.GetProperty("id").GetGuid();
        var invalid = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/planning-scenarios", owner,
            new { name = "", startMonth = "2026-1", months = 13 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var foreignTarget = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/planning-scenarios", owner,
            new { name = "Conta externa", startMonth = "2027-01", months = 1, recoveryAccountId = foreignAccountId });
        Assert.Equal(HttpStatusCode.BadRequest, foreignTarget.StatusCode);

        var create = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/planning-scenarios", owner,
            new { name = "Conservador", startMonth = "2027-01", months = 12 });
        using var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        var foreign = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{otherFamily.Id}/planning-scenarios/{id}", other);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var parameter = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/financial-workspaces/{family.Id}/planning-scenarios/{id}/parameters/2027-01", owner,
            new { expectedAdditionalIncome = "0.00", expectedAdditionalExpense = "0.00", contingencyReserve = "0.00", recoveryTargetPercentage = "100.00" });
        Assert.Equal(HttpStatusCode.OK, parameter.StatusCode);
        var outOfRange = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/financial-workspaces/{family.Id}/planning-scenarios/{id}/parameters/2028-01", owner,
            new { expectedAdditionalIncome = "0.00", expectedAdditionalExpense = "0.00", contingencyReserve = "0.00", recoveryTargetPercentage = "100.01" });
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);

        var archived = await SendWithCsrfAsync(HttpMethod.Delete, $"/api/financial-workspaces/{family.Id}/planning-scenarios/{id}", owner);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var activeOnly = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/planning-scenarios", owner);
        Assert.Empty(JsonDocument.Parse(await activeOnly.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
        var all = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/planning-scenarios?includeArchived=true", owner);
        Assert.Single(JsonDocument.Parse(await all.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Projection_AppliesMonthlyAdjustmentsCumulatively_AndKeepsReserveOutsideBalance()
    {
        var owner = await SignInAsync("scenario-projection-owner");
        var family = await CreateWorkspaceAsync(owner, "Família Projeção");
        var accountResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/accounts", owner,
            new { name = "Conta", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = "2026-09-01" });
        Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);
        var create = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/planning-scenarios", owner,
            new { name = "Otimista", startMonth = "2026-11", months = 2 });
        using var scenarioBody = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var scenarioId = scenarioBody.RootElement.GetProperty("id").GetGuid();
        var parameterUrl = $"/api/financial-workspaces/{family.Id}/planning-scenarios/{scenarioId}/parameters";
        foreach (var (month, income, expense, reserve) in new[] { ("2026-11", "500.00", "0.00", "1000.00"), ("2026-12", "0.00", "200.00", "500.00") })
        {
            var response = await SendWithCsrfAsync(HttpMethod.Put, $"{parameterUrl}/{month}", owner,
                new { expectedAdditionalIncome = income, expectedAdditionalExpense = expense, contingencyReserve = reserve, recoveryTargetPercentage = "85.00" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var projection = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/planning-scenarios/{scenarioId}/projection", owner);
        Assert.Equal(HttpStatusCode.OK, projection.StatusCode);
        using var body = JsonDocument.Parse(await projection.Content.ReadAsStringAsync());
        var november = body.RootElement.GetProperty("items")[0];
        var december = body.RootElement.GetProperty("items")[1];
        Assert.Equal("5000.00", november.GetProperty("baseClosingBalance").GetString());
        Assert.Equal("5500.00", november.GetProperty("scenarioClosingBalance").GetString());
        Assert.Equal("4500.00", november.GetProperty("plannedAvailable").GetString());
        Assert.Equal("300.00", december.GetProperty("cumulativeAdjustment").GetString());
        Assert.Equal("5300.00", december.GetProperty("scenarioClosingBalance").GetString());
        Assert.Equal("true", december.GetProperty("projectionComplete").GetRawText());

        await using var db = Db();
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
        Assert.Equal(0, await db.FinancialPlannedTransactions.CountAsync());
    }

    [Fact]
    public async Task Projection_UsesExplicitRecoveryAccount_WithoutCreatingFinancialEntries()
    {
        var owner = await SignInAsync("scenario-recovery-owner");
        var family = await CreateWorkspaceAsync(owner, "Família Recuperação");
        var accountResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/accounts", owner,
            new { name = "Conta negativa", type = "Checking", financialScope = "PF", openingBalance = "-1000.00", openingBalanceDate = "2026-09-01" });
        Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);
        using var accountBody = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync());
        var accountId = accountBody.RootElement.GetProperty("id").GetGuid();
        var create = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/planning-scenarios", owner,
            new { name = "Recuperação", startMonth = "2026-11", months = 1, recoveryAccountId = accountId });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var scenarioBody = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var scenarioId = scenarioBody.RootElement.GetProperty("id").GetGuid();
        var parameter = await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{family.Id}/planning-scenarios/{scenarioId}/parameters/2026-11", owner,
            new { expectedAdditionalIncome = "0.00", expectedAdditionalExpense = "0.00", contingencyReserve = "0.00", recoveryTargetPercentage = "85.00" });
        Assert.Equal(HttpStatusCode.OK, parameter.StatusCode);

        var projection = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/planning-scenarios/{scenarioId}/projection", owner);
        Assert.Equal(HttpStatusCode.OK, projection.StatusCode);
        using var body = JsonDocument.Parse(await projection.Content.ReadAsStringAsync());
        var item = body.RootElement.GetProperty("items")[0];
        Assert.Equal(accountId, item.GetProperty("recoveryAccountId").GetGuid());
        Assert.Equal("1000.00", item.GetProperty("openingNegativeBalance").GetString());
        Assert.Equal("1000.00", item.GetProperty("closingNegativeBalance").GetString());
        Assert.Equal("0.00", item.GetProperty("projectedRecovery").GetString());
        Assert.Equal("Stable", item.GetProperty("recoveryStatus").GetString());
        Assert.True(item.GetProperty("recoveryProjectionComplete").GetBoolean());

        await using var db = Db();
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
        Assert.Equal(0, await db.FinancialPlannedTransactions.CountAsync());
    }
}

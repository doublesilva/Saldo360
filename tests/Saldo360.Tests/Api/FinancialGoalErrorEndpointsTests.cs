using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialGoalErrorEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task Contribution_StandardizesValidationConflictAndIdempotency_WithoutDuplicateAllocation()
    {
        var owner = await SignInAsync("goal-error-owner");
        var family = await CreateFamilyAsync(owner, "Família Objetivos Erros");
        var accountId = await CreateAccountAsync(family.Id, owner, "100.00");
        var goalId = await CreateGoalAsync(family.Id, owner);
        var today = TodayInSaoPaulo();

        var invalid = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/financial-goals/{goalId}/contributions", owner,
            new { operationId = Guid.Empty, financialAccountId = accountId, amount = "abc", effectiveDate = today });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidBody = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        AssertProblem(invalidBody.RootElement, 400, "VALIDATION_ERROR");
        Assert.True(invalidBody.RootElement.GetProperty("errors").TryGetProperty("amount", out _));

        var conflict = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/financial-goals/{goalId}/contributions", owner,
            new { operationId = Guid.NewGuid(), financialAccountId = accountId, amount = "1100.00", effectiveDate = today });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var conflictBody = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        AssertProblem(conflictBody.RootElement, 409, "OPERATION_CONFLICT");

        var operationId = Guid.NewGuid();
        var payload = new { operationId, financialAccountId = accountId, amount = "50.00", effectiveDate = today };
        var first = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/financial-goals/{goalId}/contributions", owner, payload);
        var replay = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/financial-goals/{goalId}/contributions", owner, payload);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(
            JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid(),
            JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());

        var duplicate = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/financial-goals/{goalId}/contributions", owner,
            new { operationId, financialAccountId = accountId, amount = "40.00", effectiveDate = today });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var duplicateBody = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        AssertProblem(duplicateBody.RootElement, 409, "DUPLICATE_OPERATION");

        var operationLog = Assert.Single(Factory.Logs.StructuredEntries, entry =>
            entry.Category.EndsWith("FinancialOperationLogger")
            && Equals(entry.Properties.GetValueOrDefault("ErrorCode"), "DUPLICATE_OPERATION"));
        Assert.Equal("GoalContribution", operationLog.Properties["OperationType"]);
        Assert.Equal("Rejected", operationLog.Properties["OperationResult"]);
        Assert.DoesNotContain("50.00", operationLog.Message);
        Assert.DoesNotContain(family.Id.ToString(), operationLog.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(accountId.ToString(), operationLog.Message, StringComparison.OrdinalIgnoreCase);

        await using var db = Db();
        Assert.Equal(1, await db.FinancialGoalAllocations.CountAsync());
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
    }

    [Fact]
    public async Task Contribution_HidesResourcesFromAnotherFamily()
    {
        var owner = await SignInAsync("goal-hidden-owner");
        var outsider = await SignInAsync("goal-hidden-outsider");
        var family = await CreateFamilyAsync(owner, "Família Objetivo Oculto");
        var accountId = await CreateAccountAsync(family.Id, owner, "100.00");
        var goalId = await CreateGoalAsync(family.Id, owner);

        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/financial-goals/{goalId}/contributions", outsider,
            new { operationId = Guid.NewGuid(), financialAccountId = accountId, amount = "10.00", effectiveDate = TodayInSaoPaulo() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertProblem(body.RootElement, 404, "RESOURCE_NOT_FOUND");
    }

    private async Task<Guid> CreateAccountAsync(Guid familyId, string owner, string openingBalance)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/accounts", owner,
            new { name = "Conta objetivos", type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate = TodayInSaoPaulo() });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateGoalAsync(Guid familyId, string owner)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/financial-goals", owner,
            new { name = "Reserva", goalType = "EmergencyFund", targetAmount = "1000.00", priority = "High" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static string TodayInSaoPaulo()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "E. South America Standard Time" : "America/Sao_Paulo");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date).ToString("yyyy-MM-dd");
    }

    private static void AssertProblem(JsonElement problem, int status, string code)
    {
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("instance").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }
}

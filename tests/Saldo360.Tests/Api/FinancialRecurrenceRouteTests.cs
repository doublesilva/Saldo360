using System.Net;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Recurrences;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>Rotas de mudança de estado de recorrência (pause, resume, end, DELETE) de ponta a ponta: o parâmetro da rota precisa casar com {workspaceId}.</summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Recurrences")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialRecurrenceRouteTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task PauseResumeEndAndArchive_WorkOverHttp_ChangeStateAndAreIsolatedPerWorkspace()
    {
        var owner = await SignInAsync("rota-dona");
        var stranger = await SignInAsync("rota-estranho");
        var workspace = await CreateWorkspaceAsync(owner);
        var other = await CreateWorkspaceAsync(stranger);
        var id = await AddRecurrenceAsync(owner, workspace);
        var url = $"/api/financial-workspaces/{workspace.Id}/recurrences/{id}";

        // Outro orçamento (e rota de outro orçamento com o id certo) nunca altera nada: 404, não 400.
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"{url}/pause", stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{other.Id}/recurrences/{id}/pause", stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Delete, $"/api/financial-workspaces/{other.Id}/recurrences/{id}", stranger)).StatusCode);
        Assert.Equal(RecurrenceStatus.Active, await StatusAsync(id));

        Assert.Equal("Paused", await ChangeAsync(HttpMethod.Post, $"{url}/pause", owner));
        Assert.Equal(RecurrenceStatus.Paused, await StatusAsync(id));
        Assert.Equal("Active", await ChangeAsync(HttpMethod.Post, $"{url}/resume", owner));
        Assert.Equal("Ended", await ChangeAsync(HttpMethod.Post, $"{url}/end", owner));
        Assert.Equal("Archived", await ChangeAsync(HttpMethod.Delete, url, owner));
        Assert.Equal(RecurrenceStatus.Archived, await StatusAsync(id));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendJsonAsync(HttpMethod.Post, $"{url}/pause", owner, csrf: null, body: null)).StatusCode); // CSRF, não binding
    }

    private async Task<string> ChangeAsync(HttpMethod method, string url, string session)
    {
        var response = await SendWithCsrfAsync(method, url, session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<StatusDto>(response)).Status;
    }

    private async Task<RecurrenceStatus> StatusAsync(Guid id)
    {
        await using var db = Db();
        return (await db.FinancialRecurrences.AsNoTracking().SingleAsync(r => r.Id == id)).Status;
    }

    private async Task<Guid> AddRecurrenceAsync(string session, WorkspaceDto workspace)
    {
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace.Id}/accounts", session, new { name = "Conta", type = "Checking", financialScope = "PF" });
        var category = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace.Id}/categories", session, new { name = "Aluguel", type = "Expense" });
        await using var db = Db();
        var recurrence = FinancialRecurrence.Create(workspace.Id,
            new((await ReadAsync<IdDto>(account)).Id, (await ReadAsync<IdDto>(category)).Id, CategoryType.Expense, "Aluguel", 100m, true,
                RecurrenceFrequency.Monthly, 1, new DateOnly(2027, 1, 10), null, null, 10), DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
        db.FinancialRecurrences.Add(recurrence);
        await db.SaveChangesAsync();
        return recurrence.Id;
    }

    private sealed record IdDto(Guid Id);
    private sealed record StatusDto(string Status);
}

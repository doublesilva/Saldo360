using System.Net;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialWorkspaceEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task ExistingFamilyAndPersonalData_SurviveLogoutAndRepeatedFirstAccess()
    {
        var session = await SignInAsync("returning-owner");
        var family = await CreateFamilyAsync(session, "Existing social family");
        var workspace = await CreateWorkspaceAsync(session);
        var details = Saldo360.Domain.Accounts.AccountDetails.TryCreate("Existing account", "Synthetic", "Checking", "PF", null, 123.45m, new DateOnly(2026, 10, 1), out _)!;
        var account = Saldo360.Domain.Accounts.FinancialAccount.Create(workspace.Id, details, workspace.OwnerUserId, DateTimeOffset.UtcNow);
        using (var db = Db()) { db.FinancialAccounts.Add(account); await db.SaveChangesAsync(); }

        Assert.Equal(HttpStatusCode.NoContent, (await SendWithCsrfAsync(HttpMethod.Post, "/auth/logout", session)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/me", session)).StatusCode);
        var nextSession = await SignInAsync("returning-owner");
        Assert.Equal(workspace.Id, (await CreateWorkspaceAsync(nextSession)).Id);
        Assert.Equal(workspace.Id, (await CreateWorkspaceAsync(nextSession)).Id);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{workspace.Id}/financial-dashboard", nextSession)).StatusCode);

        using var verify = Db();
        var preserved = Assert.Single(await verify.FinancialAccounts.ToListAsync());
        Assert.Equal(account.Id, preserved.Id); Assert.Equal(123.45m, preserved.OpeningBalance);
        Assert.Equal(workspace.Id, preserved.WorkspaceId);
        Assert.Equal(workspace.OwnerUserId, Assert.Single(await verify.FinancialWorkspaces.ToListAsync()).OwnerUserId);
        Assert.Equal(family.Id, Assert.Single(await verify.Families.ToListAsync()).Id);
        Assert.Single(await verify.FamilyMembers.ToListAsync());
        Assert.Single(await verify.Users.ToListAsync());
        Assert.Empty(await verify.FinancialTransactions.ToListAsync());
    }

    [Fact]
    public async Task TenConcurrentBootstraps_CreateExactlyOneEmptyPersonal_WithoutFamily()
    {
        var session = await SignInAsync("workspace-owner");
        using (var before = Db())
        {
            Assert.Empty(await before.FinancialWorkspaces.ToListAsync());
            Assert.Empty(await before.Families.ToListAsync());
        }
        var workspaces = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => CreateWorkspaceAsync(session)));
        Assert.Single(workspaces.Select(w => w.Id).Distinct());
        Assert.All(workspaces, w => Assert.Equal("Personal", w.Type));
        Assert.Equal(workspaces[0].Id, (await CreateWorkspaceAsync(session)).Id);
        using var db = Db();
        Assert.Single(await db.FinancialWorkspaces.ToListAsync());
        Assert.Empty(await db.Families.ToListAsync());
        Assert.Empty(await db.FamilyMembers.ToListAsync());
        Assert.Empty(await db.FinancialAccounts.ToListAsync());
        Assert.Empty(await db.FinancialCategories.ToListAsync());
        Assert.Empty(await db.FinancialTransactions.ToListAsync());
        Assert.Empty(await db.FinancialRecurrences.ToListAsync());
    }

    [Fact]
    public async Task Bootstrap_RequiresCsrf_AndDoesNotAcceptSharedParameters()
    {
        var session = await SignInAsync("workspace-owner");
        var withoutCsrf = await SendAsync(HttpMethod.Post, "/api/me/financial-workspace", session);
        Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);
        var shared = await SendWithCsrfAsync(HttpMethod.Post, "/api/me/financial-workspace", session, new { type = "Shared" });
        Assert.Equal(HttpStatusCode.BadRequest, shared.StatusCode);
        using var db = Db();
        Assert.Empty(await db.FinancialWorkspaces.ToListAsync());
    }

    [Fact]
    public async Task InactivePersonal_IsNotReplaced_AndStillParticipatesInUniqueness()
    {
        var session = await SignInAsync("inactive-owner");
        var root = await CreateWorkspaceAsync(session);
        using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_workspaces SET is_active=false WHERE id={root.Id}");
        var response = await SendWithCsrfAsync(HttpMethod.Post, "/api/me/financial-workspace", session);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var verify = Db();
        Assert.Single(await verify.FinancialWorkspaces.ToListAsync());
        verify.FinancialWorkspaces.Add(Saldo360.Domain.Workspaces.FinancialWorkspace.CreatePersonal(root.OwnerUserId, DateTimeOffset.UtcNow));
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, Assert.IsType<Npgsql.PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task SocialMembership_DoesNotGrantPersonalFinancialAccess_InEitherDirection()
    {
        var alice = await SignInAsync("alice");
        var bob = await SignInAsync("bob");
        var a = await CreateWorkspaceAsync(alice);
        var b = await CreateWorkspaceAsync(bob);
        Assert.NotEqual(a.Id, b.Id);
        var family = await CreateFamilyAsync(alice, "Social only");
        await JoinByInvitationAsync(alice, family.Id, bob);
        foreach (var resource in new[] { "accounts", "categories", "transactions", "planned-transactions", "recurrences", "credit-cards", "debts", "financial-goals", "planning-scenarios", "financial-documents/inbox" })
        {
            var deniedA = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{a.Id}/{resource}", bob);
            var deniedB = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{b.Id}/{resource}", alice);
            Assert.Equal(HttpStatusCode.NotFound, deniedA.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, deniedB.StatusCode);
        }
        Assert.Equal(a.Id, (await CreateWorkspaceAsync(alice)).Id);
        Assert.Equal(b.Id, (await CreateWorkspaceAsync(bob)).Id);
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Workspaces;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Workspaces")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialResetEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static string Path(Guid w) => $"/api/financial-workspaces/{w}/financial-reset";
    private async Task<string> Fingerprint(Guid w)
    {
        await using var db = Db();
        return await FinancialResetFixture.FingerprintAsync(db, w);
    }
    [Fact]
    public async Task CompleteWorkspace_ResetIsIsolatedAtomicIdempotent_AndPreservesPersonalAndSocialData()
    {
        var a = await SignInAsync("reset-a"); var b = await SignInAsync("reset-b");
        var wa = await CreateWorkspaceAsync(a); var wb = await CreateWorkspaceAsync(b);
        await using (var seed = Db()) { await FinancialResetFixture.SeedAsync(seed, wa.Id, wa.OwnerUserId); await FinancialResetFixture.SeedAsync(seed, wb.Id, wb.OwnerUserId); }
        var family = await CreateFamilyAsync(a, "Social"); await JoinByInvitationAsync(a, family.Id, b);
        await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/invitations", a);
        var beforeB = await Fingerprint(wb.Id); var beforeA = await Fingerprint(wa.Id);
        int sessionsBefore, keysBefore;
        await using (var before = Db()) { sessionsBefore=await before.UserSessions.CountAsync(); keysBefore=await before.DataProtectionKeys.CountAsync(); }
        var preview = await SendAsync(HttpMethod.Get, Path(wa.Id) + "/preview", a);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode); Assert.True(preview.Headers.CacheControl!.NoStore);
        var report = await ReadAsync<JsonElement>(preview);
        Assert.Equal(48, report.GetProperty("counts").EnumerateObject().Count());
        foreach(var forbidden in new[] { "synthetic.pdf", "synthetic-email", "***0000", "totalAmount", "lookupHash", "storageReference", "protectedNormalizedValue" })
            Assert.DoesNotContain(forbidden,report.GetRawText());
        Assert.All(report.GetProperty("counts").EnumerateObject(), c => Assert.True(c.Value.GetInt64() > 0, c.Name));
        Assert.Equal(beforeA, await Fingerprint(wa.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, Path(wa.Id) + "/preview", b)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, Path(wa.Id), b, new { confirmation = "APAGAR ORÇAMENTO" })).StatusCode);
        var reset = await SendWithCsrfAsync(HttpMethod.Post, Path(wa.Id), a, new { confirmation = "APAGAR ORÇAMENTO" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode); Assert.True(reset.Headers.CacheControl!.NoStore);
        Assert.Equal("ResetCompleted", (await ReadAsync<JsonElement>(reset)).GetProperty("status").GetString());
        Assert.Equal(beforeB, await Fingerprint(wb.Id));
        var after = await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, Path(wa.Id) + "/preview", a));
        Assert.All(after.GetProperty("counts").EnumerateObject(), c => Assert.Equal(0, c.Value.GetInt64()));
        var agenda=await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get,$"/api/financial-workspaces/{wa.Id}/financial-calendar?from=2026-10-01&through=2026-10-31",a));
        Assert.Equal(0,agenda.GetProperty("items").GetArrayLength());
        var planning=await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get,$"/api/financial-workspaces/{wa.Id}/financial-planning/six-months?from=2026-10",a));
        Assert.Equal(6,planning.GetProperty("months").GetArrayLength());
        Assert.Equal(JsonValueKind.Null,planning.GetProperty("summary").GetProperty("lowestClosingBalance").ValueKind);
        var again = await ReadAsync<JsonElement>(await SendWithCsrfAsync(HttpMethod.Post, Path(wa.Id), a, new { confirmation = "APAGAR ORÇAMENTO" }));
        Assert.Equal("AlreadyEmpty", again.GetProperty("status").GetString());
        await using var db = Db();
        Assert.Equal(2, await db.FinancialWorkspaces.CountAsync());
        Assert.Equal(2,await db.Users.CountAsync()); Assert.Equal(sessionsBefore,await db.UserSessions.CountAsync()); Assert.Equal(keysBefore,await db.DataProtectionKeys.CountAsync());
        Assert.Equal(wa.OwnerUserId, (await db.FinancialWorkspaces.SingleAsync(x => x.Id == wa.Id)).OwnerUserId);
        Assert.Equal(2, await db.ServiceAccounts.CountAsync()); Assert.Equal(2, await db.ServiceAccountIdentifiers.CountAsync());
        Assert.Single(await db.Families.ToListAsync()); Assert.Equal(2, await db.FamilyMembers.CountAsync()); Assert.Equal(2, await db.FamilyInvitations.CountAsync());
        Assert.Equal("reset-a", (await GetMeAsync(a)).Subject);
        // A fresh command on the same session/workspace must work after the reset epoch changes.
        var fresh = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{wa.Id}/accounts", a,
            new { name = "Fresh start", type = "Checking", financialScope = "PF" });
        Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
        Assert.Equal(beforeB, await Fingerprint(wb.Id));
    }
    [Fact]
    public async Task OnlyActivePersonalOwner_CanReset()
    {
        var session = await SignInAsync("restricted-owner"); var w = await CreateWorkspaceAsync(session);
        await using (var db = Db()) await FinancialResetFixture.SeedAsync(db,w.Id,w.OwnerUserId);
        await using (var db = Db()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_workspaces SET type='Shared' WHERE id={w.Id}");
        Assert.Equal(HttpStatusCode.Conflict,(await SendAsync(HttpMethod.Get,Path(w.Id)+"/preview",session)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await SendWithCsrfAsync(HttpMethod.Post,Path(w.Id),session,new { confirmation="APAGAR ORÇAMENTO" })).StatusCode);
        await using (var db = Db()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_workspaces SET type='Personal', is_active=false WHERE id={w.Id}");
        Assert.Equal(HttpStatusCode.Conflict,(await SendWithCsrfAsync(HttpMethod.Post,Path(w.Id),session,new { confirmation="APAGAR ORÇAMENTO" })).StatusCode);
        await using (var db = Db()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_workspaces SET is_active=true WHERE id={w.Id}");
        var response = await SendWithCsrfAsync(HttpMethod.Post,Path(w.Id),session,new { confirmation="APAGAR ORÇAMENTO" });
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        await using var verify=Db(); Assert.Equal(0,await verify.FinancialAccounts.CountAsync()); Assert.Single(await verify.Set<FinancialResetAudit>().ToListAsync());
    }

    [Fact]
    public async Task HttpSecurity_RejectsMissingCsrfWrongConfirmationExtraFieldsAndOversizedBody()
    {
        var a = await SignInAsync("security"); var w = await CreateWorkspaceAsync(a);
        var anonymous = await Client.GetAsync(Path(w.Id) + "/preview");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); Assert.True(anonymous.Headers.CacheControl!.NoStore);
        var noCsrf = await SendAsync(HttpMethod.Post, Path(w.Id), a);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode); Assert.True(noCsrf.Headers.CacheControl!.NoStore);
        foreach (var body in new object[] { new { confirmation = "apagar orçamento" }, new { confirmation = "APAGAR ORÇAMENTO", extra = true } })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendWithCsrfAsync(HttpMethod.Post, Path(w.Id), a, body)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SendWithCsrfAsync(HttpMethod.Post, Path(w.Id), a, new { confirmation = new string('x', 2000) })).StatusCode);
    }
}

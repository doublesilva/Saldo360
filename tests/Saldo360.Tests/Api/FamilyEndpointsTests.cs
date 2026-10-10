using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>Endpoints de família sobre PostgreSQL real, com sessões criadas pelo endpoint de teste.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FamilyEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private const string OtherIssuer = "https://outro-provedor.test.invalid";

    [Fact]
    public async Task Create_ReturnsFamilyWithCreatorAsSingleOwner_AndPersistsIt()
    {
        var session = await SignInAsync("maria");

        var response = await PostFamilyAsync(session, new { name = "  Família Silva  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var family = await ReadAsync<FamilyDto>(response);
        Assert.Equal("Família Silva", family.Name);
        Assert.Equal($"/api/families/{family.Id}", response.Headers.Location!.ToString());
        Assert.Equal("Owner", family.MyRole);
        var owner = Assert.Single(family.Members);
        Assert.Equal(("Maria Silva", "Owner", true), (owner.DisplayName, owner.Role, owner.IsCurrentUser));

        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        var members = await db.FamilyMembers.Where(m => m.FamilyId == family.Id).ToListAsync();
        Assert.Equal("Owner", Assert.Single(members).Role.ToString());
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Create_WithoutCsrfToken_IsRejected_AndNothingIsPersisted()
    {
        var session = await SignInAsync();

        var response = await SendJsonAsync(HttpMethod.Post, "/api/families", session, csrf: null, new { name = "Sem CSRF" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoFamiliesAsync();
    }

    [Fact]
    public async Task Create_WithCsrfTokenFromAnotherSession_IsRejected()
    {
        var attacker = await SignInAsync("atacante");
        var (attackerCsrfCookie, attackerToken) = await GetCsrfAsync(attacker);
        var victim = await SignInAsync("vitima");

        var response = await SendJsonAsync(
            HttpMethod.Post, "/api/families", $"{victim}; {attackerCsrfCookie}", attackerToken, new { name = "Forjada" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoFamiliesAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("     ")]
    [InlineData(null)]
    public async Task Create_WithoutName_ReturnsValidationError(string? name)
    {
        var session = await SignInAsync();

        var response = await PostFamilyAsync(session, new { name });

        await AssertNameValidationErrorAsync(response);
    }

    [Fact]
    public async Task Create_WithNameOver100Characters_ReturnsValidationError()
    {
        var session = await SignInAsync();

        var response = await PostFamilyAsync(session, new { name = new string('a', 101) });

        await AssertNameValidationErrorAsync(response);
    }

    [Fact]
    public async Task Create_WithNameOf100Characters_IsAccepted()
    {
        var session = await SignInAsync();

        var response = await PostFamilyAsync(session, new { name = new string('a', 100) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsClientSuppliedIdentityOrRole()
    {
        var session = await SignInAsync();

        var response = await PostFamilyAsync(session, new
        {
            name = "Tentativa",
            userId = Guid.CreateVersion7(),
            issuer = OtherIssuer,
            subject = "outra-pessoa",
            role = "Member",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoFamiliesAsync();
    }

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/families")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/api/families/{Guid.CreateVersion7()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/api/families", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task FamiliesOfDifferentUsers_AreIsolated()
    {
        var ana = await SignInAsync("ana");
        var bruno = await SignInAsync("bruno");
        var anaFamily = await ReadAsync<FamilyDto>(await PostFamilyAsync(ana, new { name = "Família da Ana" }));
        var brunoFamily = await ReadAsync<FamilyDto>(await PostFamilyAsync(bruno, new { name = "Família do Bruno" }));

        var anaList = await ListAsync(ana);
        var brunoList = await ListAsync(bruno);
        Assert.Equal([anaFamily.Id], anaList.Select(f => f.Id));
        Assert.Equal([brunoFamily.Id], brunoList.Select(f => f.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/families/{brunoFamily.Id}", ana)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/families/{anaFamily.Id}", bruno)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/families/{anaFamily.Id}", ana)).StatusCode);
    }

    [Fact]
    public async Task Get_NonexistentFamily_Returns404()
    {
        var session = await SignInAsync();

        var response = await SendAsync(HttpMethod.Get, $"/api/families/{Guid.CreateVersion7()}", session);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Responses_DoNotExposeOidcIdentityOrInternalUserIds()
    {
        var session = await SignInAsync("sub-secreto-da-maria");
        var created = await PostFamilyAsync(session, new { name = "Família" });
        var family = await ReadAsync<FamilyDto>(created);

        foreach (var body in new[]
        {
            await created.Content.ReadAsStringAsync(),
            await (await SendAsync(HttpMethod.Get, "/api/families", session)).Content.ReadAsStringAsync(),
            await (await SendAsync(HttpMethod.Get, $"/api/families/{family.Id}", session)).Content.ReadAsStringAsync(),
        })
        {
            Assert.DoesNotContain("sub-secreto-da-maria", body);
            Assert.DoesNotContain(Saldo360ApiFactory.TestAuthority, body);
            Assert.DoesNotContain("maria@example.com", body);
            Assert.DoesNotContain("userId", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("issuer", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("subject", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SameSubjectInDifferentIssuers_AreDifferentUsers()
    {
        var fromDefault = await SignInAsync("mesmo-subject");
        var fromOther = await SignInAsync("mesmo-subject", OtherIssuer);
        await PostFamilyAsync(fromDefault, new { name = "Família do provedor padrão" });

        Assert.Single(await ListAsync(fromDefault));
        Assert.Empty(await ListAsync(fromOther));

        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        Assert.Equal(2, await db.Users.CountAsync(u => u.Subject == "mesmo-subject"));
    }

    [Fact]
    public async Task RepeatedLogins_DoNotDuplicateTheInternalUser()
    {
        for (var login = 0; login < 3; login++)
        {
            var session = await SignInAsync("recorrente");
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/families", session)).StatusCode);
        }

        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task User_CanCreateAndListSeveralFamilies()
    {
        var session = await SignInAsync();
        await PostFamilyAsync(session, new { name = "Família B" });
        await PostFamilyAsync(session, new { name = "Família A" });

        var list = await ListAsync(session);

        Assert.Equal(["Família A", "Família B"], list.Select(f => f.Name));
        Assert.All(list, f => Assert.Equal(("Owner", 1), (f.MyRole, f.MemberCount)));
    }

    private async Task<List<FamilySummaryDto>> ListAsync(string session)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/families", session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<List<FamilySummaryDto>>(response);
    }

    private async Task AssertNameValidationErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("name", out _));
        await AssertNoFamiliesAsync();
    }

    private async Task AssertNoFamiliesAsync()
    {
        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        Assert.Equal(0, await db.Families.CountAsync());
        Assert.Equal(0, await db.FamilyMembers.CountAsync());
    }
}

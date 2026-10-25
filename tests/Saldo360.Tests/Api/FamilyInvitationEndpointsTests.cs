using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>Endpoints de convite sobre PostgreSQL real.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
public sealed class FamilyInvitationEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private const string AcceptUrl = "/api/family-invitations/accept";

    [Fact]
    public async Task Owner_CreatesInvitation_TokenReturnedOnlyOnCreation()
    {
        var (owner, family) = await OwnerWithFamilyAsync();

        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/invitations", owner);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var invitation = await ReadAsync<InvitationDto>(response);
        Assert.Equal(43, invitation.Token.Length); // 32 bytes em base64url
        Assert.InRange(invitation.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromHours(47.9), TimeSpan.FromHours(48));

        // O detalhe da família não traz convites nem tokens.
        var details = await (await SendAsync(HttpMethod.Get, $"/api/families/{family.Id}", owner)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(invitation.Token, details);
        AssertTokenNotLogged(invitation.Token);
    }

    [Fact]
    public async Task WriteOperations_RequireCsrf()
    {
        var (owner, family) = await OwnerWithFamilyAsync();
        var invitation = await CreateInvitationAsync(owner, family.Id);
        var guest = await SignInAsync("convidado");

        var create = await SendJsonAsync(HttpMethod.Post, $"/api/families/{family.Id}/invitations", owner, csrf: null, body: null);
        var revoke = await SendJsonAsync(HttpMethod.Delete, $"/api/families/{family.Id}/invitations/{invitation.Id}", owner, csrf: null, body: null);
        var accept = await SendJsonAsync(HttpMethod.Post, AcceptUrl, guest, csrf: null, new { token = invitation.Token });

        Assert.Equal(
            [HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest],
            new[] { create.StatusCode, revoke.StatusCode, accept.StatusCode });
        await using var db = Db();
        Assert.Equal(1, await db.FamilyInvitations.CountAsync());
        Assert.Equal(1, await db.FamilyMembers.CountAsync());

        // O convite segue pendente e utilizável.
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(guest, invitation.Token)).StatusCode);
    }

    [Fact]
    public async Task Guest_AcceptsInvitation_AndSharesTheFamilyAsMember()
    {
        var (owner, family) = await OwnerWithFamilyAsync();
        var invitation = await CreateInvitationAsync(owner, family.Id);
        var guest = await SignInAsync("convidado");

        var response = await AcceptAsync(guest, invitation.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = await ReadAsync<AcceptDto>(response);
        Assert.False(accepted.AlreadyMember);
        Assert.Equal(family.Id, accepted.Family.Id);
        Assert.Equal("Member", accepted.Family.MyRole);
        Assert.Equal(["Owner", "Member"], accepted.Family.Members.Select(m => m.Role));
        Assert.Equal([false, true], accepted.Family.Members.Select(m => m.IsCurrentUser));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/families/{family.Id}", guest)).StatusCode);
        var ownerView = await ReadAsync<FamilyDto>(await SendAsync(HttpMethod.Get, $"/api/families/{family.Id}", owner));
        Assert.Equal(2, ownerView.Members.Count);
        AssertTokenNotLogged(invitation.Token);
    }

    [Fact]
    public async Task NonParticipant_StillHasNoAccess()
    {
        var (owner, family) = await OwnerWithFamilyAsync();
        var invitation = await CreateInvitationAsync(owner, family.Id);
        await AcceptAsync(await SignInAsync("convidado"), invitation.Token);
        var outsider = await SignInAsync("estranho");

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/families/{family.Id}", outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/invitations", outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendWithCsrfAsync(HttpMethod.Delete, $"/api/families/{family.Id}/invitations/{invitation.Id}", outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{Guid.CreateVersion7()}/invitations", outsider)).StatusCode);
    }

    [Fact]
    public async Task Member_CannotCreateOrRevokeInvitations()
    {
        var (owner, family) = await OwnerWithFamilyAsync();
        var pending = await CreateInvitationAsync(owner, family.Id);
        var member = await SignInAsync("membro");
        await AcceptAsync(member, (await CreateInvitationAsync(owner, family.Id)).Token);

        var create = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/invitations", member);
        var revoke = await SendWithCsrfAsync(HttpMethod.Delete, $"/api/families/{family.Id}/invitations/{pending.Id}", member);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        await using var db = Db();
        Assert.Null((await db.FamilyInvitations.SingleAsync(i => i.Id == pending.Id)).RevokedAt);
        Assert.Equal(2, await db.FamilyInvitations.CountAsync());
    }

    [Fact]
    public async Task UnavailableTokens_GetTheSameGenericAnswer_WithoutFamilyData()
    {
        var (owner, family) = await OwnerWithFamilyAsync("Família Secreta");
        var used = await CreateInvitationAsync(owner, family.Id);
        await AcceptAsync(await SignInAsync("primeiro"), used.Token);
        var revoked = await CreateInvitationAsync(owner, family.Id);
        await SendWithCsrfAsync(HttpMethod.Delete, $"/api/families/{family.Id}/invitations/{revoked.Id}", owner);
        var expired = await CreateInvitationAsync(owner, family.Id);
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE family_invitations SET created_at = now() - interval '50 hours', expires_at = now() - interval '2 hours' WHERE id = {expired.Id}");
        }

        var guest = await SignInAsync("segundo");
        var bodies = new List<string>();
        foreach (var token in new[] { used.Token, revoked.Token, expired.Token, "token-invalido", "" })
        {
            var response = await AcceptAsync(guest, token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            bodies.Add(Normalize(await response.Content.ReadAsStringAsync()));
        }

        Assert.Single(bodies.Distinct());
        Assert.DoesNotContain("Família Secreta", bodies[0]);
        Assert.DoesNotContain(family.Id.ToString(), bodies[0]);
        await using var verify = Db();
        Assert.Equal(2, await verify.FamilyMembers.CountAsync());
    }

    [Fact]
    public async Task ExistingMember_AcceptingKeepsRole_AndDoesNotConsume()
    {
        var (owner, family) = await OwnerWithFamilyAsync();
        var invitation = await CreateInvitationAsync(owner, family.Id);

        var response = await AcceptAsync(owner, invitation.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = await ReadAsync<AcceptDto>(response);
        Assert.True(accepted.AlreadyMember);
        Assert.Equal("Owner", accepted.Family.MyRole);
        Assert.Single(accepted.Family.Members);

        // Ainda pendente: outra pessoa consegue usar.
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(await SignInAsync("outra"), invitation.Token)).StatusCode);
    }

    [Fact]
    public async Task Accept_RejectsClientSuppliedRoleOrIdentity()
    {
        var (owner, family) = await OwnerWithFamilyAsync();
        var invitation = await CreateInvitationAsync(owner, family.Id);
        var guest = await SignInAsync("convidado");

        var response = await SendWithCsrfAsync(HttpMethod.Post, AcceptUrl, guest, new
        {
            token = invitation.Token,
            role = "Owner",
            userId = Guid.CreateVersion7(),
            subject = "outra-pessoa",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = Db();
        Assert.Null((await db.FamilyInvitations.SingleAsync()).ConsumedAt);
        Assert.Equal(1, await db.FamilyMembers.CountAsync());
    }

    [Fact]
    public async Task Revoke_PendingThenAgain_ThenConsumed()
    {
        var (owner, family) = await OwnerWithFamilyAsync();
        var pending = await CreateInvitationAsync(owner, family.Id);
        var consumed = await CreateInvitationAsync(owner, family.Id);
        await AcceptAsync(await SignInAsync("convidado"), consumed.Token);

        var first = await SendWithCsrfAsync(HttpMethod.Delete, $"/api/families/{family.Id}/invitations/{pending.Id}", owner);
        var again = await SendWithCsrfAsync(HttpMethod.Delete, $"/api/families/{family.Id}/invitations/{pending.Id}", owner);
        var used = await SendWithCsrfAsync(HttpMethod.Delete, $"/api/families/{family.Id}/invitations/{consumed.Id}", owner);
        var unknown = await SendWithCsrfAsync(HttpMethod.Delete, $"/api/families/{family.Id}/invitations/{Guid.CreateVersion7()}", owner);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, used.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(await SignInAsync("tarde"), pending.Token)).StatusCode);
    }

    [Fact]
    public async Task Invitation_OfAnotherFamily_CannotBeRevokedThroughOwnFamily()
    {
        var (ownerA, familyA) = await OwnerWithFamilyAsync("A", "dono-a");
        var (ownerB, familyB) = await OwnerWithFamilyAsync("B", "dono-b");
        var invitationB = await CreateInvitationAsync(ownerB, familyB.Id);

        var response = await SendWithCsrfAsync(HttpMethod.Delete, $"/api/families/{familyA.Id}/invitations/{invitationB.Id}", ownerA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var db = Db();
        Assert.Null((await db.FamilyInvitations.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsync($"/api/families/{Guid.CreateVersion7()}/invitations", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsync(AcceptUrl, null)).StatusCode);
    }

    private async Task<(string Session, FamilyDto Family)> OwnerWithFamilyAsync(string name = "Família Silva", string subject = "dona")
    {
        var session = await SignInAsync(subject);
        var family = await ReadAsync<FamilyDto>(await PostFamilyAsync(session, new { name }));
        return (session, family);
    }

    private async Task<InvitationDto> CreateInvitationAsync(string session, Guid familyId)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/invitations", session);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InvitationDto>(response);
    }

    private Task<HttpResponseMessage> AcceptAsync(string session, string token) =>
        SendWithCsrfAsync(HttpMethod.Post, AcceptUrl, session, new { token });

    private void AssertTokenNotLogged(string token) =>
        Assert.DoesNotContain(Factory.Logs.Entries, entry => entry.Contains(token));

    // O traceId muda a cada resposta; o resto do corpo deve ser idêntico.
    private static string Normalize(string problem)
    {
        using var document = JsonDocument.Parse(problem);
        return string.Join("|", document.RootElement.EnumerateObject()
            .Where(p => p.Name != "traceId")
            .Select(p => $"{p.Name}={p.Value}"));
    }

    private sealed record InvitationDto(Guid Id, string Token, DateTimeOffset ExpiresAt);

    private sealed record AcceptDto(bool AlreadyMember, FamilyDto Family);
}

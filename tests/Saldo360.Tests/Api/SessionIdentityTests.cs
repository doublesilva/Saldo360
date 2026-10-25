using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Api.Authentication;
using Saldo360.Infrastructure.Authentication;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>Identidade externa (issuer + subject) nas sessões persistidas.</summary>
[Trait("Category", "CORE")]
public sealed class SessionIdentityTests : ApiTestBase
{
    private const string OtherIssuer = "https://outro-provedor.test.invalid";

    [Fact]
    public async Task SameSubject_FromDifferentIssuers_AreDistinctIdentities()
    {
        var fromDefault = await SignInAsync("compartilhado", Saldo360ApiFactory.TestAuthority);
        var fromOther = await SignInAsync("compartilhado", OtherIssuer);

        var meDefault = await GetMeAsync(fromDefault);
        var meOther = await GetMeAsync(fromOther);

        Assert.Equal(meDefault.Subject, meOther.Subject);
        Assert.NotEqual(meDefault.Issuer, meOther.Issuer);
        Assert.NotEqual(
            new ExternalIdentity(meDefault.Issuer, meDefault.Subject),
            new ExternalIdentity(meOther.Issuer, meOther.Subject));

        await WithDbAsync(async db =>
        {
            var sessions = await db.UserSessions.Where(s => s.Subject == "compartilhado").ToListAsync();
            Assert.Equal(2, sessions.Count);
            Assert.Equal([Saldo360ApiFactory.TestAuthority, OtherIssuer], sessions.Select(s => s.Issuer).Order());
        });

        // Encerrar uma não afeta a outra.
        var (csrfCookie, csrfToken) = await GetCsrfAsync(fromDefault);
        await SendAsync(HttpMethod.Post, "/auth/logout", $"{fromDefault}; {csrfCookie}", csrfToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/me", fromDefault)).StatusCode);
        Assert.Equal(OtherIssuer, (await GetMeAsync(fromOther)).Issuer);
    }

    [Theory]
    [InlineData("Maria.Silva@Exemplo.COM|42")]
    [InlineData("  espaços nas pontas  ")]
    [InlineData("AbCdEf0123456789")]
    public async Task NonGuidSubject_IsKeptOpaqueAndUnchanged(string subject)
    {
        var session = await SignInAsync(subject);

        var me = await GetMeAsync(session);

        Assert.Equal(subject, me.Subject);
        Assert.Equal(subject, me.Id);
        await WithDbAsync(async db => Assert.Equal(subject, (await db.UserSessions.SingleAsync()).Subject));
    }

    [Fact]
    public async Task LegacySession_WithoutIssuer_IsRejectedAndRemoved()
    {
        var session = await SignInAsync();
        // Simula uma linha criada antes da migration AddIssuerToUserSessions.
        await UpdateSessionAsync(entry => entry.Property(s => s.Issuer).CurrentValue = null);

        var response = await SendAsync(HttpMethod.Get, "/api/me", session);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoSessionsAsync();
    }

    [Fact]
    public async Task LegacyTicket_WithoutIssClaim_IsRejectedAndRemoved()
    {
        var session = await SignInAsync();
        // Ticket antigo, gravado só com sub (sem iss), embora o registro tenha issuer.
        var legacyTicket = ProtectTicket(new Claim("sub", "user-123"), new Claim("name", "Maria Silva"));
        await UpdateSessionAsync(entry => entry.Entity.ProtectedTicket = legacyTicket);

        var response = await SendAsync(HttpMethod.Get, "/api/me", session);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoSessionsAsync();
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("subject")]
    public async Task Session_WhoseRecordDisagreesWithTicket_IsRejectedAndRemoved(string field)
    {
        var session = await SignInAsync();
        await UpdateSessionAsync(entry =>
        {
            if (field == "issuer")
            {
                entry.Property(s => s.Issuer).CurrentValue = OtherIssuer;
            }
            else
            {
                entry.Property(s => s.Subject).CurrentValue = "outro-usuario";
            }
        });

        var response = await SendAsync(HttpMethod.Get, "/api/me", session);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoSessionsAsync();
    }

    [Fact]
    public async Task Session_WhoseTicketDiffersOnlyInCase_IsRejected()
    {
        var session = await SignInAsync("usuario-abc");
        // Comparação ordinal: mudar só a caixa já é outra identidade.
        await UpdateSessionAsync(entry => entry.Property(s => s.Subject).CurrentValue = "USUARIO-ABC");

        var response = await SendAsync(HttpMethod.Get, "/api/me", session);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task UpdateSessionAsync(Action<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<UserSession>> change)
    {
        await WithDbAsync(async db =>
        {
            var row = await db.UserSessions.SingleAsync();
            change(db.Entry(row));
            await db.SaveChangesAsync();
        });
    }

    private Task AssertNoSessionsAsync() =>
        WithDbAsync(async db => Assert.Empty(await db.UserSessions.ToListAsync()));

    private byte[] ProtectTicket(params Claim[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "legacy", "name", "role"));
        var ticket = new AuthenticationTicket(principal, AuthenticationSetup.CookieScheme);
        var protector = Factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(DatabaseTicketStore.ProtectorPurpose);
        return protector.Protect(TicketSerializer.Default.Serialize(ticket));
    }
}

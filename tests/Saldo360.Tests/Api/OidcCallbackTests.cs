using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Api.Authentication;
using Saldo360.Application.Users;
using Saldo360.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Tests.Api;

/// <summary>
/// Fluxo OIDC completo contra o <see cref="FakeOidcProvider"/>: /auth/login → callback /signin-oidc
/// (validação do ID token, userinfo e ClaimActions do handler) → sessão → /api/me.
/// Diferente do endpoint de teste, aqui a claim iss passa pela filtragem real do middleware.
/// </summary>
[Trait("Category", "CORE")]
[Trait("Area", "Security")]
public sealed class OidcCallbackTests : ApiTestBase
{
    private const string SyntheticCpf = "52998224725";

    [Theory]
    [InlineData(false, "TokenExpired")]
    [InlineData(true, "StoredAccessTokenMissing")]
    public async Task ValidApplicationSessionDoesNotHideAnExpiredOrLostProfileToken(bool remove, string diagnostic)
    {
        Factory.OidcProvider.ProfileTokenScenario = "valid";
        Factory.OidcProvider.IdTokenClaims["sub"] = Subject; Factory.OidcProvider.UserInfo["sub"] = Subject;
        var session = FindCookie(await CompleteLoginAsync(), SessionCookieName);
        Factory.OidcProvider.ProfileTokenScenario = "expired";
        var expired = Factory.OidcProvider.CreateProfileToken();
        await WithDbAsync(async db =>
        {
            var row = await db.UserSessions.SingleAsync();
            var protector = Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(DatabaseTicketStore.ProtectorPurpose);
            var ticket = TicketSerializer.Default.Deserialize(protector.Unprotect(row.ProtectedTicket))!;
            ticket.Properties.StoreTokens(remove ? [] : [new AuthenticationToken { Name = "access_token", Value = expired }]);
            row.ProtectedTicket = protector.Protect(TicketSerializer.Default.Serialize(ticket));
            await db.SaveChangesAsync();
        });
        var me = await GetMeAsync(session); // HTTP 200: financial session is still valid.
        Assert.Equal("SessionInvalid", me.CpfStatus); Assert.Equal(diagnostic, me.CpfDiagnosticCode);
        Assert.Equal(0, Factory.ProfileGateway.Calls);
        Assert.DoesNotContain(expired, JsonSerializer.Serialize(me));
        Assert.DoesNotContain(Factory.Logs.Entries, value => value.Contains(expired));
    }

    [Theory]
    [InlineData("azp")]
    [InlineData("both-client")]
    [InlineData("multiple-audiences")]
    public async Task EligibleAutheliaClaimShapesKeepProfileTokenOnlyInProtectedSession(string shape)
    {
        Factory.OidcProvider.ProfileTokenScenario = shape;
        Factory.OidcProvider.IdTokenClaims["sub"] = Subject; Factory.OidcProvider.UserInfo["sub"] = Subject;
        Factory.ProfileGateway.Status = HttpStatusCode.OK;
        Factory.ProfileGateway.Body = JsonSerializer.Serialize(new { issuer = FakeOidcProvider.Issuer, subject = Subject, cpfRegistered = true, cpf = SyntheticCpf });
        var callback = await CompleteLoginAsync();
        var session = FindCookie(callback, SessionCookieName);
        var me = await GetMeAsync(session);
        Assert.Equal("Available", me.CpfStatus); Assert.Equal("ProfileAvailable", me.CpfDiagnosticCode);
        Assert.Equal(Factory.OidcProvider.LastAccessToken, Factory.ProfileGateway.LastBearer);
        var stages = Factory.Logs.StructuredEntries.Where(e => e.EventId.Id is 17080 or 17082).ToArray();
        Assert.Equal(new[] { "TokenResponseReceived", "AccessTokenValidation", "TicketReceived" }, stages.Select(e => e.Properties["Stage"]));
        Assert.Equal(true, stages[0].Properties["AccessTokenPresent"]);
        Assert.Equal(true, stages[0].Properties["BearerType"]);
        Assert.Equal("AccessTokenValidated", stages[1].Properties["Code"]);
        Assert.Equal(true, stages[2].Properties["AccessTokenRetained"]);
        Assert.All(stages, entry => Assert.Null(entry.Exception));
        Assert.Contains(Factory.Logs.StructuredEntries, entry => entry.EventId.Id == 17081 && Equals(entry.Properties["Code"], "ProfileAvailable"));
        await WithDbAsync(async db =>
        {
            var protector = Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(DatabaseTicketStore.ProtectorPurpose);
            var ticket = TicketSerializer.Default.Deserialize(protector.Unprotect((await db.UserSessions.SingleAsync()).ProtectedTicket))!;
            Assert.Equal(Factory.OidcProvider.LastAccessToken, Assert.Single(ticket.Properties.GetTokens()).Value);
            Assert.Equal("AccessTokenValidated", ticket.Properties.Items[ProfileAccessTokenValidation.SessionDiagnostic]);
            Assert.DoesNotContain(SyntheticCpf, JsonSerializer.Serialize(ticket.Properties.Items));
        });
        Assert.DoesNotContain(Factory.Logs.Entries, entry => entry.Contains(SyntheticCpf) || entry.Contains(Factory.OidcProvider.LastAccessToken!));
        Assert.DoesNotContain(SyntheticCpf, JsonSerializer.Serialize(me));
        Assert.DoesNotContain(Factory.OidcProvider.LastAccessToken!, session);
    }

    [Theory]
    [InlineData("valid", "Available", "ProfileAvailable")]
    [InlineData("absent", "NotRegistered", "GatewayCpfNotRegistered")]
    [InlineData("denied", "AccessDenied", "GatewayForbidden")]
    [InlineData("unauthorized", "SessionInvalid", "GatewayUnauthorized")]
    [InlineData("missing-grant", "AccessDenied", "GatewayCpfGrantMissing")]
    [InlineData("wrong-subject", "SessionInvalid", "GatewayIdentityMismatch")]
    [InlineData("wrong-issuer", "SessionInvalid", "GatewayIdentityMismatch")]
    [InlineData("malformed", "Unavailable", "GatewayInvalidResponse")]
    [InlineData("bad-cpf", "Unavailable", "GatewayCpfInvalid")]
    [InlineData("network", "Unavailable", "GatewayUnavailable")]
    [InlineData("unavailable", "Unavailable", "GatewayUnavailable")]
    public async Task ProfileUsesOnlySessionAccessTokenAndNeverExposesOrPersistsCpf(string scenario, string expected, string diagnostic)
    {
        Factory.OidcProvider.ProfileTokenScenario = "valid";
        Factory.OidcProvider.IdTokenClaims["sub"] = Subject; Factory.OidcProvider.UserInfo["sub"] = Subject;
        Factory.OidcProvider.UserInfo["cpf"] = SyntheticCpf; // This claim must never enter the session.
        Factory.ProfileGateway.Status = scenario switch
        { "denied" => HttpStatusCode.Forbidden, "unauthorized" => HttpStatusCode.Unauthorized, "unavailable" => HttpStatusCode.ServiceUnavailable, _ => HttpStatusCode.OK };
        Factory.ProfileGateway.ThrowNetwork = scenario == "network";
        Factory.ProfileGateway.Body = scenario switch
        {
            "absent" => JsonSerializer.Serialize(new { issuer = FakeOidcProvider.Issuer, subject = Subject, cpfRegistered = false }),
            "missing-grant" => JsonSerializer.Serialize(new { issuer = FakeOidcProvider.Issuer, subject = Subject }),
            "malformed" => "not-json",
            _ => JsonSerializer.Serialize(new { issuer = scenario == "wrong-issuer" ? "https://other.invalid" : FakeOidcProvider.Issuer, subject = scenario == "wrong-subject" ? "other" : Subject, cpfRegistered = true, cpf = scenario == "bad-cpf" ? "11111111111" : SyntheticCpf })
        };
        var callback = await CompleteLoginAsync(); Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var session = FindCookie(callback, SessionCookieName);
        var response = await SendAsync(HttpMethod.Get, "/api/me", session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(expected, JsonDocument.Parse(body).RootElement.GetProperty("cpfStatus").GetString());
        Assert.Equal(diagnostic, JsonDocument.Parse(body).RootElement.GetProperty("cpfDiagnosticCode").GetString());
        Assert.DoesNotContain(SyntheticCpf, body); Assert.DoesNotContain(Factory.OidcProvider.LastAccessToken!, body);
        Assert.Equal(Factory.OidcProvider.LastAccessToken, Factory.ProfileGateway.LastBearer);
        Assert.Equal(1, Factory.ProfileGateway.Calls);
        await WithDbAsync(async db =>
        {
            var row = await db.UserSessions.SingleAsync();
            var protector = Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(DatabaseTicketStore.ProtectorPurpose);
            var ticket = TicketSerializer.Default.Deserialize(protector.Unprotect(row.ProtectedTicket))!;
            Assert.DoesNotContain(ticket.Principal.Claims, c => c.Type.Contains("cpf", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("access_token", Assert.Single(ticket.Properties.GetTokens()).Name);
            Assert.Null(ticket.Properties.GetTokenValue("id_token")); Assert.Null(ticket.Properties.GetTokenValue("refresh_token"));
            Assert.Equal(FakeOidcProvider.Issuer, row.Issuer); Assert.Equal(Subject, row.Subject);
        });
        Assert.DoesNotContain(Factory.Logs.Entries, text => text.Contains(SyntheticCpf) || text.Contains(Factory.OidcProvider.LastAccessToken!));
        Assert.DoesNotContain(SyntheticCpf, session);
    }

    [Theory]
    [InlineData("audience", "AccessDenied", "AudienceMismatch")]
    [InlineData("client", "AccessDenied", "ClientMismatch")]
    [InlineData("id-token", "AccessDenied", "JwtTypeInvalid")]
    [InlineData("cpf-claim", "AccessDenied", "SensitiveClaimsPresent")]
    [InlineData("signature", "SessionInvalid", "SigningKeyUnavailable")]
    [InlineData("expired", "SessionInvalid", "TokenExpired")]
    [InlineData("subject", "SessionInvalid", "SubjectMismatch")]
    [InlineData("issuer", "SessionInvalid", "IssuerMismatch")]
    [InlineData("opaque", "AccessDenied", "AccessTokenMalformed")]
    [InlineData("signature-invalid", "SessionInvalid", "SignatureInvalid")]
    [InlineData("future", "SessionInvalid", "TokenNotYetValid")]
    [InlineData("algorithm", "SessionInvalid", "AlgorithmInvalid")]
    [InlineData("conflicting-azp", "AccessDenied", "ClientMismatch")]
    public async Task IneligibleAccessTokenNeverLeavesSaldoAndDoesNotBreakLogin(string scenario, string expected, string diagnostic)
    {
        Factory.OidcProvider.ProfileTokenScenario = scenario;
        Factory.OidcProvider.IdTokenClaims["sub"] = Subject; Factory.OidcProvider.UserInfo["sub"] = Subject;
        var callback = await CompleteLoginAsync(); Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var session = FindCookie(callback, SessionCookieName);
        var me = await GetMeAsync(session);
        Assert.Equal(expected, me.CpfStatus); Assert.Equal(diagnostic, me.CpfDiagnosticCode); Assert.Equal(Subject, me.Subject); Assert.Equal(0, Factory.ProfileGateway.Calls);
        Assert.DoesNotContain(Factory.Logs.Entries, text => text.Contains(SyntheticCpf) || text.Contains(Factory.OidcProvider.LastAccessToken!));
        await WithDbAsync(async db =>
        {
            var protector = Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(DatabaseTicketStore.ProtectorPurpose);
            var ticket = TicketSerializer.Default.Deserialize(protector.Unprotect((await db.UserSessions.SingleAsync()).ProtectedTicket))!;
            Assert.Empty(ticket.Properties.GetTokens());
            Assert.Equal(diagnostic, ticket.Properties.Items[ProfileAccessTokenValidation.SessionDiagnostic]);
        });
    }

    private const string Subject = "Usuário-Não-Guid|ÇÃO-42";

    [Fact]
    [Trait("Profile", "FAST")]
    public async Task ValidatedIssuer_SurvivesClaimFiltering_AndIsStoredWithTheSession()
    {
        Factory.OidcProvider.IdTokenClaims["sub"] = Subject;
        Factory.OidcProvider.UserInfo["sub"] = Subject;
        Factory.OidcProvider.UserInfo["name"] = "Ana Souza";
        Factory.OidcProvider.UserInfo["preferred_username"] = "ana";
        Factory.OidcProvider.UserInfo["email"] = "ana@example.com";
        // Um iss vindo do userinfo não pode substituir o do ID token validado.
        Factory.OidcProvider.UserInfo["iss"] = "https://forjado.example";

        var callback = await CompleteLoginAsync();

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.ToString());
        var session = FindCookie(callback, SessionCookieName);

        var me = await GetMeAsync(session);
        Assert.Equal(FakeOidcProvider.Issuer, me.Issuer);
        Assert.Equal(Subject, me.Subject);
        Assert.Equal(Subject, me.Id);
        Assert.Equal("Ana Souza", me.DisplayName);
        Assert.Equal("ana", me.Username);
        Assert.Equal("ana@example.com", me.Email);

        await WithDbAsync(async db =>
        {
            var row = await db.UserSessions.SingleAsync();
            Assert.Equal(FakeOidcProvider.Issuer, row.Issuer);
            Assert.Equal(Subject, row.Subject);
        });

        Assert.DoesNotContain(Factory.Logs.Entries, entry =>
            entry.Contains(Saldo360ApiFactory.TestClientSecret) || entry.Contains("access-token-de-teste"));
    }

    [Fact]
    [Trait("Profile", "FAST")]
    public async Task IdTokenWithoutSubject_IsRejected_AndNoSessionIsCreated()
    {
        Factory.OidcProvider.UserInfo["name"] = "Sem Sub";

        var callback = await CompleteLoginAsync();

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.StartsWith("/?login=failed&ref=", callback.Headers.Location!.ToString());
        Assert.False(HasCookie(callback, SessionCookieName));
        await WithDbAsync(async db => Assert.Empty(await db.UserSessions.ToListAsync()));
    }

    [Fact]
    [Trait("Profile", "FAST")]
    public async Task LoginWithoutOptionalPar_UsesPkceAndTokenClientSecretPost()
    {
        // O provedor anuncia PAR, mas a aplicação usa o fluxo OIDC convencional com PKCE.
        Factory.OidcProvider.EnablePushedAuthorization = true;
        Factory.OidcProvider.IdTokenClaims["sub"] = "usuario-sem-par";
        Factory.OidcProvider.UserInfo["sub"] = "usuario-sem-par";

        var start = await StartLoginAsync();

        var authorize = QueryHelpers.ParseQuery(start.Location.Query);
        Assert.False(authorize.ContainsKey("request_uri"));
        Assert.Equal(FakeOidcProvider.ClientId, authorize["client_id"].ToString());
        Assert.Equal("S256", authorize["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(authorize["code_challenge"].ToString()));
        Assert.False(string.IsNullOrWhiteSpace(authorize["nonce"].ToString()));
        Assert.False(string.IsNullOrWhiteSpace(authorize["redirect_uri"].ToString()));

        var callback = await CallbackAsync(start, "code=code-de-teste");
        Assert.Equal("/", callback.Headers.Location!.ToString());
        Assert.Equal("usuario-sem-par", (await GetMeAsync(FindCookie(callback, SessionCookieName))).Subject);

        Assert.DoesNotContain(Factory.OidcProvider.Requests, request => request.Path == "/par");

        var token = Assert.Single(Factory.OidcProvider.Requests, request => request.Path == "/token");
        Assert.False(token.HasAuthorizationHeader, "token: não deve usar client_secret_basic");
        Assert.Equal(FakeOidcProvider.ClientId, token.Form["client_id"]);
        Assert.Equal(Saldo360ApiFactory.TestClientSecret, token.Form["client_secret"]);
        Assert.True(token.Form.ContainsKey("code_verifier"));
    }

    /// <summary>Inicia o login, simula o provedor devolvendo o code e chama o callback com os cookies de correlação.</summary>
    private async Task<HttpResponseMessage> CompleteLoginAsync() =>
        await CallbackAsync(await StartLoginAsync(), "code=code-de-teste");
}

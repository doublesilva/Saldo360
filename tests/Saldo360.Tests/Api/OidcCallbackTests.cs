using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Fluxo OIDC completo contra o <see cref="FakeOidcProvider"/>: /auth/login → callback /signin-oidc
/// (validação do ID token, userinfo e ClaimActions do handler) → sessão → /api/me.
/// Diferente do endpoint de teste, aqui a claim iss passa pela filtragem real do middleware.
/// </summary>
public sealed class OidcCallbackTests : ApiTestBase
{
    private const string Subject = "Usuário-Não-Guid|ÇÃO-42";

    [Fact]
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
    public async Task IdTokenWithoutSubject_IsRejected_AndNoSessionIsCreated()
    {
        Factory.OidcProvider.UserInfo["name"] = "Sem Sub";

        var callback = await CompleteLoginAsync();

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/?login=failed", callback.Headers.Location!.ToString());
        Assert.False(HasCookie(callback, SessionCookieName));
        await WithDbAsync(async db => Assert.Empty(await db.UserSessions.ToListAsync()));
    }

    /// <summary>Inicia o login, simula o provedor devolvendo o code e chama o callback com os cookies de correlação.</summary>
    private async Task<HttpResponseMessage> CompleteLoginAsync()
    {
        var login = await Client.GetAsync("/auth/login?returnUrl=%2F");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var authorize = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        Factory.OidcProvider.Nonce = authorize["nonce"];
        var correlationCookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        return await SendAsync(
            HttpMethod.Get,
            $"/signin-oidc?code=code-de-teste&state={Uri.EscapeDataString(authorize["state"]!)}",
            correlationCookies);
    }
}

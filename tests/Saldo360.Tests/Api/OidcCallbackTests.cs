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
        Assert.StartsWith("/?login=failed&ref=", callback.Headers.Location!.ToString());
        Assert.False(HasCookie(callback, SessionCookieName));
        await WithDbAsync(async db => Assert.Empty(await db.UserSessions.ToListAsync()));
    }

    [Fact]
    public async Task ParAndTokenEndpoints_ReceiveClientSecretInBody_AsClientSecretPost()
    {
        Factory.OidcProvider.EnablePushedAuthorization = true;
        Factory.OidcProvider.IdTokenClaims["sub"] = "usuario-par";
        Factory.OidcProvider.UserInfo["sub"] = "usuario-par";

        var start = await StartLoginAsync();

        // Com PAR, o navegador só leva client_id e request_uri; o resto foi pelo backchannel.
        var authorize = QueryHelpers.ParseQuery(start.Location.Query);
        Assert.Equal(["client_id", "request_uri"], authorize.Keys.Order());

        var callback = await CallbackAsync(start, "code=code-de-teste");
        Assert.Equal("/", callback.Headers.Location!.ToString());
        Assert.Equal("usuario-par", (await GetMeAsync(FindCookie(callback, SessionCookieName))).Subject);

        foreach (var path in new[] { "/par", "/token" })
        {
            var request = Assert.Single(Factory.OidcProvider.Requests, r => r.Path == path);
            Assert.False(request.HasAuthorizationHeader, $"{path}: não deve usar client_secret_basic");
            Assert.Equal(FakeOidcProvider.ClientId, request.Form["client_id"]);
            Assert.Equal(Saldo360ApiFactory.TestClientSecret, request.Form["client_secret"]);
        }

        Assert.Equal("S256", Assert.Single(Factory.OidcProvider.Requests, r => r.Path == "/par").Form["code_challenge_method"]);
        Assert.True(Assert.Single(Factory.OidcProvider.Requests, r => r.Path == "/token").Form.ContainsKey("code_verifier"));
    }

    /// <summary>Inicia o login, simula o provedor devolvendo o code e chama o callback com os cookies de correlação.</summary>
    private async Task<HttpResponseMessage> CompleteLoginAsync() =>
        await CallbackAsync(await StartLoginAsync(), "code=code-de-teste");
}

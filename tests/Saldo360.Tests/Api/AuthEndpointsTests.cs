using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Saldo360.Api.Authentication;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Trait("Category", "CORE")]
public sealed class AuthEndpointsTests : ApiTestBase
{
    [Fact]
    public async Task Me_Returns401_WithoutSession()
    {
        var response = await Client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_Returns200_WithIssuerAndSubject_WhenSessionIsValid()
    {
        var session = await SignInAsync();

        var me = await GetMeAsync(session);

        Assert.Equal(
            new CurrentUserResponse(
                Id: "user-123",
                Issuer: Saldo360ApiFactory.TestAuthority,
                Subject: "user-123",
                DisplayName: "Maria Silva",
                Username: "maria",
                Email: "maria@example.com"),
            me);
    }

    [Fact]
    public async Task SessionCookie_IsHttpOnlySecureLaxAndOnlyCarriesSessionId()
    {
        var response = await Client.GetAsync(Saldo360ApiFactory.SignInPath);

        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SessionCookieName + "="));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        // Com o ticket no servidor, o cookie é pequeno (só a referência da sessão, protegida).
        Assert.True(setCookie.Split(';')[0].Length < 600, "O cookie não deveria carregar as claims do usuário.");
    }

    [Fact]
    public async Task Logout_WithCsrfToken_InvalidatesSession_EvenForReplayedCookie()
    {
        var session = await SignInAsync();
        var (csrfCookie, csrfToken) = await GetCsrfAsync(session);

        var logout = await SendAsync(HttpMethod.Post, "/auth/logout", $"{session}; {csrfCookie}", csrfToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // Reenvia o cookie antigo: a sessão foi removida no servidor, então não vale mais.
        var me = await SendAsync(HttpMethod.Get, "/api/me", session);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutCsrfToken_IsRejected_AndSessionStaysValid()
    {
        var session = await SignInAsync();

        var logout = await SendAsync(HttpMethod.Post, "/auth/logout", session);
        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);

        var me = await SendAsync(HttpMethod.Get, "/api/me", session);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Logout_WithCsrfTokenFromAnotherSession_IsRejected()
    {
        var attacker = await SignInAsync("attacker");
        var (attackerCsrfCookie, attackerToken) = await GetCsrfAsync(attacker);
        var victim = await SignInAsync("victim");

        var logout = await SendAsync(HttpMethod.Post, "/auth/logout", $"{victim}; {attackerCsrfCookie}", attackerToken);

        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
    }

    [Fact]
    public async Task Login_RedirectsToProvider_UsingCodeFlowWithPkce()
    {
        var response = await Client.GetAsync("/auth/login?returnUrl=%2F");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(Saldo360ApiFactory.TestAuthorizationEndpoint, location.GetLeftPart(UriPartial.Path));

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(FakeOidcProvider.ClientId, query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.False(query.ContainsKey("response_mode"), "Code flow deve usar o padrão (query), não form_post.");
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.Equal("https://localhost/signin-oidc", query["redirect_uri"]);
        Assert.Equal("openid profile email", query["scope"]);
        Assert.False(query.ContainsKey("client_secret"));
        Assert.DoesNotContain(Saldo360ApiFactory.TestClientSecret, location.ToString());
        Assert.DoesNotContain(Factory.Logs.Entries, entry => entry.Contains(Saldo360ApiFactory.TestClientSecret));
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task Login_WhenAlreadyAuthenticated_OnlyRedirectsToLocalUrls(string returnUrl)
    {
        var session = await SignInAsync();

        var response = await SendAsync(HttpMethod.Get, $"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}", session);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
    }

}

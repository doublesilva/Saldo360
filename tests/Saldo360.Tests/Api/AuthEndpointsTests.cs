using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Saldo360.Api.Authentication;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

public sealed class AuthEndpointsTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-saldo360.session";

    private readonly Saldo360ApiFactory _factory = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        // HTTPS como em produção (cookies Secure). Cookies são controlados manualmente
        // para poder reenviar um cookie antigo depois do logout.
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Me_Returns401_WithoutSession()
    {
        var response = await _client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_Returns200_WithUserIdentity_WhenSessionIsValid()
    {
        var session = await SignInAsync();

        var response = await SendAsync(HttpMethod.Get, "/api/me", session);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.Equal(new CurrentUserResponse("user-123", "Maria Silva", "maria", "maria@example.com"), me);
    }

    [Fact]
    public async Task SessionCookie_IsHttpOnlySecureLaxAndOnlyCarriesSessionId()
    {
        var response = await _client.GetAsync($"{Saldo360ApiFactory.SignInPath}");

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
        var response = await _client.GetAsync("/auth/login?returnUrl=%2F");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(Saldo360ApiFactory.TestAuthorizationEndpoint, location.GetLeftPart(UriPartial.Path));

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("saldo360-tests", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.False(query.ContainsKey("response_mode"), "Code flow deve usar o padrão (query), não form_post.");
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.Equal("https://localhost/signin-oidc", query["redirect_uri"]);
        Assert.Equal("openid profile email", query["scope"]);
        Assert.False(query.ContainsKey("client_secret"));
        Assert.DoesNotContain(Saldo360ApiFactory.TestClientSecret, location.ToString());
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(Saldo360ApiFactory.TestClientSecret));
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

    [Fact]
    public async Task Health_RemainsAnonymous()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Cria uma sessão e devolve o par "nome=valor" do cookie de sessão.</summary>
    private async Task<string> SignInAsync(string subject = "user-123")
    {
        var response = await _client.GetAsync($"{Saldo360ApiFactory.SignInPath}?sub={subject}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return FindCookie(response, SessionCookieName);
    }

    private async Task<(string Cookie, string Token)> GetCsrfAsync(string sessionCookie)
    {
        var response = await SendAsync(HttpMethod.Get, "/auth/csrf", sessionCookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CsrfTokenResponse>();
        return (FindCookie(response, "__Host-saldo360.csrf"), body!.Token);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string cookies, string? csrfToken = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookies);
        if (csrfToken is not null)
        {
            request.Headers.Add(AuthenticationSetup.CsrfHeaderName, csrfToken);
        }

        return _client.SendAsync(request);
    }

    private static string FindCookie(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie")
            .Select(header => header.Split(';')[0])
            .Single(pair => pair.StartsWith(name + "="));
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Saldo360.Api.Authentication;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Tests.Support;

/// <summary>
/// Base dos testes de API com sessão: HTTPS como em produção (cookies Secure) e cookies controlados
/// manualmente, para poder reenviar um cookie antigo ou combinar cookies de sessões diferentes.
/// </summary>
public abstract class ApiTestBase : IAsyncLifetime
{
    protected const string SessionCookieName = "__Host-saldo360.session";

    /// <param name="settings">Configurações extras da API.</param>
    /// <param name="baseAddress">Origem usada pelo cliente HTTP.</param>
    /// <param name="connectionString">PostgreSQL real; sem ela, a API usa EF Core InMemory.</param>
    protected ApiTestBase(
        IReadOnlyDictionary<string, string?>? settings = null,
        string baseAddress = "https://localhost",
        string? connectionString = null)
    {
        Factory = new Saldo360ApiFactory(connectionString, settings);
        BaseAddress = new Uri(baseAddress);
    }

    protected Saldo360ApiFactory Factory { get; }

    protected Uri BaseAddress { get; }

    protected HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await BeforeApiStartsAsync();
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
    }

    /// <summary>Executado antes de a API subir (ex.: criar e migrar o banco do teste).</summary>
    protected virtual Task BeforeApiStartsAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }

    /// <summary>Cria uma sessão pelo endpoint de teste e devolve o par "nome=valor" do cookie de sessão.</summary>
    protected async Task<string> SignInAsync(string subject = "user-123", string issuer = Saldo360ApiFactory.TestAuthority)
    {
        var response = await Client.GetAsync(
            $"{Saldo360ApiFactory.SignInPath}?sub={Uri.EscapeDataString(subject)}&iss={Uri.EscapeDataString(issuer)}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return FindCookie(response, SessionCookieName);
    }

    protected async Task<(string Cookie, string Token)> GetCsrfAsync(string sessionCookie)
    {
        var response = await SendAsync(HttpMethod.Get, "/auth/csrf", sessionCookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CsrfTokenResponse>();
        return (FindCookie(response, "__Host-saldo360.csrf"), body!.Token);
    }

    protected async Task<CurrentUserResponse> GetMeAsync(string sessionCookie)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/me", sessionCookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
    }

    protected Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string cookies, string? csrfToken = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookies);
        if (csrfToken is not null)
        {
            request.Headers.Add(AuthenticationSetup.CsrfHeaderName, csrfToken);
        }

        return Client.SendAsync(request);
    }

    /// <summary>
    /// Inicia o login (GET /auth/login) e captura state, nonce e cookies de correlação, lendo-os da URL
    /// de autorização ou, com PAR, do corpo enviado ao provedor simulado.
    /// </summary>
    protected async Task<LoginStart> StartLoginAsync(HttpRequestMessage? request = null)
    {
        var login = await Client.SendAsync(request ?? new HttpRequestMessage(HttpMethod.Get, "/auth/login?returnUrl=%2F"));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var location = login.Headers.Location!;
        var query = QueryHelpers.ParseQuery(location.Query);
        var parameters = query.ContainsKey("request_uri")
            ? Factory.OidcProvider.Requests.Last(r => r.Path == "/par").Form
            : query;
        Factory.OidcProvider.Nonce = parameters["nonce"];

        var cookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        return new LoginStart(location, parameters, cookies);
    }

    /// <summary>Simula o provedor redirecionando o navegador ao callback com <paramref name="query"/> e o state original.</summary>
    protected Task<HttpResponseMessage> CallbackAsync(LoginStart start, string query) =>
        SendAsync(
            HttpMethod.Get,
            $"/signin-oidc?{query}&state={Uri.EscapeDataString(start.Parameters["state"]!)}",
            start.Cookies);

    protected sealed record LoginStart(Uri Location, Dictionary<string, StringValues> Parameters, string Cookies);

    /// <summary>Acesso direto ao banco da API (InMemory) para inspecionar ou adulterar sessões.</summary>
    protected async Task WithDbAsync(Func<Saldo360DbContext, Task> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<Saldo360DbContext>());
    }

    protected static string FindCookie(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie")
            .Select(header => header.Split(';')[0])
            .Single(pair => pair.StartsWith(name + "="));

    protected static bool HasCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
        && values.Any(header => header.StartsWith(name + "=") && !header.StartsWith(name + "=;"));
}

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Tests.Support;

/// <summary>
/// Testes de API sobre PostgreSQL real: cada teste recebe um banco novo e migrado.
/// Inclui helpers para JSON com CSRF e criação de famílias.
/// </summary>
public abstract class PostgresApiTestBase : ApiTestBase
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;

    protected PostgresApiTestBase(PostgresFixture postgres)
        : this(postgres, postgres.NewDatabaseConnectionString())
    {
    }

    private PostgresApiTestBase(PostgresFixture postgres, string connectionString)
        : base(connectionString: connectionString)
    {
        _postgres = postgres;
        ConnectionString = connectionString;
    }

    protected string ConnectionString { get; }

    protected override Task BeforeApiStartsAsync() => _postgres.CreateDatabaseAsync(ConnectionString);

    protected Saldo360DbContext Db() => PostgresFixture.CreateDbContext(ConnectionString);

    protected virtual async Task<WorkspaceDto> CreateWorkspaceAsync(string session, string name = "Personal")
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, "/api/me/financial-workspace", session);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<WorkspaceDto>(response);
    }

    protected sealed record WorkspaceDto(Guid Id, string Name, Guid OwnerUserId, string Type);

    /// <summary>Envia JSON com o token CSRF da própria sessão.</summary>
    protected async Task<HttpResponseMessage> SendWithCsrfAsync(HttpMethod method, string url, string session, object? body = null)
    {
        var (csrfCookie, csrfToken) = await GetCsrfAsync(session);
        return await SendJsonAsync(method, url, $"{session}; {csrfCookie}", csrfToken, body);
    }

    protected Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string url, string cookies, string? csrf, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }

        request.Headers.Add("Cookie", cookies);
        if (csrf is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf);
        }

        return Client.SendAsync(request);
    }

    protected Task<HttpResponseMessage> PostFamilyAsync(string session, object body) =>
        SendWithCsrfAsync(HttpMethod.Post, "/api/families", session, body);

    /// <summary>Cria uma família para a sessão e devolve seus dados.</summary>
    protected async Task<FamilyDto> CreateFamilyAsync(string session, string name = "Família Silva")
    {
        var response = await PostFamilyAsync(session, new { name });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<FamilyDto>(response);
    }

    /// <summary>O Owner convida e <paramref name="guestSession"/> aceita; devolve a família vista pelo convidado.</summary>
    protected async Task<FamilyDto> JoinByInvitationAsync(string ownerSession, Guid familyId, string guestSession)
    {
        var invitation = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/invitations", ownerSession);
        Assert.Equal(System.Net.HttpStatusCode.Created, invitation.StatusCode);
        using var created = JsonDocument.Parse(await invitation.Content.ReadAsStringAsync());
        var token = created.RootElement.GetProperty("token").GetString();

        var accepted = await SendWithCsrfAsync(HttpMethod.Post, "/api/family-invitations/accept", guestSession, new { token });
        Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
        using var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("family").Deserialize<FamilyDto>(Json)!;
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    protected sealed record FamilyDto(Guid Id, string Name, DateTimeOffset CreatedAt, string MyRole, List<MemberDto> Members);

    protected sealed record MemberDto(Guid MemberId, string DisplayName, string Role, DateTimeOffset JoinedAt, bool IsCurrentUser);

    protected sealed record FamilySummaryDto(Guid Id, string Name, DateTimeOffset CreatedAt, string MyRole, int MemberCount);
}

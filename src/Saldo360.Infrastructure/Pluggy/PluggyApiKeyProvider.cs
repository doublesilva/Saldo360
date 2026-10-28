using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Saldo360.Application.Pluggy;

namespace Saldo360.Infrastructure.Pluggy;

/// <summary>
/// Autentica via <c>POST /auth</c> e mantém a API Key em memória até perto da expiração.
/// Chamadas concorrentes aguardam a mesma autenticação em vez de disparar várias.
/// </summary>
internal sealed class PluggyApiKeyProvider(IHttpClientFactory clients, PluggyOptions options, TimeProvider time)
{
    public const string ClientName = "Pluggy";
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? apiKey;
    private DateTimeOffset expiresAt;

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (Cached() is { } cached) return cached;
        await gate.WaitAsync(ct);
        try
        {
            return Cached() ?? await AuthenticateAsync(ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>Descarta a chave se ainda for a que falhou, sem derrubar uma já renovada por outra chamada.</summary>
    public async Task InvalidateAsync(string rejectedKey, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { if (apiKey == rejectedKey) apiKey = null; }
        finally { gate.Release(); }
    }

    private string? Cached() => apiKey is { } key && expiresAt > time.GetUtcNow() ? key : null;

    private async Task<string> AuthenticateAsync(CancellationToken ct)
    {
        if (!options.IsConfigured) throw new PluggyException(PluggyFailureKind.NotConfigured);
        try
        {
            using var response = await clients.CreateClient(ClientName).PostAsJsonAsync("auth",
                new { clientId = options.ClientId, clientSecret = options.ClientSecret }, ct);
            if (!response.IsSuccessStatusCode)
                throw new PluggyException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest
                    ? PluggyFailureKind.Authentication : PluggyClientErrors.FromStatus(response.StatusCode), (int)response.StatusCode);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var key = body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("apiKey", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(key)) throw new PluggyException(PluggyFailureKind.InvalidResponse);
            apiKey = key;
            expiresAt = time.GetUtcNow().AddMinutes(Math.Clamp(options.ApiKeyLifetimeMinutes, 1, 115));
            return key;
        }
        catch (PluggyException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PluggyException(PluggyFailureKind.Timeout); }
        catch (HttpRequestException) { throw new PluggyException(PluggyFailureKind.Unavailable); }
        catch (JsonException) { throw new PluggyException(PluggyFailureKind.InvalidResponse); }
    }
}

internal static class PluggyClientErrors
{
    public static PluggyFailureKind FromStatus(HttpStatusCode status) => (int)status switch
    {
        401 => PluggyFailureKind.Authentication,
        403 => PluggyFailureKind.Forbidden,
        404 => PluggyFailureKind.NotFound,
        429 => PluggyFailureKind.RateLimited,
        408 or 504 => PluggyFailureKind.Timeout,
        >= 500 => PluggyFailureKind.Unavailable,
        _ => PluggyFailureKind.Rejected,
    };
}

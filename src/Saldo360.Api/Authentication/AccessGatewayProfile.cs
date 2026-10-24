using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Saldo360.Application.Users;
using Saldo360.Infrastructure.Authentication;

namespace Saldo360.Api.Authentication;

public sealed class ProfileSessionAccess
{
    internal ProfileSessionAccess(UserCpfStatus status, string? token = null) { Status = status; Token = token; }
    public UserCpfStatus Status { get; }
    internal string? Token { get; }
    public override string ToString() => Status.ToString();
}

public static class ProfileAccessTokenValidation
{
    public const string Audience = "accessgateway-profile";
    public const string SessionStatus = "saldo360.profile.status";
    public static async Task<ProfileSessionAccess> ValidateAsync(string? accessToken, string? tokenType,
        ExternalIdentity expected, OpenIdConnectOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase)) return new(UserCpfStatus.SessionInvalid);
        if (accessToken.Length > 16384) return new(UserCpfStatus.AccessDenied);
        try
        {
            var handler = new JsonWebTokenHandler { MapInboundClaims = false };
            if (!handler.CanReadToken(accessToken)) return new(UserCpfStatus.AccessDenied);
            var jwt = handler.ReadJsonWebToken(accessToken);
            // A preflight only: signature/lifetime/issuer are validated below before any use or forwarding.
            if (jwt.Typ != "at+jwt" || !jwt.Audiences.Contains(Audience, StringComparer.Ordinal)) return new(UserCpfStatus.AccessDenied);
            var configuration = options.Configuration ?? (options.ConfigurationManager is not null
                ? await options.ConfigurationManager.GetConfigurationAsync(ct) : null);
            if (configuration is null) return new(UserCpfStatus.Unavailable);
            var result = await handler.ValidateTokenAsync(accessToken, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = expected.Issuer, ValidateAudience = true, ValidAudience = Audience,
                ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
                ValidateIssuerSigningKey = true, IssuerSigningKeys = configuration.SigningKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"],
                ClockSkew = TimeSpan.FromSeconds(30), IncludeTokenOnFailedValidation = false, LogValidationExceptions = false
            });
            if (!result.IsValid || result.ClaimsIdentity is null) return new(UserCpfStatus.SessionInvalid);
            var principal = new ClaimsPrincipal(result.ClaimsIdentity);
            if (!ExternalIdentity.TryFromPrincipal(principal, out var actual) || actual != expected) return new(UserCpfStatus.SessionInvalid);
            var clientClaims = principal.FindAll("client_id").ToArray(); var authorizedParties = principal.FindAll("azp").ToArray();
            if (clientClaims.Length > 1 || authorizedParties.Length > 1
                || (clientClaims.FirstOrDefault()?.Value ?? authorizedParties.FirstOrDefault()?.Value) != options.ClientId
                || principal.Claims.Any(c => c.Type.Contains("cpf", StringComparison.OrdinalIgnoreCase))) return new(UserCpfStatus.AccessDenied);
            return new(UserCpfStatus.Available, accessToken);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(UserCpfStatus.Unavailable); }
    }
}

/// <summary>Fixed self-profile route; never receives another user's ID or an ID token.</summary>
public sealed class AccessGatewayProfileClient(HttpClient client)
{
    public async Task<UserCpfResult> GetAsync(ProfileSessionAccess access, ExternalIdentity expected, CancellationToken ct)
    {
        if (access.Status != UserCpfStatus.Available || access.Token is null) return new(access.Status);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var requestToken = timeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://gateway.home.doublesilva.com/api/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return new(UserCpfStatus.SessionInvalid);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) return new(UserCpfStatus.AccessDenied);
            if (!response.IsSuccessStatusCode) return new(UserCpfStatus.Unavailable);
            await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
            var bytes = new byte[8193]; var count = 0;
            while (count < bytes.Length) { var n = await stream.ReadAsync(bytes.AsMemory(count), requestToken); if (n == 0) break; count += n; }
            if (count > 8192) return new(UserCpfStatus.Unavailable);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count)); var root = json.RootElement;
            // Reject duplicate keys rather than interpreting ambiguous identity/sensitive attributes.
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)) return new(UserCpfStatus.Unavailable);
            if (!root.TryGetProperty("issuer", out var issuer) || issuer.ValueKind != JsonValueKind.String || issuer.GetString() != expected.Issuer
                || !root.TryGetProperty("subject", out var subject) || subject.ValueKind != JsonValueKind.String || subject.GetString() != expected.Subject) return new(UserCpfStatus.SessionInvalid);
            // Gateway's client attribute grant, not an invented OAuth scope: omitted means no cpf grant.
            if (!root.TryGetProperty("cpfRegistered", out var registered)) return new(UserCpfStatus.AccessDenied);
            if (registered.ValueKind == JsonValueKind.False)
                return root.TryGetProperty("cpf", out _) ? new(UserCpfStatus.Unavailable) : new(UserCpfStatus.NotRegistered);
            if (registered.ValueKind != JsonValueKind.True || !root.TryGetProperty("cpf", out var cpf) || cpf.ValueKind != JsonValueKind.String || !ValidCpf(cpf.GetString())) return new(UserCpfStatus.Unavailable);
            return new(UserCpfStatus.Available, cpf.GetString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(UserCpfStatus.Unavailable); }
    }

    private static bool ValidCpf(string? cpf)
    {
        if (cpf is null || cpf.Length != 11 || cpf.Any(c => !char.IsAsciiDigit(c)) || cpf.Distinct().Count() == 1) return false;
        for (var length = 9; length <= 10; length++)
        {
            var sum = 0; for (var i = 0; i < length; i++) sum += (cpf[i] - '0') * (length + 1 - i);
            var digit = sum * 10 % 11; if (digit == 10) digit = 0;
            if (cpf[length] - '0' != digit) return false;
        }
        return true;
    }
}

public sealed class CurrentUserCpf(IHttpContextAccessor contexts, IOptionsMonitor<OpenIdConnectOptions> options,
    AccessGatewayProfileClient client) : ICurrentUserCpf
{
    private Task<UserCpfResult>? current;
    public Task<UserCpfResult> GetAsync(CancellationToken ct) => current ??= ReadAsync(ct);
    private async Task<UserCpfResult> ReadAsync(CancellationToken ct)
    {
        var http = contexts.HttpContext;
        if (http is null || !ExternalIdentity.TryFromPrincipal(http.User, out var identity)) return new(UserCpfStatus.SessionInvalid);
        var session = await http.AuthenticateAsync(AuthenticationSetup.CookieScheme);
        if (!session.Succeeded || session.Principal is null || !ExternalIdentity.TryFromPrincipal(session.Principal, out var sessionIdentity) || sessionIdentity != identity) return new(UserCpfStatus.SessionInvalid);
        var token = session.Properties?.GetTokenValue("access_token");
        if (token is null)
        {
            var saved = session.Properties is { } properties && properties.Items.TryGetValue(ProfileAccessTokenValidation.SessionStatus, out var value) ? value : null;
            return new(Enum.TryParse<UserCpfStatus>(saved, out var status) && status != UserCpfStatus.Available ? status : UserCpfStatus.SessionInvalid);
        }
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            var validated = await ProfileAccessTokenValidation.ValidateAsync(token, "Bearer", identity, options.Get(AuthenticationSetup.OidcScheme), budget.Token);
            return await client.GetAsync(validated, identity, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(UserCpfStatus.Unavailable); }
    }
}

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
    internal ProfileSessionAccess(UserCpfStatus status, string? token = null, UserCpfDiagnosticCode? diagnosticCode = null)
    { Status = status; Token = token; DiagnosticCode = diagnosticCode; }
    public UserCpfStatus Status { get; }
    public UserCpfDiagnosticCode? DiagnosticCode { get; }
    internal string? Token { get; }
    public override string ToString() => Status.ToString();
}

public static class ProfileAccessTokenValidation
{
    public const string Audience = "accessgateway-profile";
    public const string SessionStatus = "saldo360.profile.status";
    public const string SessionDiagnostic = "saldo360.profile.diagnostic";
    public static async Task<ProfileSessionAccess> ValidateAsync(string? accessToken, string? tokenType,
        ExternalIdentity expected, OpenIdConnectOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return new(UserCpfStatus.SessionInvalid, diagnosticCode: UserCpfDiagnosticCode.AccessTokenMissing);
        if (!string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase)) return new(UserCpfStatus.SessionInvalid, diagnosticCode: UserCpfDiagnosticCode.BearerTokenTypeInvalid);
        if (accessToken.Length > 16384) return new(UserCpfStatus.AccessDenied, diagnosticCode: UserCpfDiagnosticCode.AccessTokenTooLarge);
        try
        {
            var handler = new JsonWebTokenHandler { MapInboundClaims = false };
            if (!handler.CanReadToken(accessToken)) return new(UserCpfStatus.AccessDenied, diagnosticCode: UserCpfDiagnosticCode.AccessTokenMalformed);
            var jwt = handler.ReadJsonWebToken(accessToken);
            // A preflight only: signature/lifetime/issuer are validated below before any use or forwarding.
            if (jwt.Typ != "at+jwt") return new(UserCpfStatus.AccessDenied, diagnosticCode: UserCpfDiagnosticCode.JwtTypeInvalid);
            if (!jwt.Audiences.Contains(Audience, StringComparer.Ordinal)) return new(UserCpfStatus.AccessDenied, diagnosticCode: UserCpfDiagnosticCode.AudienceMismatch);
            if (jwt.Alg != SecurityAlgorithms.RsaSha256) return new(UserCpfStatus.SessionInvalid, diagnosticCode: UserCpfDiagnosticCode.AlgorithmInvalid);
            var configuration = options.Configuration ?? (options.ConfigurationManager is not null
                ? await options.ConfigurationManager.GetConfigurationAsync(ct) : null);
            if (configuration is null) return new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.MetadataUnavailable);
            var result = await handler.ValidateTokenAsync(accessToken, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = expected.Issuer, ValidateAudience = true, ValidAudience = Audience,
                ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
                ValidateIssuerSigningKey = true, IssuerSigningKeys = configuration.SigningKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"],
                ClockSkew = TimeSpan.FromSeconds(30), IncludeTokenOnFailedValidation = false, LogValidationExceptions = false
            });
            if (!result.IsValid || result.ClaimsIdentity is null) return new(UserCpfStatus.SessionInvalid, diagnosticCode: ValidationFailure(result.Exception));
            var principal = new ClaimsPrincipal(result.ClaimsIdentity);
            if (!ExternalIdentity.TryFromPrincipal(principal, out var actual)) return new(UserCpfStatus.SessionInvalid, diagnosticCode: UserCpfDiagnosticCode.TokenIdentityInvalid);
            if (actual != expected) return new(UserCpfStatus.SessionInvalid, diagnosticCode: actual.Issuer != expected.Issuer ? UserCpfDiagnosticCode.IssuerMismatch : UserCpfDiagnosticCode.SubjectMismatch);
            var clientClaims = principal.FindAll("client_id").ToArray(); var authorizedParties = principal.FindAll("azp").ToArray();
            if (clientClaims.Length > 1 || authorizedParties.Length > 1
                || clientClaims.Length + authorizedParties.Length == 0
                || clientClaims.Concat(authorizedParties).Any(c => c.Value != options.ClientId))
                return new(UserCpfStatus.AccessDenied, diagnosticCode: UserCpfDiagnosticCode.ClientMismatch);
            if (principal.Claims.Any(c => c.Type.Contains("cpf", StringComparison.OrdinalIgnoreCase)))
                return new(UserCpfStatus.AccessDenied, diagnosticCode: UserCpfDiagnosticCode.SensitiveClaimsPresent);
            return new(UserCpfStatus.Available, accessToken, UserCpfDiagnosticCode.AccessTokenValidated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.TokenValidationUnavailable); }
    }

    private static UserCpfDiagnosticCode ValidationFailure(Exception? error) => error switch
    {
        SecurityTokenExpiredException => UserCpfDiagnosticCode.TokenExpired,
        SecurityTokenNotYetValidException => UserCpfDiagnosticCode.TokenNotYetValid,
        SecurityTokenNoExpirationException or SecurityTokenInvalidLifetimeException => UserCpfDiagnosticCode.TokenLifetimeInvalid,
        SecurityTokenInvalidIssuerException => UserCpfDiagnosticCode.IssuerMismatch,
        SecurityTokenInvalidAudienceException => UserCpfDiagnosticCode.AudienceMismatch,
        SecurityTokenSignatureKeyNotFoundException => UserCpfDiagnosticCode.SigningKeyUnavailable,
        SecurityTokenInvalidSignatureException => UserCpfDiagnosticCode.SignatureInvalid,
        SecurityTokenInvalidAlgorithmException => UserCpfDiagnosticCode.AlgorithmInvalid,
        _ => UserCpfDiagnosticCode.AccessTokenInvalid
    };
}

/// <summary>Fixed self-profile route; never receives another user's ID or an ID token.</summary>
public sealed class AccessGatewayProfileClient(HttpClient client)
{
    public async Task<UserCpfResult> GetAsync(ProfileSessionAccess access, ExternalIdentity expected, CancellationToken ct)
    {
        if (access.Status != UserCpfStatus.Available || access.Token is null) return new(access.Status, diagnosticCode: access.DiagnosticCode);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var requestToken = timeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://gateway.home.doublesilva.com/api/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return new(UserCpfStatus.SessionInvalid, diagnosticCode: UserCpfDiagnosticCode.GatewayUnauthorized);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) return new(UserCpfStatus.AccessDenied, diagnosticCode: UserCpfDiagnosticCode.GatewayForbidden);
            if (!response.IsSuccessStatusCode) return new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.GatewayUnavailable);
            await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
            var bytes = new byte[8193]; var count = 0;
            while (count < bytes.Length) { var n = await stream.ReadAsync(bytes.AsMemory(count), requestToken); if (n == 0) break; count += n; }
            if (count > 8192) return new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.GatewayInvalidResponse);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count)); var root = json.RootElement;
            // Reject duplicate keys rather than interpreting ambiguous identity/sensitive attributes.
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)) return new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.GatewayInvalidResponse);
            if (!root.TryGetProperty("issuer", out var issuer) || issuer.ValueKind != JsonValueKind.String || issuer.GetString() != expected.Issuer
                || !root.TryGetProperty("subject", out var subject) || subject.ValueKind != JsonValueKind.String || subject.GetString() != expected.Subject) return new(UserCpfStatus.SessionInvalid, diagnosticCode: UserCpfDiagnosticCode.GatewayIdentityMismatch);
            // Gateway's client attribute grant, not an invented OAuth scope: omitted means no cpf grant.
            if (!root.TryGetProperty("cpfRegistered", out var registered)) return new(UserCpfStatus.AccessDenied, diagnosticCode: UserCpfDiagnosticCode.GatewayCpfGrantMissing);
            if (registered.ValueKind == JsonValueKind.False)
                return root.TryGetProperty("cpf", out _) ? new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.GatewayInvalidResponse) : new(UserCpfStatus.NotRegistered, diagnosticCode: UserCpfDiagnosticCode.GatewayCpfNotRegistered);
            if (registered.ValueKind != JsonValueKind.True || !root.TryGetProperty("cpf", out var cpf) || cpf.ValueKind != JsonValueKind.String || !ValidCpf(cpf.GetString())) return new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.GatewayCpfInvalid);
            return new(UserCpfStatus.Available, cpf.GetString(), UserCpfDiagnosticCode.ProfileAvailable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.GatewayTimeout); }
        catch (JsonException) { return new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.GatewayInvalidResponse); }
        catch (Exception) { return new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.GatewayUnavailable); }
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
    AccessGatewayProfileClient client, ILogger<CurrentUserCpf>? logger = null) : ICurrentUserCpf
{
    private Task<UserCpfResult>? current;
    public Task<UserCpfResult> GetAsync(CancellationToken ct) => current ??= ReadAndReportAsync(ct);
    private async Task<UserCpfResult> ReadAndReportAsync(CancellationToken ct)
    {
        var result = await ReadAsync(ct);
        if (result.DiagnosticCode is { } code)
            logger?.LogInformation(new EventId(17081, "CurrentUserCpfOutcome"),
                "Profile lookup stage {Stage}; code {Code}; status {Status}",
                code.ToString().StartsWith("Gateway", StringComparison.Ordinal) || code == UserCpfDiagnosticCode.ProfileAvailable ? "Gateway"
                    : code.ToString().StartsWith("Session", StringComparison.Ordinal) || code == UserCpfDiagnosticCode.StoredAccessTokenMissing ? "Session" : "AccessToken",
                code.ToString(), result.Status.ToString());
        return result;
    }
    private async Task<UserCpfResult> ReadAsync(CancellationToken ct)
    {
        var http = contexts.HttpContext;
        if (http?.User.Identity?.IsAuthenticated != true) return new(UserCpfStatus.SessionInvalid, diagnosticCode: UserCpfDiagnosticCode.SessionMissing);
        if (!ExternalIdentity.TryFromPrincipal(http.User, out var identity)) return new(UserCpfStatus.SessionInvalid, diagnosticCode: UserCpfDiagnosticCode.SessionIdentityInvalid);
        var session = await http.AuthenticateAsync(AuthenticationSetup.CookieScheme);
        if (!session.Succeeded || session.Principal is null) return new(UserCpfStatus.SessionInvalid, diagnosticCode: UserCpfDiagnosticCode.SessionAuthenticationFailed);
        if (!ExternalIdentity.TryFromPrincipal(session.Principal, out var sessionIdentity) || sessionIdentity != identity) return new(UserCpfStatus.SessionInvalid, diagnosticCode: UserCpfDiagnosticCode.SessionIdentityMismatch);
        var token = session.Properties?.GetTokenValue("access_token");
        if (token is null)
        {
            var saved = session.Properties is { } properties && properties.Items.TryGetValue(ProfileAccessTokenValidation.SessionStatus, out var value) ? value : null;
            var savedCode = session.Properties is { } props && props.Items.TryGetValue(ProfileAccessTokenValidation.SessionDiagnostic, out var diagnostic) ? diagnostic : null;
            return new(Enum.TryParse<UserCpfStatus>(saved, out var status) && Enum.IsDefined(status) && status != UserCpfStatus.Available ? status : UserCpfStatus.SessionInvalid,
                diagnosticCode: Enum.TryParse<UserCpfDiagnosticCode>(savedCode, out var code) && Enum.IsDefined(code)
                    && code is not (UserCpfDiagnosticCode.AccessTokenValidated or UserCpfDiagnosticCode.ProfileAvailable) ? code : UserCpfDiagnosticCode.StoredAccessTokenMissing);
        }
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            var validated = await ProfileAccessTokenValidation.ValidateAsync(token, "Bearer", identity, options.Get(AuthenticationSetup.OidcScheme), budget.Token);
            return await client.GetAsync(validated, identity, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(UserCpfStatus.Unavailable, diagnosticCode: UserCpfDiagnosticCode.TokenValidationUnavailable); }
    }
}

using System.Text.Json.Serialization;

namespace Saldo360.Application.Users;

public enum UserCpfStatus { Available, NotRegistered, SessionInvalid, AccessDenied, Unavailable }

// Fixed technical outcomes only; never attach claims, tokens, CPF or provider response text.
public enum UserCpfDiagnosticCode
{
    ProfileAvailable, AccessTokenValidated, SessionMissing, SessionIdentityInvalid, SessionAuthenticationFailed, SessionIdentityMismatch,
    StoredAccessTokenMissing, AccessTokenMissing, BearerTokenTypeInvalid, AccessTokenTooLarge, AccessTokenMalformed,
    JwtTypeInvalid, AudienceMismatch, MetadataUnavailable, TokenExpired, TokenNotYetValid, TokenLifetimeInvalid,
    IssuerMismatch, SigningKeyUnavailable, SignatureInvalid, AlgorithmInvalid, AccessTokenInvalid,
    TokenIdentityInvalid, SubjectMismatch, ClientMismatch, SensitiveClaimsPresent, TokenValidationUnavailable,
    GatewayUnauthorized, GatewayForbidden, GatewayUnavailable, GatewayTimeout, GatewayInvalidResponse,
    GatewayIdentityMismatch, GatewayCpfGrantMissing, GatewayCpfNotRegistered, GatewayCpfInvalid
}

/// <summary>Request-scoped sensitive value. Never put this value in logs, tickets or persistence.</summary>
public sealed class UserCpfResult(UserCpfStatus status, string? fullValue = null, UserCpfDiagnosticCode? diagnosticCode = null)
{
    public UserCpfStatus Status { get; } = status;
    public UserCpfDiagnosticCode? DiagnosticCode { get; } = diagnosticCode;
    [JsonIgnore] public string? FullValue { get; } = status == UserCpfStatus.Available ? fullValue : null;
    public override string ToString() => Status.ToString();
}

/// <summary>Only the authenticated caller; no other user identifier can be supplied.</summary>
public interface ICurrentUserCpf
{
    Task<UserCpfResult> GetAsync(CancellationToken ct);
}

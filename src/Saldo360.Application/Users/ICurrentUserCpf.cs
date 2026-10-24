using System.Text.Json.Serialization;

namespace Saldo360.Application.Users;

public enum UserCpfStatus { Available, NotRegistered, SessionInvalid, AccessDenied, Unavailable }

/// <summary>Request-scoped sensitive value. Never put this value in logs, tickets or persistence.</summary>
public sealed class UserCpfResult(UserCpfStatus status, string? fullValue = null)
{
    public UserCpfStatus Status { get; } = status;
    [JsonIgnore] public string? FullValue { get; } = status == UserCpfStatus.Available ? fullValue : null;
    public override string ToString() => Status.ToString();
}

/// <summary>Only the authenticated caller; no other user identifier can be supplied.</summary>
public interface ICurrentUserCpf
{
    Task<UserCpfResult> GetAsync(CancellationToken ct);
}

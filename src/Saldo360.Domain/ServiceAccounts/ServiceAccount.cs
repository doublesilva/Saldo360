using System.Text;
using System.Text.RegularExpressions;
using Saldo360.Domain.Common;

namespace Saldo360.Domain.ServiceAccounts;

public enum ServiceType { Electricity, Water, Telecom }
public enum ServiceIdentifierType { UtilityCustomerNumber, CustomerCode, InstallationNumber, ContractNumber, Keyword }

public sealed class ServiceAccount : Entity
{
    private ServiceAccount() { Issuer = DisplayName = string.Empty; }
    public Guid UserId { get; private init; }
    public ServiceType ServiceType { get; private init; }
    public string Issuer { get; private init; }
    public string DisplayName { get; private init; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public static ServiceAccount Create(Guid userId, ServiceType serviceType, string issuer, string displayName, DateTimeOffset now)
    {
        if (userId == Guid.Empty || !Enum.IsDefined(serviceType)) throw new ArgumentException("Proprietário ou tipo inválido.");
        issuer = NormalizeIssuer(issuer);
        displayName = displayName.Trim();
        if (issuer.Length is < 2 or > 120 || displayName.Length is < 1 or > 160 || displayName.Any(char.IsControl))
            throw new ArgumentException("Informe emissor e nome válidos.");
        return new() { UserId = userId, ServiceType = serviceType, Issuer = issuer, DisplayName = displayName, CreatedAt = now };
    }
    public void Deactivate(DateTimeOffset now) { IsActive = false; UpdatedAt = now; }
    public static string NormalizeIssuer(string value)
    {
        var normalized = Regex.Replace(value.Trim().Normalize(NormalizationForm.FormKC), @"\s+", " ");
        if (normalized.Any(char.IsControl)) throw new ArgumentException("Emissor inválido.");
        return normalized.ToUpperInvariant() switch { "CEEE" or "CEEE-D" or "EQUATORIAL" => "Equatorial", "DMAE" => "DMAE", _ => normalized.ToUpperInvariant() };
    }
}

public sealed class ServiceAccountIdentifier : Entity
{
    private ServiceAccountIdentifier() { LookupHash = MaskedValue = string.Empty; ProtectedNormalizedValue = []; }
    public Guid ServiceAccountId { get; private init; }
    public Guid UserId { get; private init; }
    public ServiceType ServiceType { get; private init; }
    public ServiceIdentifierType Type { get; private init; }
    public byte[] ProtectedNormalizedValue { get; private init; }
    public string LookupHash { get; private init; }
    public string MaskedValue { get; private init; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private init; }
    public static ServiceAccountIdentifier Create(ServiceAccount account, ServiceIdentifierType type, byte[] protectedValue, string hash, string mask, DateTimeOffset now)
    {
        if (!account.IsActive || !Enum.IsDefined(type) || protectedValue.Length == 0 || !Regex.IsMatch(hash, "^[0-9a-f]{64}$") || string.IsNullOrWhiteSpace(mask))
            throw new ArgumentException("Identificador inválido.");
        return new() { ServiceAccountId = account.Id, UserId = account.UserId, ServiceType = account.ServiceType, Type = type,
            ProtectedNormalizedValue = protectedValue, LookupHash = hash, MaskedValue = mask, CreatedAt = now };
    }
    public void Deactivate() => IsActive = false;
}

public static class ServiceIdentifierNormalization
{
    public static string Normalize(ServiceIdentifierType type, string value)
    {
        if (!Enum.IsDefined(type) || string.IsNullOrWhiteSpace(value) || value.Length > 160) throw new ArgumentException("Identificador inválido.");
        value = value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        if (type == ServiceIdentifierType.Keyword)
        {
            if (value.Any(char.IsControl)) throw new ArgumentException("Palavra-chave inválida.");
            return Regex.Replace(value, @"\s+", " ");
        }
        if (!Regex.IsMatch(value, type == ServiceIdentifierType.UtilityCustomerNumber ? @"^[0-9.\s/\-]+$" : @"^[A-Z0-9.\s/\-]+$"))
            throw new ArgumentException("Caracteres inválidos no identificador.");
        var normalized = Regex.Replace(value, @"[.\s/\-]", "");
        if (normalized.Length is < 3 or > 80) throw new ArgumentException("Tamanho de identificador inválido.");
        return normalized;
    }
    public static string Mask(string normalized) => normalized.Length > 4 ? "***" + normalized[^4..] : "***";
}

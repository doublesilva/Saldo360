using System.Text.Json.Serialization;
using Saldo360.Domain.ServiceAccounts;
using Saldo360.Application.CreditCards;

namespace Saldo360.Application.ServiceAccounts;

public sealed record ExtractedServiceIdentifier(ServiceIdentifierType Type, [property: JsonIgnore] string NormalizedValue, string MaskedValue, string Confidence)
{
    public string Context { get; init; } = "ExplicitLabel";
}
public sealed record ServiceAccountDto(Guid Id, ServiceType ServiceType, string Issuer, string DisplayName, bool IsActive, IReadOnlyList<ServiceAccountIdentifierDto> Identifiers);
public sealed record ServiceAccountIdentifierDto(Guid Id, ServiceIdentifierType Type, string MaskedValue, bool IsActive);
public sealed record ServiceAccountResolution(string State, string? Issuer = null, Guid? ServiceAccountId = null);
public interface IServiceAccountResolver
{
    Task<ServiceAccountResolution> ResolveAsync(Guid userId, ServiceType serviceType, IReadOnlyList<ExtractedServiceIdentifier> identifiers, CancellationToken ct);
    Task<ServiceBillExtractionOutput> EnrichAsync(Guid userId, ServiceBillExtractionOutput extraction, CancellationToken ct);
}
public interface IServiceAccountIdentifierProtection
{
    bool IsConfigured { get; }
    string Hash(Guid userId, ServiceType serviceType, ServiceIdentifierType type, string normalized);
    byte[] Protect(Guid userId, string normalized);
}
public interface IServiceAccountRepository
{
    Task<IReadOnlyList<ServiceAccountDto>> ListAsync(Guid userId, CancellationToken ct);
    Task<ServiceAccount?> GetAsync(Guid userId, Guid id, CancellationToken ct);
    Task AddAsync(ServiceAccount account, CancellationToken ct);
    Task<bool> TryAddIdentifierAsync(ServiceAccountIdentifier identifier, CancellationToken ct);
    Task<bool> HasActiveAsync(Guid userId, ServiceType type, CancellationToken ct);
    Task<IReadOnlyList<ServiceAccount>> FindAsync(Guid userId, ServiceType type, IReadOnlyList<(ServiceIdentifierType Type, string Hash)> hashes, CancellationToken ct);
}

public sealed class ServiceAccountService(IServiceAccountRepository repository, IServiceAccountIdentifierProtection protection, TimeProvider clock)
{
    public Task<IReadOnlyList<ServiceAccountDto>> ListAsync(Guid userId, CancellationToken ct) => repository.ListAsync(userId, ct);
    public async Task<ServiceAccount> CreateAsync(Guid userId, ServiceType type, string issuer, string name, CancellationToken ct)
    {
        var account = ServiceAccount.Create(userId, type, issuer, name, clock.GetUtcNow());
        await repository.AddAsync(account, ct);
        return account;
    }
    public async Task<string> AddIdentifierAsync(Guid userId, Guid accountId, ServiceIdentifierType type, string value, CancellationToken ct)
    {
        var account = await repository.GetAsync(userId, accountId, ct);
        if (account is null) return "NotFound";
        if (!account.IsActive) return "Inactive";
        var normalized = ServiceIdentifierNormalization.Normalize(type, value);
        if (!protection.IsConfigured) return "ProtectionUnavailable";
        var identifier = ServiceAccountIdentifier.Create(account, type, protection.Protect(userId, normalized),
            protection.Hash(userId, account.ServiceType, type, normalized), ServiceIdentifierNormalization.Mask(normalized), clock.GetUtcNow());
        return await repository.TryAddIdentifierAsync(identifier, ct) ? "Created" : "Conflict";
    }
}

public sealed class ServiceAccountResolver(IServiceAccountRepository repository, IServiceAccountIdentifierProtection protection) : IServiceAccountResolver
{
    public async Task<ServiceAccountResolution> ResolveAsync(Guid userId, ServiceType serviceType, IReadOnlyList<ExtractedServiceIdentifier> identifiers, CancellationToken ct)
    {
        var strong = identifiers.Where(i => i.Type != ServiceIdentifierType.Keyword && i.Confidence == "High").ToList();
        if (strong.Count == 0 || !await repository.HasActiveAsync(userId, serviceType, ct)) return new("NotFound");
        if (!protection.IsConfigured) return new("NeedsReview");
        var hashes = strong.Select(i => (i.Type, protection.Hash(userId, serviceType, i.Type, ServiceIdentifierNormalization.Normalize(i.Type, i.NormalizedValue)))).Distinct().ToList();
        var accounts = await repository.FindAsync(userId, serviceType, hashes, ct);
        return accounts.Count switch { 0 => new("NotFound"), 1 => new("Matched", accounts[0].Issuer, accounts[0].Id), _ => new("Ambiguous") };
    }
    public async Task<ServiceBillExtractionOutput> EnrichAsync(Guid userId, ServiceBillExtractionOutput extraction, CancellationToken ct)
    {
        var bill = extraction.Result;
        var type = bill.DocumentType switch { "ElectricityBill" => ServiceType.Electricity, "WaterBill" => ServiceType.Water, "TelecomBill" => ServiceType.Telecom, _ => (ServiceType?)null };
        if (type is null || bill.Identifiers.Count == 0) return extraction;
        var resolution = await ResolveAsync(userId, type.Value, bill.Identifiers, ct);
        var failure = resolution.State switch { "Ambiguous" => "AmbiguousServiceAccount", "NeedsReview" => "ServiceAccountResolutionUnavailable", _ => bill.FailureCode };
        if (resolution.State == "Matched")
        {
            if (bill.Issuer is not null && !string.Equals(ServiceAccount.NormalizeIssuer(bill.Issuer), resolution.Issuer, StringComparison.Ordinal))
                failure = "IssuerConflict";
            else
            {
                bill = bill with { Issuer = bill.Issuer ?? resolution.Issuer, FailureCode = bill.FailureCode == "IssuerNotFound" ? null : bill.FailureCode,
                    Confidence = (bill.FailureCode is null or "IssuerNotFound") && bill.Competence is not null && bill.DueDate is not null && bill.TotalAmount is not null ? "High" : bill.Confidence,
                    IssuerSource = bill.Issuer is null ? "RegisteredServiceAccountIdentifier" : bill.IssuerSource,
                    ResolvedServiceAccountId = resolution.ServiceAccountId, IssuerResolutionState = resolution.State };
                return new(bill, CompositeServiceBillExtractor.Assess(bill).ToString());
            }
        }
        bill = bill with { FailureCode = failure, IssuerResolutionState = resolution.State };
        return new(bill, failure is "AmbiguousServiceAccount" or "IssuerConflict" or "ServiceAccountResolutionUnavailable" ? "NeedsReview" : extraction.Assessment);
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Saldo360.Application.CreditCards;
using Saldo360.Application.ServiceAccounts;
using Saldo360.Domain.ServiceAccounts;
using Saldo360.Infrastructure.ServiceAccounts;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Services")]
[Trait("Profile", "FAST")]
public sealed class ServiceAccountResolverTests
{
    internal static readonly IReadOnlyDictionary<string, string?> Settings = new Dictionary<string, string?>
    { ["ServiceAccounts:IdentifierHmacKey"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()) };
    private const string FakeUc = "987.654.321-00";
    private readonly Guid owner = Guid.NewGuid();
    private readonly Repository repository = new();
    private readonly ServiceAccountIdentifierProtection protection = Protection();
    internal static ServiceAccountIdentifierProtection Protection(IDataProtectionProvider? provider = null) => new(provider ?? new EphemeralDataProtectionProvider(), new ConfigurationBuilder().AddInMemoryCollection(Settings).Build());

    private async Task<ServiceAccount> Register(Guid user, ServiceType type = ServiceType.Electricity, ServiceIdentifierType identifierType = ServiceIdentifierType.UtilityCustomerNumber, string value = FakeUc)
    {
        var service = new ServiceAccountService(repository, protection, TimeProvider.System);
        var account = await service.CreateAsync(user, type, "Equatorial", "Synthetic account", default);
        Assert.Equal("Created", await service.AddIdentifierAsync(user, account.Id, identifierType, value, default));
        return account;
    }
    private static ExtractedServiceIdentifier Identifier(string value = FakeUc, ServiceIdentifierType type = ServiceIdentifierType.UtilityCustomerNumber) =>
        new(type, ServiceIdentifierNormalization.Normalize(type, value), "***2100", "High");
    private ServiceAccountResolver Resolver() => new(repository, protection);

    [Fact]
    public async Task ExactNormalizedUcResolvesOwnerButNeverOtherUser()
    {
        var account = await Register(owner);
        var result = await Resolver().ResolveAsync(owner, ServiceType.Electricity, [Identifier("98765432100")], default);
        Assert.Equal("Matched", result.State); Assert.Equal("Equatorial", result.Issuer); Assert.Equal(account.Id, result.ServiceAccountId);
        Assert.Equal("NotFound", (await Resolver().ResolveAsync(Guid.NewGuid(), ServiceType.Electricity, [Identifier()], default)).State);
        Assert.Equal("NotFound", (await Resolver().ResolveAsync(owner, ServiceType.Water, [Identifier()], default)).State);
        Assert.Equal("NotFound", (await Resolver().ResolveAsync(owner, ServiceType.Electricity, [Identifier("98765432101")], default)).State);
    }

    [Fact]
    public async Task IndependentUsersMayRegisterSameCodeWithoutInterference()
    {
        var a = await Register(owner); var other = Guid.NewGuid(); var b = await Register(other);
        Assert.Equal(a.Id, (await Resolver().ResolveAsync(owner, ServiceType.Electricity, [Identifier()], default)).ServiceAccountId);
        Assert.Equal(b.Id, (await Resolver().ResolveAsync(other, ServiceType.Electricity, [Identifier()], default)).ServiceAccountId);
    }

    [Fact]
    public async Task InactiveAccountOrIdentifierAndKeywordsDoNotResolve()
    {
        var a = await Register(owner); a.Deactivate(DateTimeOffset.UtcNow);
        Assert.Equal("NotFound", (await Resolver().ResolveAsync(owner, ServiceType.Electricity, [Identifier()], default)).State);
        await Register(owner, identifierType: ServiceIdentifierType.Keyword, value: "ENERGIA");
        Assert.Equal("NotFound", (await Resolver().ResolveAsync(owner, ServiceType.Electricity, [Identifier("ENERGIA", ServiceIdentifierType.Keyword)], default)).State);
    }

    [Fact]
    public async Task AmbiguousLegacyDataNeverChoosesAnAccount()
    {
        await Register(owner); await Register(owner); // Fake repository intentionally permits a corrupt duplicate.
        var result = await Resolver().ResolveAsync(owner, ServiceType.Electricity, [Identifier()], default);
        Assert.Equal("Ambiguous", result.State); Assert.Null(result.Issuer);
    }

    [Fact]
    public async Task ElectricityParserAndResolverProduceRegisteredProvenanceWithoutChangingTextEvidence()
    {
        await Register(owner);
        var extraction = await new CompositeServiceBillExtractor(new(), new ForbiddenAgent()).ExtractAsync(new("synthetic.pdf", new([new(1,
            "UC: " + FakeUc + "\nCompetência: Vencimento: Valor cobrado (R$):\n02/2028 12/03/2028 123,45")]), "ElectricityBill"), default);
        Assert.Null(extraction.Result.Issuer); Assert.Equal("IssuerNotFound", extraction.Result.FailureCode);
        Assert.Single(extraction.Result.Identifiers);
        var result = await Resolver().EnrichAsync(owner, extraction, default);
        Assert.Equal("Reliable", result.Assessment); Assert.Equal("Equatorial", result.Result.Issuer);
        Assert.Equal("RegisteredServiceAccountIdentifier", result.Result.IssuerSource); Assert.Null(result.Result.IssuerExtracted);
        Assert.Null(result.Result.FailureCode); Assert.Equal("123.45", result.Result.TotalAmount);
        Assert.Equal("NeedsReview", (await Resolver().EnrichAsync(Guid.NewGuid(), extraction, default)).Assessment);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("98765432100", json); Assert.DoesNotContain(FakeUc, json);
    }

    [Fact]
    public async Task TextualConflictAndAmbiguityRequireReviewAndDoNotOverwriteIssuer()
    {
        await Register(owner);
        var text = new ServiceBillExtractionOutput(new("ElectricityBill", "OutroEmissor", "2028-02", "2028-03-12", "123.45", null, null, "High", "Deterministic", null)
        { IssuerExtracted = "OutroEmissor", IssuerSource = "DeterministicText", Identifiers = [Identifier()] }, "Reliable");
        var result = await Resolver().EnrichAsync(owner, text, default);
        Assert.Equal("NeedsReview", result.Assessment); Assert.Equal("IssuerConflict", result.Result.FailureCode);
        Assert.Equal("OutroEmissor", result.Result.Issuer); Assert.Equal("DeterministicText", result.Result.IssuerSource);
        var agreement = await Resolver().EnrichAsync(owner, text with { Result = text.Result with { Issuer = "CEEE" } }, default);
        Assert.Equal("Reliable", agreement.Assessment); Assert.Equal("DeterministicText", agreement.Result.IssuerSource);
        await Register(owner);
        Assert.Equal("AmbiguousServiceAccount", (await Resolver().EnrichAsync(owner, text, default)).Result.FailureCode);
    }

    [Fact]
    public void ProtectedValueRoundTripsAndHmacIsOwnerAndTypeScoped()
    {
        var provider = new EphemeralDataProtectionProvider(); var crypto = Protection(provider);
        var normalized = ServiceIdentifierNormalization.Normalize(ServiceIdentifierType.UtilityCustomerNumber, FakeUc);
        var ciphertext = crypto.Protect(owner, normalized);
        Assert.DoesNotContain(normalized, Encoding.UTF8.GetString(ciphertext));
        Assert.Equal(normalized, Encoding.UTF8.GetString(provider.CreateProtector("Saldo360.ServiceAccountIdentifiers.v1", owner.ToString("N")).Unprotect(ciphertext)));
        var hash = crypto.Hash(owner, ServiceType.Electricity, ServiceIdentifierType.UtilityCustomerNumber, normalized);
        Assert.Equal(hash, crypto.Hash(owner, ServiceType.Electricity, ServiceIdentifierType.UtilityCustomerNumber, normalized));
        Assert.NotEqual(hash, crypto.Hash(Guid.NewGuid(), ServiceType.Electricity, ServiceIdentifierType.UtilityCustomerNumber, normalized));
        Assert.NotEqual(hash, crypto.Hash(owner, ServiceType.Water, ServiceIdentifierType.UtilityCustomerNumber, normalized));
        Assert.NotEqual(hash, crypto.Hash(owner, ServiceType.Electricity, ServiceIdentifierType.ContractNumber, normalized));
    }

    [Theory]
    [InlineData("12<script>")][InlineData("12A3")][InlineData("--")]
    public void UcRejectsInvalidCharacters(string value) => Assert.Throws<ArgumentException>(() => ServiceIdentifierNormalization.Normalize(ServiceIdentifierType.UtilityCustomerNumber, value));

    [Fact]
    public async Task MissingProtectionRequiresReviewWithoutFallingBackToPlaintext()
    {
        var account = await Register(owner);
        var unavailable = new ServiceAccountIdentifierProtection(new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build());
        Assert.False(unavailable.IsConfigured);
        var resolver = new ServiceAccountResolver(repository, unavailable);
        Assert.Equal("NeedsReview", (await resolver.ResolveAsync(owner, ServiceType.Electricity, [Identifier()], default)).State);
        var service = new ServiceAccountService(repository, unavailable, TimeProvider.System);
        Assert.Equal("ProtectionUnavailable", await service.AddIdentifierAsync(owner, account.Id, ServiceIdentifierType.CustomerCode, "ABC-123", default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid-base64")]
    [InlineData("AQID")]
    public void ProductionRejectsMissingOrInvalidHmacKey(string? key)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ServiceAccounts:IdentifierHmacKey"] = key }).Build();
        var crypto = new ServiceAccountIdentifierProtection(new EphemeralDataProtectionProvider(), config);
        Assert.Throws<InvalidOperationException>(() => crypto.ValidateEnvironment("Production"));
        crypto.ValidateEnvironment("Testing");
        Protection().ValidateEnvironment("Production");
    }

    [Fact]
    public void LegacyReferencesAreMaskedInPublicJson()
    {
        var bill = new ServiceBillExtractionResult("ElectricityBill", null, null, null, null, FakeUc, FakeUc, "Medium", "Deterministic", "IssuerNotFound");
        var json = JsonSerializer.Serialize(bill);
        Assert.DoesNotContain(FakeUc, json);
        Assert.Contains("***", json);
        Assert.Equal(FakeUc, bill.AccountReference);
    }

    [Fact]
    public void IdentifierLabelsPreserveGenerationContextAndDoNotScanArbitraryCpfOrPrefixes()
    {
        var result = ServiceBillIdentifiers.Extract(new([new(1, "CPF: 111.222.333-00\nUnidade Consumidora geradora 987.654.321-00: informação\nUC: 987 654 321 00\nContrato: AB-987\nInstalação: XY-123\nCódigo do Cliente: ZZ-456")]));
        var uc = Assert.Single(result, i => i.Type == ServiceIdentifierType.UtilityCustomerNumber);
        Assert.Equal("98765432100", uc.NormalizedValue); Assert.Equal("GenerationUnit", uc.Context);
        Assert.DoesNotContain(result, i => i.NormalizedValue == "11122233300");
        Assert.Equal(4, result.Count);
    }

    private sealed class ForbiddenAgent : IServiceBillExtractionAgent
    { public Task<string> ExtractAsync(ServiceBillExtractionRequest request, CancellationToken ct) => throw new InvalidOperationException("Forbidden"); }
    private sealed class Repository : IServiceAccountRepository
    {
        private readonly List<ServiceAccount> accounts = []; private readonly List<ServiceAccountIdentifier> identifiers = [];
        public Task AddAsync(ServiceAccount account, CancellationToken ct) { accounts.Add(account); return Task.CompletedTask; }
        public Task<bool> TryAddIdentifierAsync(ServiceAccountIdentifier identifier, CancellationToken ct) { identifiers.Add(identifier); return Task.FromResult(true); }
        public Task<ServiceAccount?> GetAsync(Guid userId, Guid id, CancellationToken ct) => Task.FromResult(accounts.SingleOrDefault(a => a.Id == id && a.UserId == userId));
        public Task<bool> HasActiveAsync(Guid userId, ServiceType type, CancellationToken ct) => Task.FromResult(accounts.Any(a => a.UserId == userId && a.ServiceType == type && a.IsActive));
        public Task<IReadOnlyList<ServiceAccount>> FindAsync(Guid userId, ServiceType type, IReadOnlyList<(ServiceIdentifierType Type, string Hash)> hashes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ServiceAccount>>(accounts.Where(a => a.UserId == userId && a.ServiceType == type && a.IsActive && identifiers.Any(i => i.IsActive && i.ServiceAccountId == a.Id && hashes.Contains((i.Type, i.LookupHash)))).ToList());
        public Task<IReadOnlyList<ServiceAccountDto>> ListAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
    }
}

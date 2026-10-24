using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class CreditCardStatementResolverTests
{
    [Fact]
    public void LegacyHeadersRetainTheirSerializedShapeForOfficialJournalHashes()
    {
        const string legacy = "{\"issuer\":\"Bank\",\"cardLastDigits\":\"1234\",\"competence\":null,\"closingDate\":null,\"dueDate\":null,\"officialTotal\":null}";
        var header = System.Text.Json.JsonSerializer.Deserialize<CreditCardStatementAgentHeader>(legacy, CreditCardStatementAgentSchema.Json);
        Assert.Equal(legacy, System.Text.Json.JsonSerializer.Serialize(header, CreditCardStatementAgentSchema.Json));
    }

    [Theory]
    [InlineData("Diego Silva", CreditCardStatementResolutionStatus.Matched)]
    [InlineData("Outro Titular", CreditCardStatementResolutionStatus.InsufficientEvidence)]
    public async Task DocumentaryHolderCanContradictButNeverReplaceIssuerAndDigits(string name, CreditCardStatementResolutionStatus expected)
    {
        var card = new CreditCardStatementCandidate(Guid.NewGuid(), "Bank", "1234", Guid.NewGuid(), "Diego Silva");
        var resolver = new CreditCardStatementResolver(new FakeRepository([card]));
        var header = new CreditCardStatementAgentHeader("Bank", "1234", null, null, null, null) { HolderName = name };
        Assert.Equal(expected, (await resolver.ResolveDocumentAsync(Guid.NewGuid(), header, default)).Status);
        Assert.Equal(CreditCardStatementResolutionStatus.InsufficientEvidence, (await resolver.ResolveDocumentAsync(Guid.NewGuid(), header with { CardLastDigits = null }, default)).Status);
    }

    [Fact]
    public async Task ResolvesExactIssuerAndDigitsWithKnownAccentNormalization()
    {
        var id = Guid.NewGuid();
        var result = await Resolve([new(id, "Itaú", "0012", null)], "Itau", "0012");
        Assert.Equal(CreditCardStatementResolutionStatus.Matched, result.Status);
        Assert.Equal(id, result.CardId);
    }

    [Fact]
    public async Task NeverMatchesByIssuerAloneOrByUnrelatedFacts()
    {
        var cards = new[] { new CreditCardStatementCandidate(Guid.NewGuid(), "Itaú", "1234", null) };
        Assert.Equal(CreditCardStatementResolutionStatus.InsufficientEvidence, (await Resolve(cards, "Itaú", null)).Status);
        Assert.Equal(CreditCardStatementResolutionStatus.NotFound, (await Resolve(cards, "Itaú", "5678")).Status);
        Assert.Equal(CreditCardStatementResolutionStatus.NotFound, (await Resolve(cards, "Outro Banco", "1234")).Status);
    }

    [Fact]
    public async Task ReturnsAmbiguousInsteadOfSelectingFirst()
    {
        var cards = new[]
        {
            new CreditCardStatementCandidate(Guid.NewGuid(), "Banco", "1234", null),
            new CreditCardStatementCandidate(Guid.NewGuid(), "Banco", "1234", null),
        };
        Assert.Equal(CreditCardStatementResolutionStatus.Ambiguous, (await Resolve(cards, "Banco", "1234")).Status);
    }

    [Fact]
    public async Task UsesTrustedHolderWhenProvided()
    {
        var holder = Guid.NewGuid(); var expected = Guid.NewGuid();
        var cards = new[]
        {
            new CreditCardStatementCandidate(expected, "Banco", "1234", holder),
            new CreditCardStatementCandidate(Guid.NewGuid(), "Banco", "1234", Guid.NewGuid()),
        };
        var result = await new CreditCardStatementResolver(new FakeRepository(cards)).ResolveAsync(Guid.NewGuid(), "Banco", "1234", holder, CancellationToken.None);
        Assert.Equal(expected, result.CardId);
    }

    private static Task<CreditCardStatementResolution> Resolve(IReadOnlyList<CreditCardStatementCandidate> cards, string? issuer, string? digits) =>
        new CreditCardStatementResolver(new FakeRepository(cards)).ResolveAsync(Guid.NewGuid(), issuer, digits, null, CancellationToken.None);

    private sealed class FakeRepository(IReadOnlyList<CreditCardStatementCandidate> cards) : ICreditCardStatementResolutionRepository
    {
        public Task<IReadOnlyList<CreditCardStatementCandidate>> ListActiveAsync(Guid workspaceId, CancellationToken cancellationToken) => Task.FromResult(cards);
    }
}

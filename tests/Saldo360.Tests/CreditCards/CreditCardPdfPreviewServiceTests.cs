using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
public sealed class CreditCardPdfPreviewServiceTests
{
    private static readonly Guid HolderId = Guid.NewGuid();

    [Fact]
    public async Task UnprotectedPdfDoesNotResolveIdentityMaterial()
    {
        var provider = new FakePasswordProvider(["12345"]);
        var service = CreateService(new FakeExtractor(requiredPassword: null), provider);

        var result = await PreviewAsync(service);

        Assert.Equal(CreditCardPdfPreviewAccess.Allowed, result.Access);
        Assert.Equal("Unprotected", result.Preview!.Security.Status);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ProtectedPdfUsesDeterministicCandidatesAndMarksPreviewAsUnlocked()
    {
        var provider = new FakePasswordProvider(["99999", "123"]);
        var extractor = new FakeExtractor("123");
        var service = CreateService(extractor, provider);

        var result = await PreviewAsync(service);

        Assert.Equal(CreditCardPdfPreviewAccess.Allowed, result.Access);
        Assert.Equal("Unlocked", result.Preview!.Security.Status);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(["99999"], extractor.Attempts[1]);
        Assert.Equal(["123"], extractor.Attempts[2]);
    }

    [Fact]
    public async Task ManualPasswordIsEphemeralAndTriedBeforeDerivedCandidates()
    {
        var provider = new FakePasswordProvider(["12345"]);
        var extractor = new FakeExtractor("manual-secret");
        var service = CreateService(extractor, provider);

        var result = await PreviewAsync(service, "manual-secret");

        Assert.Equal(CreditCardPdfPreviewAccess.Allowed, result.Access);
        Assert.Equal("manual-secret", extractor.Attempts[1][0]);
        Assert.Equal(0, provider.Calls);
        Assert.DoesNotContain("manual-secret", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task MissingIdentityAndRejectedPasswordHaveDistinctSafeMessages()
    {
        var unavailable = await PreviewAsync(CreateService(new FakeExtractor("12345"), new FakePasswordProvider([])));
        Assert.Contains("material de identidade", unavailable.Errors!["file"].Single(), StringComparison.OrdinalIgnoreCase);

        var rejected = await PreviewAsync(CreateService(new FakeExtractor("correct"), new FakePasswordProvider(["wrong"])));
        var serialized = System.Text.Json.JsonSerializer.Serialize(rejected);
        Assert.Contains("credenciais disponíveis", rejected.Errors!["file"].Single(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wrong", serialized);
        Assert.DoesNotContain("correct", serialized);
    }

    [Fact]
    public async Task UnsupportedEncryptionIsNotReportedAsCorruption()
    {
        var result = await PreviewAsync(CreateService(new FakeExtractor(CreditCardPdfReadFailure.UnsupportedEncryption), new FakePasswordProvider([])));

        Assert.Contains("criptografia", result.Errors!["file"].Single(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corrompido", result.Errors["file"].Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnlockedPdfWithoutTextIsReportedAsNonExtractable()
    {
        var service = CreateService(new FakeExtractor("12345", text: string.Empty), new FakePasswordProvider(["12345"]));

        var result = await PreviewAsync(service);

        Assert.Contains("OCR não é suportado", result.Errors!["file"].Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlocksStatementWhoseDigitsDifferFromSelectedCard()
    {
        var service = new CreditCardPdfPreviewService(new FakeContextRepository("5678"), new FakeExtractor(null, cardLastDigits: "1234"), new FakePasswordProvider([]));
        var result = await PreviewAsync(service);
        Assert.Equal(CreditCardPdfPreviewAccess.Invalid, result.Access);
        Assert.Contains("1234", result.Errors!["cardId"].Single());
        Assert.Contains("5678", result.Errors["cardId"].Single());
    }

    private static CreditCardPdfPreviewService CreateService(FakeExtractor extractor, FakePasswordProvider provider) =>
        new(new FakeContextRepository(), extractor, provider);

    private static Task<CreditCardPdfPreviewResult> PreviewAsync(CreditCardPdfPreviewService service, string? password = null)
    {
        var bytes = new byte[] { 1, 2, 3 };
        return service.PreviewAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new MemoryStream(bytes), bytes.Length,
            "fatura.pdf", null, "2026-10", null, password, CancellationToken.None);
    }

    private sealed class FakeContextRepository(string? lastFourDigits = null) : ICreditCardPdfContextRepository
    {
        public Task<CreditCardPdfContext?> GetAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<CreditCardPdfContext?>(new("Banco Teste", HolderId, lastFourDigits));
    }

    private sealed class FakePasswordProvider(IReadOnlyList<string> candidates) : ICreditCardPdfPasswordProvider
    {
        public int Calls { get; private set; }
        public Task<CreditCardPdfPasswordResolution> ResolveAsync(Guid holderUserId, string issuer, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HolderId, holderUserId);
            return Task.FromResult(new CreditCardPdfPasswordResolution(candidates));
        }
    }

    private sealed class FakeExtractor : ICreditCardPdfTextExtractor
    {
        private readonly string? _requiredPassword;
        private readonly CreditCardPdfReadFailure? _failure;
        private readonly string _text;
        private readonly string? _cardLastDigits;
        public List<IReadOnlyList<string>> Attempts { get; } = [];

        public FakeExtractor(string? requiredPassword, string text = "10/10/2026 MERCADO 10.00", string? cardLastDigits = null) => (_requiredPassword, _text, _cardLastDigits) = (requiredPassword, text, cardLastDigits);
        public FakeExtractor(CreditCardPdfReadFailure failure) => (_failure, _text, _cardLastDigits) = (failure, string.Empty, null);

        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken)
        {
            Attempts.Add(passwords.ToArray());
            if (_failure is { } failure) throw new CreditCardPdfReadException(failure);
            if (_requiredPassword is not null && passwords.Count == 0) throw new CreditCardPdfReadException(CreditCardPdfReadFailure.PasswordRequired);
            if (_requiredPassword is not null && !passwords.Contains(_requiredPassword, StringComparer.Ordinal)) throw new CreditCardPdfReadException(CreditCardPdfReadFailure.PasswordRejected);
            return Task.FromResult(new CreditCardPdfExtractedDocument([new(1, _text)]));
        }

        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat) =>
            new(fileName, 1, "Generic", new(null, _cardLastDigits, competence, null, null, null), new(1, 0, 0, 0), [], []);
    }
}

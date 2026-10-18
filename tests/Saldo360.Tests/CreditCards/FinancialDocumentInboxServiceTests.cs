using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class FinancialDocumentInboxServiceTests
{
    private static readonly Guid FamilyId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid CardId = Guid.NewGuid();

    [Fact]
    public async Task ReceivesStoresHashesAndIdentifiesSupportedStatement()
    {
        var fixture = Fixture(Preview("Nubank", "Reconciled", "1234"));
        var result = await Receive(fixture.Service, [1, 2, 3], "mail-1");

        Assert.Equal("Created", result.Result);
        Assert.Equal("ReadyToConfirm", result.Item!.Status);
        Assert.Equal(CardId, result.Item.CreditCardId);
        Assert.Equal(64, result.Item.ContentHash.Length);
        Assert.Single(fixture.Storage.Objects);
    }

    [Fact]
    public async Task SameHashAcrossSourcesIsIdempotentAndExternalIdWithDifferentHashConflicts()
    {
        var fixture = Fixture(Preview("Nubank", "Reconciled", "1234"));
        var first = await Receive(fixture.Service, [1, 2, 3], "mail-1");
        var repeated = await Receive(fixture.Service, [1, 2, 3], "other-id");
        var conflict = await Receive(fixture.Service, [9, 8, 7], "mail-1");

        Assert.Equal("AlreadyExists", repeated.Result);
        Assert.Equal(first.Item!.Id, repeated.Item!.Id);
        Assert.Equal("Conflict", conflict.Result);
        Assert.Single(fixture.Repository.Items);
        Assert.Single(fixture.Storage.Objects);
    }

    [Theory]
    [InlineData("Nubank", "Reconciled", null)]
    [InlineData("Generic", "NeedsReview", "1234")]
    [InlineData("Nubank", "Divergent", "1234")]
    public async Task AmbiguousGenericOrDivergentDocumentNeedsReview(string parser, string reconciliation, string? digits)
    {
        var fixture = Fixture(Preview(parser, reconciliation, digits));
        var result = await Receive(fixture.Service, [4, 5, 6], null);
        Assert.Equal("NeedsReview", result.Item!.Status);
        if (digits is null) Assert.Null(result.Item.CreditCardId); else Assert.Equal(CardId, result.Item.CreditCardId);
    }

    [Fact]
    public async Task MissingStoredObjectFailsReprocessingWithSanitizedCode()
    {
        var fixture = Fixture(Preview("Nubank", "Reconciled", "1234"));
        var received = await Receive(fixture.Service, [7, 7], null);
        fixture.Storage.Objects.Clear();
        var result = await fixture.Service.ReprocessAsync(FamilyId, received.Item!.Id, UserId, null, null, CancellationToken.None);
        Assert.Equal("Failed", result!.Status);
        Assert.Equal("StorageObjectMissing", result.FailureCode);
    }

    private static (FinancialDocumentInboxService Service, FakeRepository Repository, FakeStorage Storage) Fixture(CreditCardPdfPreview preview)
    {
        var repository = new FakeRepository(); var storage = new FakeStorage();
        var resolver = new CreditCardStatementResolver(new FakeResolutionRepository());
        return (new(repository, storage, new FakeExtractor(preview), new FakePreviewService(preview), resolver, TimeProvider.System), repository, storage);
    }
    private static Task<FinancialDocumentInboxReceiveResult> Receive(FinancialDocumentInboxService service, byte[] bytes, string? externalId) =>
        service.ReceiveAsync(FamilyId, UserId, new MemoryStream(bytes), bytes.Length, "fatura.pdf", "application/pdf",
            FinancialDocumentSource.Manual, externalId, null, null, CancellationToken.None);
    private static CreditCardPdfPreview Preview(string parser, string reconciliation, string? digits) =>
        new("fatura.pdf", 1, parser, new("Nubank", digits, "2026-10", "2026-10-05", "2026-10-13", "10.00"),
            new(1, 0, 0, 0), [], []) { Reconciliation = new(reconciliation, "10.00", reconciliation == "Divergent" ? "9.00" : "10.00", reconciliation == "Divergent" ? "1.00" : "0.00", false) };

    private sealed class FakeRepository : IFinancialDocumentInboxRepository
    {
        public List<FinancialDocumentInboxItem> Items { get; } = [];
        public Task<bool> CanAccessFamilyAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) => Task.FromResult(familyId == FamilyId && userId == UserId);
        public Task<bool> FamilyExistsAsync(Guid familyId, CancellationToken cancellationToken) => Task.FromResult(familyId == FamilyId);
        public Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid familyId, string hash, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.FamilyId == familyId && item.ContentHash == hash));
        public Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid familyId, FinancialDocumentSource source, string externalId, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.FamilyId == familyId && item.Source == source && item.ExternalSourceId == externalId));
        public Task<FinancialDocumentInboxItem?> GetAsync(Guid familyId, Guid id, Guid userId, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.FamilyId == familyId && item.Id == id && userId == UserId));
        public Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialDocumentInboxItem>>(Items.Where(item => item.FamilyId == familyId).ToList());
        public Task<bool> TryAddAsync(FinancialDocumentInboxItem item, CancellationToken cancellationToken) { Items.Add(item); return Task.FromResult(true); }
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class FakeStorage : IFinancialDocumentStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public async Task<string> SaveAsync(Guid familyId, Guid documentId, Stream content, CancellationToken cancellationToken) { using var memory = new MemoryStream(); await content.CopyToAsync(memory, cancellationToken); var key = $"{familyId:N}/{documentId:N}.pdf"; Objects[key] = memory.ToArray(); return key; }
        public Task<Stream?> OpenReadAsync(string reference, CancellationToken cancellationToken) => Task.FromResult<Stream?>(Objects.TryGetValue(reference, out var bytes) ? new MemoryStream(bytes) : null);
        public Task DeleteAsync(string reference, CancellationToken cancellationToken) { Objects.Remove(reference); return Task.CompletedTask; }
    }
    private sealed class FakeExtractor(CreditCardPdfPreview preview) : ICreditCardPdfTextExtractor
    {
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken) => Task.FromResult(new CreditCardPdfExtractedDocument([new(1, "text")]));
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat) => preview;
    }
    private sealed class FakePreviewService(CreditCardPdfPreview preview) : ICreditCardPdfPreviewService
    {
        public Task<CreditCardPdfPreviewResult> PreviewAsync(Guid familyId, Guid cardId, Guid userId, Stream pdf, long length, string fileName, string? issuerProfile, string? competence, string? dateFormat, string? password, CancellationToken cancellationToken) => Task.FromResult(new CreditCardPdfPreviewResult(CreditCardPdfPreviewAccess.Allowed, preview, null));
    }
    private sealed class FakeResolutionRepository : ICreditCardStatementResolutionRepository
    {
        public Task<IReadOnlyList<CreditCardStatementCandidate>> ListActiveAsync(Guid familyId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CreditCardStatementCandidate>>([new(CardId, "Nubank", "1234", UserId)]);
    }
}

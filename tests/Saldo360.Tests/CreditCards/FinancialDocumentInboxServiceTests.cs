using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class FinancialDocumentInboxServiceTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid CardId = Guid.NewGuid();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnDemandEmailStoresAndDeduplicatesWithoutAnyAnalysis(bool review)
    {
        var fixture = Fixture(Preview("Nubank", "Reconciled", "1234"), true);
        var date = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
        async Task<FinancialDocumentInboxReceiveResult> ReceivePending(string id, byte[] bytes) =>
            await fixture.Service.ReceiveEmailPendingAsync(WorkspaceId, UserId, new MemoryStream(bytes), bytes.Length,
                "bill.pdf", id, date, review, default);
        var first = await ReceivePending("gmail:m:part:1", [1, 2, 3]);
        var repeated = await ReceivePending("gmail:m:part:1", [1, 2, 3]);
        var sameHash = await ReceivePending("gmail:other:part:1", [1, 2, 3]);
        var conflict = await ReceivePending("gmail:m:part:1", [9, 8, 7]);
        Assert.Equal("Created", first.Result); Assert.Equal(review ? "NeedsReview" : "Received", first.Item!.Status);
        Assert.Null(first.Item.CreditCardId); Assert.Null(first.Item.Parser); Assert.Null(first.Item.ClassifiedAt);
        Assert.Equal("AlreadyExists", repeated.Result); Assert.Equal(first.Item.Id, sameHash.Item!.Id);
        Assert.Equal("Conflict", conflict.Result); Assert.Single(fixture.Repository.Items); Assert.Single(fixture.Storage.Objects);
        Assert.Equal(0, fixture.Extractor.ParseCalls);
    }

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
        var result = await fixture.Service.ReprocessAsync(WorkspaceId, received.Item!.Id, UserId, null, null, CancellationToken.None);
        Assert.Equal("Failed", result!.Status);
        Assert.Equal("StorageObjectMissing", result.FailureCode);
    }

    [Theory]
    [InlineData("ElectricityBill")]
    [InlineData("WaterBill")]
    [InlineData("BillingDocument")]
    [InlineData("Unknown")]
    public async Task ClassificationIsPersistedAndNonCardDocumentsNeverReachCardParser(string documentType)
    {
        var classification = new FinancialDocumentClassificationResult(documentType, documentType == "Unknown" ? "Low" : "High", [], documentType == "Unknown" ? "Unknown" : "Deterministic", documentType == "Unknown" ? "InsufficientEvidence" : null);
        var fixture = Fixture(Preview("Nubank", "Reconciled", "1234"), true, classification);

        var result = await Receive(fixture.Service, [1, 3, 5], null);

        Assert.Equal(documentType, result.Item!.DocumentType);
        Assert.NotNull(result.Item.ClassifiedAt);
        Assert.Equal("NeedsReview", result.Item.Status);
        Assert.Equal(documentType == "Unknown" ? "InsufficientEvidence" : "RecognizedDocumentWithoutExtractor", result.Item.FailureCode);
        Assert.Equal(0, fixture.Extractor.ParseCalls);
        Assert.Null(result.Item.CreditCardId);
    }

    [Fact]
    public async Task CreditCardClassificationContinuesExistingCardPipeline()
    {
        var fixture = Fixture(Preview("Nubank", "Reconciled", "1234"), true,
            new("CreditCardStatement", "High", [], "Deterministic"));
        var result = await Receive(fixture.Service, [2, 4, 6], null);
        Assert.Equal("CreditCardStatement", result.Item!.DocumentType);
        Assert.Equal("ReadyToConfirm", result.Item.Status);
        Assert.Equal(1, fixture.Extractor.ParseCalls);
    }

    private static (FinancialDocumentInboxService Service, FakeRepository Repository, FakeStorage Storage, FakeExtractor Extractor) Fixture(CreditCardPdfPreview preview,
        bool classificationEnabled = false, FinancialDocumentClassificationResult? classification = null)
    {
        var repository = new FakeRepository(); var storage = new FakeStorage(); var extractor = new FakeExtractor(preview);
        var resolver = new CreditCardStatementResolver(new FakeResolutionRepository());
        return (new(repository, storage, extractor, new FakePreviewService(preview), resolver,
            new FinancialDocumentClassificationOptions { Enabled = classificationEnabled }, new FakeClassifier(classification), new FinancialDocumentAnalysisRouter(), TimeProvider.System), repository, storage, extractor);
    }
    private static Task<FinancialDocumentInboxReceiveResult> Receive(FinancialDocumentInboxService service, byte[] bytes, string? externalId) =>
        service.ReceiveAsync(WorkspaceId, UserId, new MemoryStream(bytes), bytes.Length, "fatura.pdf", "application/pdf",
            FinancialDocumentSource.Manual, externalId, null, null, CancellationToken.None);
    private static CreditCardPdfPreview Preview(string parser, string reconciliation, string? digits) =>
        new("fatura.pdf", 1, parser, new("Nubank", digits, "2026-10", "2026-10-05", "2026-10-13", "10.00"),
            new(1, 0, 0, 0), [], []) { Reconciliation = new(reconciliation, "10.00", reconciliation == "Divergent" ? "9.00" : "10.00", reconciliation == "Divergent" ? "1.00" : "0.00", false) };

    private sealed class FakeRepository : IFinancialDocumentInboxRepository
    {
        public List<FinancialDocumentInboxItem> Items { get; } = [];
        public Task<bool> CanAccessWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) => Task.FromResult(workspaceId == WorkspaceId && userId == UserId);
        public Task<bool> WorkspaceExistsAsync(Guid workspaceId, CancellationToken cancellationToken) => Task.FromResult(workspaceId == WorkspaceId);
        public Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid workspaceId, string hash, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.WorkspaceId == workspaceId && item.ContentHash == hash));
        public Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid workspaceId, FinancialDocumentSource source, string externalId, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.WorkspaceId == workspaceId && item.Source == source && item.ExternalSourceId == externalId));
        public Task<FinancialDocumentInboxItem?> GetAsync(Guid workspaceId, Guid id, Guid userId, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.WorkspaceId == workspaceId && item.Id == id && userId == UserId));
        public Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialDocumentInboxItem>>(Items.Where(item => item.WorkspaceId == workspaceId).ToList());
        public Task<bool> TryAddAsync(FinancialDocumentInboxItem item, CancellationToken cancellationToken) { Items.Add(item); return Task.FromResult(true); }
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class FakeStorage : IFinancialDocumentStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public async Task<string> SaveAsync(Guid workspaceId, Guid documentId, Stream content, CancellationToken cancellationToken) { using var memory = new MemoryStream(); await content.CopyToAsync(memory, cancellationToken); var key = $"{workspaceId:N}/{documentId:N}.pdf"; Objects[key] = memory.ToArray(); return key; }
        public Task<Stream?> OpenReadAsync(string reference, CancellationToken cancellationToken) => Task.FromResult<Stream?>(Objects.TryGetValue(reference, out var bytes) ? new MemoryStream(bytes) : null);
        public Task DeleteAsync(string reference, CancellationToken cancellationToken) { Objects.Remove(reference); return Task.CompletedTask; }
    }
    private sealed class FakeExtractor(CreditCardPdfPreview preview) : ICreditCardPdfTextExtractor
    {
        public int ParseCalls { get; private set; }
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken) => Task.FromResult(new CreditCardPdfExtractedDocument([new(1, "text")]));
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat) { ParseCalls++; return preview; }
    }
    private sealed class FakePreviewService(CreditCardPdfPreview preview) : ICreditCardPdfPreviewService
    {
        public Task<CreditCardPdfPreviewResult> PreviewAsync(Guid workspaceId, Guid cardId, Guid userId, Stream pdf, long length, string fileName, string? issuerProfile, string? competence, string? dateFormat, string? password, CancellationToken cancellationToken) => Task.FromResult(new CreditCardPdfPreviewResult(CreditCardPdfPreviewAccess.Allowed, preview, null));
    }
    private sealed class FakeResolutionRepository : ICreditCardStatementResolutionRepository
    {
        public Task<IReadOnlyList<CreditCardStatementCandidate>> ListActiveAsync(Guid workspaceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CreditCardStatementCandidate>>([new(CardId, "Nubank", "1234", UserId)]);
    }
    private sealed class FakeClassifier(FinancialDocumentClassificationResult? value = null) : IFinancialDocumentClassifier { public Task<FinancialDocumentClassificationResult> ClassifyAsync(FinancialDocumentClassificationRequest request, CancellationToken cancellationToken) => Task.FromResult(value ?? new FinancialDocumentClassificationResult("CreditCardStatement", "High", [], "Deterministic")); }
}

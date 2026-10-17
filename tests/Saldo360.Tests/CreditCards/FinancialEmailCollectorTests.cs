using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class FinancialEmailCollectorTests
{
    private static readonly Guid FamilyId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task CollectsMultiplePdfsIndependentlyAndSkipsUnsupportedAttachments()
    {
        var source = new FakeEmailSource();
        source.Add("m1", new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), ("a1", "Fatura_MASTERCARD_xxx.pdf", "application/octet-stream", Pdf(1)), ("a2", "image.png", "image/png", new byte[] { 2 }));
        source.Add("m2", new(2026, 10, 17, 13, 0, 0, TimeSpan.Zero), ("a3", "two.pdf", "application/pdf", Pdf(3)));
        var fixture = Create(source, Preview("Nubank", "Reconciled", null));

        var result = await fixture.Collector.RunAsync("Bills", UserId, false, CancellationToken.None);

        Assert.Equal(2, result!.AcceptedDocuments);
        Assert.Equal(2, result.NeedsReview);
        Assert.Equal(3, result.AttachmentsScanned);
        Assert.Equal(2, fixture.Inbox.Items.Count);
        Assert.All(fixture.Inbox.Items, item => Assert.Equal(FinancialDocumentSource.Email, item.Source));
        Assert.Equal(2, source.Downloads);
        Assert.Equal(0, source.Mutations);
    }

    [Fact]
    public async Task DryRunListsStableExternalIdsWithoutDownloadingOrPersisting()
    {
        var source = new FakeEmailSource(); source.Add("message", DateTimeOffset.UtcNow, ("attachment", "bill.pdf", "application/pdf", Pdf(1)));
        var fixture = Create(source, Preview("Nubank", "Reconciled", null));
        var result = await fixture.Collector.RunAsync("Bills", UserId, true, CancellationToken.None);
        Assert.Equal(["gmail:message:attachment"], result!.CandidateExternalIds);
        Assert.Empty(fixture.Inbox.Items); Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task RepeatedRunAndSameHashFromAnotherMessageRemainIdempotent()
    {
        var bytes = Pdf(8); var source = new FakeEmailSource();
        source.Add("m1", DateTimeOffset.UtcNow, ("a1", "one.pdf", "application/pdf", bytes));
        source.Add("m2", DateTimeOffset.UtcNow.AddMinutes(1), ("a2", "two.pdf", "application/pdf", bytes));
        var fixture = Create(source, Preview("Generic", "NeedsReview", null));
        var first = await fixture.Collector.RunAsync("Bills", UserId, false, CancellationToken.None);
        var second = await fixture.Collector.RunAsync("Bills", UserId, false, CancellationToken.None);
        Assert.Equal(1, first!.AcceptedDocuments); Assert.Equal(1, first.Duplicates);
        Assert.Equal(0, second!.AcceptedDocuments); Assert.Equal(2, second.Duplicates);
        Assert.Single(fixture.Inbox.Items);
    }

    [Fact]
    public async Task ProtectedPdfIsRetainedForReviewAndOneFailureDoesNotAbortBatch()
    {
        var source = new FakeEmailSource();
        source.Add("bad", DateTimeOffset.UtcNow, ("fail", "bad.pdf", "application/pdf", Pdf(1)));
        source.Add("protected", DateTimeOffset.UtcNow.AddMinutes(1), ("locked", "locked.pdf", "application/pdf", Pdf(2)));
        source.FailAttachmentId = "fail";
        var fixture = Create(source, null, CreditCardPdfReadFailure.PasswordRequired);
        var result = await fixture.Collector.RunAsync("Bills", UserId, false, CancellationToken.None);
        Assert.Equal(1, result!.AcceptedDocuments); Assert.Equal(1, result.Failures);
        Assert.Equal(FinancialDocumentInboxStatus.NeedsReview, Assert.Single(fixture.Inbox.Items).Status);
        Assert.Equal("PasswordRequired", fixture.Inbox.Items[0].FailureCode);
    }

    [Fact]
    public async Task PdfExtensionWithInvalidSignatureIsRejectedBeforeInbox()
    {
        var source = new FakeEmailSource(); source.Add("m1", DateTimeOffset.UtcNow, ("a1", "fake.pdf", "application/octet-stream", new byte[] { 1, 2, 3, 4, 5 }));
        var fixture = Create(source, Preview("Nubank", "Reconciled", null));
        var result = await fixture.Collector.RunAsync("Bills", UserId, false, CancellationToken.None);
        Assert.Equal(1, result!.Failures); Assert.Contains(result.Errors, error => error.Code == "InvalidPdfContent"); Assert.Empty(fixture.Inbox.Items);
    }

    private static Fixture Create(FakeEmailSource source, CreditCardPdfPreview? preview, CreditCardPdfReadFailure? failure = null)
    {
        var inboxRepository = new FakeInboxRepository(); var storage = new FakeStorage();
        var extractor = new FakeExtractor(preview, failure); var resolver = new CreditCardStatementResolver(new EmptyResolutionRepository());
        var inbox = new FinancialDocumentInboxService(inboxRepository, storage, extractor, new UnusedPreview(), resolver, TimeProvider.System);
        var options = new FinancialEmailImportOptions { Enabled = true, LookbackDays = 7, MaxMessages = 20,
            Sources = [new() { Name = "Bills", FamilyId = FamilyId, Query = "has:attachment filename:pdf", Enabled = true }] };
        var checkpoints = new FakeCheckpoints();
        return new(new(options, source, checkpoints, inboxRepository, inbox, TimeProvider.System), inboxRepository);
    }
    private static CreditCardPdfPreview Preview(string parser, string status, string? digits) => new("x.pdf", 1, parser,
        new("Nubank", digits, "2026-10", null, null, "1.00"), new(0, 0, 0, 0), [], []) { Reconciliation = new(status, "1.00", "1.00", "0.00", false) };
    private static byte[] Pdf(byte marker) => [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', marker];
    private sealed record Fixture(FinancialEmailCollector Collector, FakeInboxRepository Inbox);

    private sealed class FakeEmailSource : IFinancialEmailSource
    {
        private readonly List<FinancialEmailMessage> messages = []; private readonly Dictionary<string, List<(FinancialEmailAttachment Meta, byte[] Bytes)>> attachments = [];
        public string? FailAttachmentId { get; set; } public int Downloads { get; private set; } public int Mutations => 0;
        public void Add(string id, DateTimeOffset at, params (string Id, string Name, string Type, byte[] Bytes)[] values) { messages.Add(new(id, at)); attachments[id] = values.Select(v => (new FinancialEmailAttachment(v.Id, v.Name, v.Type, v.Bytes.Length), v.Bytes)).ToList(); }
        public Task<IReadOnlyList<FinancialEmailMessage>> SearchAsync(string query, DateTimeOffset after, int maxMessages, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialEmailMessage>>(messages.Take(maxMessages).ToList());
        public Task<IReadOnlyList<FinancialEmailAttachment>> ListAttachmentsAsync(string messageId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialEmailAttachment>>(attachments[messageId].Select(item => item.Meta).ToList());
        public Task<Stream> OpenAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken) { Downloads++; if (attachmentId == FailAttachmentId) throw new IOException("synthetic"); return Task.FromResult<Stream>(new MemoryStream(attachments[messageId].Single(item => item.Meta.Id == attachmentId).Bytes)); }
    }
    private sealed class FakeInboxRepository : IFinancialDocumentInboxRepository
    {
        public List<FinancialDocumentInboxItem> Items { get; } = [];
        public Task<bool> CanAccessFamilyAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) => Task.FromResult(familyId == FamilyId && userId == UserId);
        public Task<bool> FamilyExistsAsync(Guid familyId, CancellationToken cancellationToken) => Task.FromResult(familyId == FamilyId);
        public Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid familyId, string hash, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.FamilyId == familyId && item.ContentHash == hash));
        public Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid familyId, FinancialDocumentSource source, string externalId, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.FamilyId == familyId && item.Source == source && item.ExternalSourceId == externalId));
        public Task<FinancialDocumentInboxItem?> GetAsync(Guid familyId, Guid id, Guid userId, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.Id == id));
        public Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialDocumentInboxItem>>(Items);
        public Task<bool> TryAddAsync(FinancialDocumentInboxItem item, CancellationToken cancellationToken) { Items.Add(item); return Task.FromResult(true); }
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class FakeStorage : IFinancialDocumentStorage
    {
        private readonly Dictionary<string, byte[]> values = [];
        public async Task<string> SaveAsync(Guid familyId, Guid documentId, Stream content, CancellationToken cancellationToken) { using var memory = new MemoryStream(); await content.CopyToAsync(memory, cancellationToken); var key = documentId.ToString("N"); values[key] = memory.ToArray(); return key; }
        public Task<Stream?> OpenReadAsync(string reference, CancellationToken cancellationToken) => Task.FromResult<Stream?>(new MemoryStream(values[reference]));
        public Task DeleteAsync(string reference, CancellationToken cancellationToken) { values.Remove(reference); return Task.CompletedTask; }
    }
    private sealed class FakeExtractor(CreditCardPdfPreview? preview, CreditCardPdfReadFailure? failure) : ICreditCardPdfTextExtractor
    {
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken) => failure is { } value ? throw new CreditCardPdfReadException(value) : Task.FromResult(new CreditCardPdfExtractedDocument([new(1, "text")]));
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat) => preview!;
    }
    private sealed class EmptyResolutionRepository : ICreditCardStatementResolutionRepository { public Task<IReadOnlyList<CreditCardStatementCandidate>> ListActiveAsync(Guid familyId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CreditCardStatementCandidate>>([]); }
    private sealed class UnusedPreview : ICreditCardPdfPreviewService { public Task<CreditCardPdfPreviewResult> PreviewAsync(Guid familyId, Guid cardId, Guid userId, Stream pdf, long length, string fileName, string? issuerProfile, string? competence, string? dateFormat, string? password, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class FakeCheckpoints : IFinancialEmailCollectorCheckpointRepository
    {
        private FinancialEmailCollectorCheckpoint? value;
        public Task<FinancialEmailCollectorCheckpoint> GetOrCreateAsync(Guid familyId, string profileName, CancellationToken cancellationToken) => Task.FromResult(value ??= FinancialEmailCollectorCheckpoint.Create(familyId, profileName));
        public Task<IReadOnlyList<FinancialEmailCollectorCheckpoint>> ListAsync(IReadOnlyCollection<Guid> familyIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialEmailCollectorCheckpoint>>(value is null ? [] : [value]);
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

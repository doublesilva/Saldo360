using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class FinancialEmailCollectorTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task HistoricalIncludesLocalMidnightAndLastInstantButExcludesAdjacentDays()
    {
        var window = FinancialEmailHistoricalWindow.Create("2026-10-01", "2026-10-31");
        var source = new FakeEmailSource();
        source.Add("start", window.StartUtc, ("a", "a.pdf", "application/pdf", Pdf(1)));
        source.Add("last", window.EndExclusiveUtc.AddTicks(-1), ("b", "b.pdf", "application/pdf", Pdf(2)));
        source.Add("too-early", window.StartUtc.AddTicks(-1), ("c", "c.pdf", "application/pdf", Pdf(3)));
        source.Add("too-late", window.EndExclusiveUtc, ("d", "d.pdf", "application/pdf", Pdf(4)));
        var fixture = Create(source, Preview("Generic", "NeedsReview", null)); fixture.Checkpoints.ForbidAccess = true;
        var result = await fixture.Collector.RunHistoricalAsync("Bills", UserId, true, window, default);
        Assert.Equal(2, result!.MessagesFound); Assert.Equal(2, result.CandidatesFound); Assert.Equal(0, source.Downloads);
        Assert.False(result.MessageLimitReached); Assert.Empty(fixture.Inbox.Items);
        Assert.Null(await fixture.Collector.RunHistoricalAsync("Bills", Guid.NewGuid(), true, window, default));
    }

    [Fact]
    public async Task HistoricalSignalsLimitedCoverageWithoutChangingCheckpoint()
    {
        var source = new FakeEmailSource();
        foreach (var id in new[] { "one", "two", "three" }) source.Add(id, new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var fixture = Create(source, Preview("Generic", "NeedsReview", null)); fixture.Checkpoints.ForbidAccess = true;
        var result = await fixture.Collector.RunHistoricalAsync("Bills", UserId, true, FinancialEmailHistoricalWindow.Create("2026-10-01", "2026-10-31", 2), default);
        Assert.Equal(2, result!.MessagesFound); Assert.True(result.MessageLimitReached); Assert.Equal(2, result.MessageLimit);
        Assert.Equal(0, fixture.Checkpoints.Saves);
    }

    [Fact]
    public async Task HistoricalFindsEarlierMessagesAndNeverTouchesCheckpointIncludingRepeatedRealRuns()
    {
        var source = new FakeEmailSource();
        source.Add("october", new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), ("private-download-id", "bill.pdf", "application/pdf", Pdf(8)));
        source.Add("before", new(2026, 10, 1, 2, 59, 59, TimeSpan.Zero), ("outside-a", "outside.pdf", "application/pdf", Pdf(1)));
        source.Add("after", new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero), ("outside-b", "outside.pdf", "application/pdf", Pdf(2)));
        var fixture = Create(source, Preview("Generic", "NeedsReview", null), schedulerEnabled: false);
        var checkpoint = await fixture.Checkpoints.GetOrCreateAsync(WorkspaceId, "Bills", default);
        checkpoint.Complete(new(2026, 12, 20, 12, 0, 0, TimeSpan.Zero), new(2026, 12, 20, 12, 0, 0, TimeSpan.Zero), "Success", 7, 1, 1, 1, 0, 0, 0);
        var snapshot = System.Text.Json.JsonSerializer.Serialize(checkpoint);
        fixture.Checkpoints.ForbidAccess = true;
        var window = FinancialEmailHistoricalWindow.Create("2026-10-01", "2026-10-31");
        var dry = await fixture.Collector.RunHistoricalAsync("Bills", UserId, true, window, default);
        Assert.Equal(1, dry!.MessagesFound); Assert.Equal(1, dry.CandidatesFound); Assert.Equal(0, source.Downloads); Assert.Empty(fixture.Inbox.Items);
        Assert.Equal(window.StartUtc.AddSeconds(-1), source.LastAfter); Assert.Contains("before:" + window.EndExclusiveUtc.ToUnixTimeSeconds(), source.LastQuery);
        Assert.Equal("Historical", dry.Mode); Assert.Equal("2026-10-01", dry.WindowFrom); Assert.Equal("2026-10-31", dry.WindowThrough);
        var first = await fixture.Collector.RunHistoricalAsync("Bills", UserId, false, window, default);
        var second = await fixture.Collector.RunHistoricalAsync("Bills", UserId, false, window, default);
        Assert.Equal(1, first!.AcceptedDocuments); Assert.Equal(1, second!.Duplicates); Assert.Equal(0, second.AcceptedDocuments);
        var existing = await fixture.Collector.RunHistoricalAsync("Bills", UserId, true, window, default);
        Assert.Equal(1, existing!.ExistingIdentityCandidates); Assert.Equal(1, source.Downloads); Assert.Single(fixture.Inbox.Items);
        Assert.Equal(snapshot, System.Text.Json.JsonSerializer.Serialize(checkpoint)); Assert.Equal(0, fixture.Checkpoints.Saves);
        Assert.DoesNotContain("private-download-id", System.Text.Json.JsonSerializer.Serialize(dry));
    }

    [Fact]
    public async Task HistoricalLinksReuseDownloaderAndHashDedupeWithoutCheckpoint()
    {
        var source = new FakeEmailSource();
        source.Add("m1", new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        source.Add("m2", new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        foreach (var id in new[] { "m1", "m2" }) source.SetBody(id, "<a href=\"https://dmae.example/contas/bill.pdf?secret=not-returned\">PDF</a>");
        var fixture = Create(source, Preview("Generic", "NeedsReview", null), mode: FinancialEmailCollectionMode.Links);
        fixture.Checkpoints.ForbidAccess = true;
        var window = FinancialEmailHistoricalWindow.Create("2026-10-01", "2026-10-31");
        var dry = await fixture.Collector.RunHistoricalAsync("Bills", UserId, true, window, default);
        Assert.Equal(2, dry!.LinkCandidates); Assert.Equal(0, fixture.Downloader.Downloads);
        var run = await fixture.Collector.RunHistoricalAsync("Bills", UserId, false, window, default);
        Assert.Equal(1, run!.AcceptedDocuments); Assert.Equal(1, run.Duplicates); Assert.Equal(2, fixture.Downloader.Downloads);
        Assert.Single(fixture.Inbox.Items); Assert.Equal(0, fixture.Checkpoints.Saves);
        Assert.DoesNotContain("not-returned", System.Text.Json.JsonSerializer.Serialize(dry));
    }

    [Fact]
    public async Task IncrementalStillUsesAndUpdatesCheckpointAndHistoricalFailureDoesNotLeakAttachmentId()
    {
        var source = new FakeEmailSource(); source.Add("m", new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), ("sensitive-attachment", "bill.pdf", "application/pdf", Pdf(1)));
        var fixture = Create(source, Preview("Generic", "NeedsReview", null));
        var checkpoint = await fixture.Checkpoints.GetOrCreateAsync(WorkspaceId, "Bills", default);
        var prior = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        checkpoint.Complete(prior, prior, "Success", 0, 0, 0, 0, 0, 0, 0);
        await fixture.Collector.RunAsync("Bills", UserId, false, default);
        Assert.Equal(prior.AddMinutes(-5), source.LastAfter); Assert.Equal("has:attachment filename:pdf", source.LastQuery);
        Assert.Equal(2, fixture.Checkpoints.Saves); Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), checkpoint.LastMessageAt);
        var failing = Create(source, Preview("Generic", "NeedsReview", null)); failing.Checkpoints.ForbidAccess = true;
        source.FailAttachmentId = "sensitive-attachment";
        var result = await failing.Collector.RunHistoricalAsync("Bills", UserId, false, FinancialEmailHistoricalWindow.Create("2026-10-01", "2026-10-31"), default);
        Assert.Equal(1, result!.Failures); Assert.DoesNotContain("sensitive-attachment", System.Text.Json.JsonSerializer.Serialize(result));
    }

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
        Assert.All(fixture.Inbox.Items, item => { Assert.Contains(":part:", item.ExternalSourceId, StringComparison.Ordinal); Assert.DoesNotContain(":a1", item.ExternalSourceId, StringComparison.Ordinal); Assert.DoesNotContain(":a3", item.ExternalSourceId, StringComparison.Ordinal); });
        Assert.Equal(2, source.Downloads);
        Assert.Equal(["a1", "a3"], source.OpenedAttachmentIds);
        Assert.Equal(0, source.Mutations);
    }

    [Fact]
    public async Task DryRunListsStableExternalIdsWithoutDownloadingOrPersisting()
    {
        var source = new FakeEmailSource(); source.Add("message", DateTimeOffset.UtcNow, ("attachment", "bill.pdf", "application/pdf", Pdf(1)));
        var fixture = Create(source, Preview("Nubank", "Reconciled", null));
        var result = await fixture.Collector.RunAsync("Bills", UserId, true, CancellationToken.None);
        Assert.Equal([GmailAttachmentIdentity.Create("message", "part-1")], result!.CandidateExternalIds);
        Assert.DoesNotContain("attachment", result.CandidateExternalIds[0], StringComparison.Ordinal);
        Assert.Empty(fixture.Inbox.Items); Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public void GmailAttachmentIdentityIsStableUniqueAndBounded()
    {
        var first = GmailAttachmentIdentity.Create("message", "attachment-a");
        var repeated = GmailAttachmentIdentity.Create("message", "attachment-a");
        var different = GmailAttachmentIdentity.Create("message", "attachment-b");

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, different);
        Assert.Equal("gmail:message:part:attachment-a", first);
        Assert.True(first.Length < 300);
        Assert.Throws<InvalidOperationException>(() => GmailAttachmentIdentity.Create("message", new string('x', 300)));
    }

    [Fact]
    public async Task DryRunIdentityDoesNotChangeWhenGmailRotatesAttachmentIdForSameMimePart()
    {
        var source = new FakeEmailSource(); source.Add("message", DateTimeOffset.UtcNow, ("attachment-v1", "bill.pdf", "application/pdf", Pdf(1)));
        var fixture = Create(source, Preview("Nubank", "Reconciled", null));

        var first = await fixture.Collector.RunAsync("Bills", UserId, true, CancellationToken.None);
        source.ChangeAttachmentId("message", "attachment-v2");
        var repeated = await fixture.Collector.RunAsync("Bills", UserId, true, CancellationToken.None);

        Assert.Equal(first!.CandidateExternalIds, repeated!.CandidateExternalIds);
        Assert.Equal(["gmail:message:part:part-1"], first.CandidateExternalIds);
        Assert.DoesNotContain("attachment-v2", repeated.CandidateExternalIds[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualDryRunWorksWhileSchedulerIsDisabled()
    {
        var source = new FakeEmailSource(); source.Add("message", DateTimeOffset.UtcNow, ("attachment", "bill.pdf", "application/pdf", Pdf(1)));
        var fixture = Create(source, Preview("Nubank", "Reconciled", null), schedulerEnabled: false);

        var result = await fixture.Collector.RunAsync("Bills", UserId, true, CancellationToken.None);

        Assert.NotNull(result); Assert.Single(result.CandidateExternalIds); Assert.Equal(0, source.Downloads); Assert.Empty(fixture.Inbox.Items);
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

    [Fact]
    public async Task LinkDryRunIsStableAndDoesNotDownloadOrPersist()
    {
        var source = new FakeEmailSource(); source.Add("message", DateTimeOffset.UtcNow); source.SetBody("message", "<a href=\"https://dmae.example/contas/segunda-via.pdf?token=secret\">PDF</a>");
        var fixture = Create(source, Preview("Generic", "NeedsReview", null), mode: FinancialEmailCollectionMode.Links);

        var first = await fixture.Collector.RunAsync("Bills", UserId, true, CancellationToken.None);
        var repeated = await fixture.Collector.RunAsync("Bills", UserId, true, CancellationToken.None);

        Assert.Equal(["gmail:message:link:1"], first!.CandidateExternalIds);
        Assert.Equal(first.CandidateExternalIds, repeated!.CandidateExternalIds);
        Assert.Equal(1, first.CandidateLinks); Assert.Equal(1, first.LinksScanned);
        Assert.Equal(0, fixture.Downloader.Downloads); Assert.Empty(fixture.Inbox.Items);
    }

    private static Fixture Create(FakeEmailSource source, CreditCardPdfPreview? preview, CreditCardPdfReadFailure? failure = null, bool schedulerEnabled = true, FinancialEmailCollectionMode mode = FinancialEmailCollectionMode.Attachments)
    {
        var inboxRepository = new FakeInboxRepository(); var storage = new FakeStorage();
        var extractor = new FakeExtractor(preview, failure); var resolver = new CreditCardStatementResolver(new EmptyResolutionRepository());
        var inbox = new FinancialDocumentInboxService(inboxRepository, storage, extractor, new UnusedPreview(), resolver,
            new FinancialDocumentClassificationOptions(), new FakeDocumentClassifier(), new FinancialDocumentAnalysisRouter(), TimeProvider.System);
        var options = new FinancialEmailImportOptions { Enabled = schedulerEnabled, LookbackDays = 7, MaxMessages = 20,
            Sources = [new() { Name = "Bills", WorkspaceId = WorkspaceId, Query = "has:attachment filename:pdf", Enabled = true, CollectionMode = mode, AllowedLinkHosts = ["dmae.example"], AllowedLinkPathPrefixes = ["/contas/"] }] };
        var checkpoints = new FakeCheckpoints();
        var downloader = new FakeLinkedDownloader();
        return new(new(options, source, new FinancialEmailLinkExtractor(), downloader, checkpoints, inboxRepository, inbox, TimeProvider.System), inboxRepository, downloader, checkpoints);
    }
    private static CreditCardPdfPreview Preview(string parser, string status, string? digits) => new("x.pdf", 1, parser,
        new("Nubank", digits, "2026-10", null, null, "1.00"), new(0, 0, 0, 0), [], []) { Reconciliation = new(status, "1.00", "1.00", "0.00", false) };
    private static byte[] Pdf(byte marker) => [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', marker];
    private sealed record Fixture(FinancialEmailCollector Collector, FakeInboxRepository Inbox, FakeLinkedDownloader Downloader, FakeCheckpoints Checkpoints);

    private sealed class FakeEmailSource : IFinancialEmailSource
    {
        public string? LastQuery { get; private set; }
        public DateTimeOffset? LastAfter { get; private set; }
        private readonly List<FinancialEmailMessage> messages = []; private readonly Dictionary<string, List<(FinancialEmailAttachment Meta, byte[] Bytes)>> attachments = []; private readonly Dictionary<string, FinancialEmailBody> bodies = [];
        public string? FailAttachmentId { get; set; } public int Downloads { get; private set; } public List<string> OpenedAttachmentIds { get; } = []; public int Mutations => 0;
        public void Add(string id, DateTimeOffset at, params (string Id, string Name, string Type, byte[] Bytes)[] values) { messages.Add(new(id, at)); attachments[id] = values.Select((v, index) => (new FinancialEmailAttachment(v.Id, $"part-{index + 1}", v.Name, v.Type, v.Bytes.Length), v.Bytes)).ToList(); }
        public void ChangeAttachmentId(string messageId, string attachmentId)
        {
            attachments[messageId] = attachments[messageId].Select(item => (item.Meta with { Id = attachmentId }, item.Bytes)).ToList();
        }
        public void SetBody(string messageId, string html) => bodies[messageId] = new(html, null);
        public Task<IReadOnlyList<FinancialEmailMessage>> SearchAsync(string query, DateTimeOffset after, int maxMessages, CancellationToken cancellationToken)
        { LastQuery = query; LastAfter = after; return Task.FromResult<IReadOnlyList<FinancialEmailMessage>>(messages.Take(maxMessages).ToList()); }
        public Task<IReadOnlyList<FinancialEmailAttachment>> ListAttachmentsAsync(string messageId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialEmailAttachment>>(attachments[messageId].Select(item => item.Meta).ToList());
        public Task<Stream> OpenAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken) { Downloads++; OpenedAttachmentIds.Add(attachmentId); if (attachmentId == FailAttachmentId) throw new IOException("synthetic"); return Task.FromResult<Stream>(new MemoryStream(attachments[messageId].Single(item => item.Meta.Id == attachmentId).Bytes)); }
        public Task<FinancialEmailBody> GetBodyAsync(string messageId, CancellationToken cancellationToken) => Task.FromResult(bodies.GetValueOrDefault(messageId) ?? new FinancialEmailBody(null, null));
    }
    private sealed class FakeLinkedDownloader : IFinancialLinkedDocumentDownloader
    {
        public int Downloads { get; private set; }
        public Task<FinancialLinkedDocument> DownloadAsync(FinancialEmailImportProfile profile, Uri url, CancellationToken cancellationToken) =>
            Task.FromResult(Download());
        private FinancialLinkedDocument Download() { Downloads++; return new(Pdf(9), "linked.pdf", "application/pdf"); }
    }
    private sealed class FakeInboxRepository : IFinancialDocumentInboxRepository
    {
        public List<FinancialDocumentInboxItem> Items { get; } = [];
        public Task<bool> CanAccessWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) => Task.FromResult(workspaceId == WorkspaceId && userId == UserId);
        public Task<bool> WorkspaceExistsAsync(Guid workspaceId, CancellationToken cancellationToken) => Task.FromResult(workspaceId == WorkspaceId);
        public Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid workspaceId, string hash, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.WorkspaceId == workspaceId && item.ContentHash == hash));
        public Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid workspaceId, FinancialDocumentSource source, string externalId, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.WorkspaceId == workspaceId && item.Source == source && item.ExternalSourceId == externalId));
        public Task<FinancialDocumentInboxItem?> GetAsync(Guid workspaceId, Guid id, Guid userId, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.Id == id));
        public Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialDocumentInboxItem>>(Items);
        public Task<bool> TryAddAsync(FinancialDocumentInboxItem item, CancellationToken cancellationToken) { Items.Add(item); return Task.FromResult(true); }
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class FakeStorage : IFinancialDocumentStorage
    {
        private readonly Dictionary<string, byte[]> values = [];
        public async Task<string> SaveAsync(Guid workspaceId, Guid documentId, Stream content, CancellationToken cancellationToken) { using var memory = new MemoryStream(); await content.CopyToAsync(memory, cancellationToken); var key = documentId.ToString("N"); values[key] = memory.ToArray(); return key; }
        public Task<Stream?> OpenReadAsync(string reference, CancellationToken cancellationToken) => Task.FromResult<Stream?>(new MemoryStream(values[reference]));
        public Task DeleteAsync(string reference, CancellationToken cancellationToken) { values.Remove(reference); return Task.CompletedTask; }
    }
    private sealed class FakeExtractor(CreditCardPdfPreview? preview, CreditCardPdfReadFailure? failure) : ICreditCardPdfTextExtractor
    {
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken) => failure is { } value ? throw new CreditCardPdfReadException(value) : Task.FromResult(new CreditCardPdfExtractedDocument([new(1, "text")]));
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat) => preview!;
    }
    private sealed class EmptyResolutionRepository : ICreditCardStatementResolutionRepository { public Task<IReadOnlyList<CreditCardStatementCandidate>> ListActiveAsync(Guid workspaceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CreditCardStatementCandidate>>([]); }
    private sealed class FakeDocumentClassifier : IFinancialDocumentClassifier { public Task<FinancialDocumentClassificationResult> ClassifyAsync(FinancialDocumentClassificationRequest request, CancellationToken cancellationToken) => Task.FromResult(new FinancialDocumentClassificationResult("Unknown", "Low", [], "Unknown")); }
    private sealed class UnusedPreview : ICreditCardPdfPreviewService { public Task<CreditCardPdfPreviewResult> PreviewAsync(Guid workspaceId, Guid cardId, Guid userId, Stream pdf, long length, string fileName, string? issuerProfile, string? competence, string? dateFormat, string? password, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class FakeCheckpoints : IFinancialEmailCollectorCheckpointRepository
    {
        public bool ForbidAccess { get; set; }
        public int Saves { get; private set; }
        private FinancialEmailCollectorCheckpoint? value;
        public Task<FinancialEmailCollectorCheckpoint> GetOrCreateAsync(Guid workspaceId, string profileName, CancellationToken cancellationToken)
        { if (ForbidAccess) throw new InvalidOperationException("Checkpoint forbidden"); return Task.FromResult(value ??= FinancialEmailCollectorCheckpoint.Create(workspaceId, profileName)); }
        public Task<IReadOnlyList<FinancialEmailCollectorCheckpoint>> ListAsync(IReadOnlyCollection<Guid> workspaceIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialEmailCollectorCheckpoint>>(value is null ? [] : [value]);
        public Task SaveAsync(CancellationToken cancellationToken) { if (ForbidAccess) throw new InvalidOperationException("Checkpoint forbidden"); Saves++; return Task.CompletedTask; }
    }
}

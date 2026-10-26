using Saldo360.Application.CreditCards;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;
using Microsoft.Extensions.Logging;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Application;

[Trait("Area", "Documents")]
[Trait("Profile", "FAST")]
public sealed class CardLatestEmailServiceTests
{
    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("missing-query", "QueryMissing")]
    [InlineData("missing-card", "NotFound")]
    [InlineData("archived", "NotFound")]
    [InlineData("not-owner", "NotFound")]
    [InlineData("other-workspace", "NotFound")]
    [InlineData("anonymous", "NotFound")]
    public async Task RejectsBeforeMailboxAccess(string scenario, string expected)
    {
        var f = new Fixture();
        if (scenario == "missing-query") f.Card = f.Card! with { GmailQuery = null };
        if (scenario == "missing-card") f.Card = null;
        if (scenario == "archived") f.Card = f.Card! with { IsArchived = true };
        f.Owner = scenario != "not-owner";
        var result = await f.Service.SearchAsync(scenario == "other-workspace" ? Guid.NewGuid() : Fixture.Workspace,
            Fixture.CardId, scenario == "anonymous" ? Guid.Empty : Fixture.User, default);
        Assert.Equal(expected, result.Status); Assert.Equal(0, f.Searches); Assert.Empty(f.Received);
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewestOrCorrectedCopyWinsEvenWhenAlreadyReceived(bool existing)
    {
        var f = new Fixture { Existing = existing };
        f.Messages = [new("old", Fixture.Now.AddMonths(-1)), new("corrected", Fixture.Now), new("previous", Fixture.Now.AddDays(-1))];
        var result = await f.Run();
        Assert.Equal(existing ? "AlreadyReceived" : "Found", result.Status);
        Assert.Equal(existing, result.AlreadyExisted); Assert.NotNull(result.DocumentId);
        Assert.Equal("gmail:corrected:part:1", Assert.Single(f.Received));
        Assert.Equal("corrected", Assert.Single(f.Downloads));
        Assert.Equal("configured-query", f.Query); Assert.Equal(CardLatestEmailService.MessageLimit, f.Limit);
        Assert.DoesNotContain("gmail:", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("provider", "Unavailable", "GmailSearchNetwork")]
    [InlineData("download", "Unavailable", "EmailPdfDownloadIo")]
    [InlineData("limit", "Error", "SearchLimitReached")]
    [InlineData("busy", "Busy", "AlreadyRunning")]
    public async Task FailuresAreSanitizedAndDoNotReceiveOlderMail(string failure, string status, string code)
    {
        var f = new Fixture { Failure = failure };
        var result = await f.Run();
        Assert.Equal(status, result.Status); Assert.Equal(code, result.FailureCode); Assert.Empty(f.Received);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("empty")]
    [InlineData("no-pdf")]
    [InlineData("invalid-signature")]
    [InlineData("oversized")]
    [InlineData("wrong-card")]
    [InlineData("corrupt")]
    public async Task NoValidPdfDoesNotWrite(string scenario)
    {
        var f = new Fixture { Failure = scenario };
        if (scenario == "empty") f.Messages = [];
        Assert.Equal("NoMatch", (await f.Run()).Status); Assert.Empty(f.Received);
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("unknown")]
    [InlineData("password")]
    [InlineData("encryption")]
    [InlineData("text")]
    public async Task InsufficientEvidenceStaysUnassociatedInInbox(string scenario)
    {
        var f = new Fixture { Failure = scenario };
        var result = await f.Run();
        Assert.Equal("Found", result.Status); Assert.True(f.Review); Assert.Single(f.Received); Assert.Equal(1, f.Processed);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task AmbiguousAttachmentsNeverPickArbitrarily()
    {
        var f = new Fixture { Failure = "ambiguous" };
        Assert.Equal("NeedsReview", (await f.Run()).Status); Assert.Empty(f.Received);
    }

    [Trait("Category", "REGRESSION")]
    [Fact]
    public async Task ExistingNewestDocumentDoesNotFallBackWhenParserNoLongerRecognizesIt()
    {
        var f = new Fixture { Existing = true, Known = true, Failure = "corrupt" };
        f.Messages = [new("old", Fixture.Now.AddMonths(-1)), new("latest", Fixture.Now)];
        var result = await f.Run();
        Assert.True(result.AlreadyExisted); Assert.Equal("AlreadyReceived", result.Status);
        Assert.Equal("latest", Assert.Single(f.Downloads)); Assert.Single(f.Received);
    }

    [Trait("Category", "REGRESSION")]
    [Theory]
    [InlineData("search", "GmailSearch", "Network")]
    [InlineData("attachments", "GmailAttachments", "Failed")]
    [InlineData("download", "PdfDownload", "Io")]
    [InlineData("lookup", "InboxLookup", "Failed")]
    [InlineData("preview", "DocumentValidation", "Failed")]
    [InlineData("receive", "InboxReception", "Failed")]
    [InlineData("process", "DocumentProcessing", "Failed")]
    [InlineData("lock", "OperationLock", "Failed")]
    public async Task TechnicalFailuresExposeOnlySafeStructuredStageAndNeverTryOlderMail(string operation, string stage, string category)
    {
        const string sensitive = "private-path token header gmail-query financial-content";
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var f = new Fixture { ErrorAt = operation, Error = operation == "search" ? new HttpRequestException(sensitive)
            : operation == "download" ? new IOException(sensitive) : new InvalidOperationException(sensitive),
            Logger = loggerFactory.CreateLogger<CardLatestEmailService>() };
        f.Messages = [new("old", Fixture.Now.AddDays(-1)), new("latest", Fixture.Now)];
        var result = await f.Run();
        Assert.Equal("Unavailable", result.Status); Assert.Equal(stage, result.FailureStage); Assert.Equal(category, result.FailureCategory);
        Assert.DoesNotContain("old", f.Downloads);
        if (operation == "process") { Assert.Single(f.Received); Assert.NotNull(result.DocumentId); }
        else Assert.Empty(f.Received);
        var entry = Assert.Single(logs.StructuredEntries);
        Assert.Equal(LogLevel.Warning, entry.Level); Assert.Null(entry.Exception);
        Assert.Equal(16051, entry.EventId.Id);
        Assert.Equal(new[] { "Category", "FailureCode", "Stage" }, entry.Properties.Keys.Order());
        Assert.Equal(stage, entry.Properties["Stage"]); Assert.Equal(category, entry.Properties["Category"]);
        Assert.Equal(result.FailureCode, entry.Properties["FailureCode"]);
        Assert.DoesNotContain(sensitive, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.DoesNotContain(sensitive, System.Text.Json.JsonSerializer.Serialize(logs.StructuredEntries));
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderTimeoutIsDiagnosedButCallerCancellationPropagates(bool callerCancelled)
    {
        using var cts = new CancellationTokenSource();
        if (callerCancelled) cts.Cancel();
        var f = new Fixture { ErrorAt = "search", Error = new TaskCanceledException("private", null, cts.Token) };
        if (callerCancelled) await Assert.ThrowsAsync<TaskCanceledException>(() => f.Service.SearchAsync(Fixture.Workspace, Fixture.CardId, Fixture.User, cts.Token));
        else
        {
            var result = await f.Run();
            Assert.Equal("GmailSearchTimeout", result.FailureCode); Assert.Equal("Unavailable", result.Status);
        }
        Assert.Empty(f.Received);
    }

    [Trait("Category", "REGRESSION")]
    [Theory]
    [InlineData("Unavailable", "NeedsReview", "Failed")]
    [InlineData("NeedsReview", "Error", "Failed")]
    [InlineData("Rejected", "NeedsReview", "Rejected")]
    [InlineData("Conflict", "NeedsReview", "Conflict")]
    [InlineData("Busy", "Busy", "Busy")]
    [InlineData("Unrecognized", "Imported", "Failed")]
    public async Task FailedProcessingResultsCannotBeReportedAsFound(string analysis, string consolidation, string category)
    {
        var f = new Fixture { ReturnedProcessing = new(new(analysis), new(consolidation)) };
        var result = await f.Run();
        Assert.Equal(category == "Busy" ? "Busy" : "Error", result.Status);
        Assert.Equal("EmailDocumentProcessing" + category, result.FailureCode);
        Assert.Equal("DocumentProcessing", result.FailureStage); Assert.NotNull(result.DocumentId);
        Assert.Single(f.Received); Assert.Equal(1, f.Processed); Assert.Null(result.Processing);
    }

    [Trait("Category", "REGRESSION")]
    [Fact]
    public async Task SwallowedAgentFailureIsNotAReceivedSuccessAndCannotLeakArbitraryFailureText()
    {
        var f = new Fixture { ReturnedProcessing = new(new("NeedsReview"), new("NeedsReview", FailureCode: "sensitive-provider-text")) };
        var result = await f.Run();
        Assert.Equal("Error", result.Status); Assert.Equal("EmailDocumentProcessingFailed", result.FailureCode);
        Assert.DoesNotContain("sensitive-provider-text", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task PasswordRejectionRemainsRecoverableWithoutReportingProcessingSuccess()
    {
        var analysis = new InboxStatementAnalysis("2.0", Fixture.CardId, "synthetic-hash", "Rejected", null, [], new Dictionary<string, Guid>(), "PasswordRequired");
        var f = new Fixture { ReturnedProcessing = new(new("Rejected", analysis), new("NeedsReview", FailureCode: "PasswordRequired")) };
        var result = await f.Run();
        Assert.Equal("NeedsReview", result.Status); Assert.Equal("EmailDocumentProcessingRejected", result.FailureCode);
        Assert.Equal("PasswordRequired", result.Processing?.Consolidation.FailureCode);
        Assert.NotNull(result.DocumentId);
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData(EmailFailureStage.Credentials, EmailFailureCategory.Missing, "GmailCredentialsMissing")]
    [InlineData(EmailFailureStage.OAuthRefresh, EmailFailureCategory.Forbidden, "GmailOAuthForbidden")]
    public async Task ProviderDiagnosticsPropagateInsteadOfBecomingGenericSearchFailures(EmailFailureStage stage, EmailFailureCategory category, string code)
    {
        var f = new Fixture { ErrorAt = "search", Error = new EmailSearchFailureException(stage, category) };
        var result = await f.Run();
        Assert.Equal(code, result.FailureCode); Assert.Equal(stage.ToString(), result.FailureStage); Assert.Equal(category.ToString(), result.FailureCategory);
        Assert.Empty(f.Downloads); Assert.Empty(f.Received);
    }

    [Trait("Category", "REGRESSION")]
    [Theory]
    [InlineData("Conflict", "EmailInboxReceptionConflict")]
    [InlineData("Unavailable", "EmailInboxReceptionRejected")]
    public async Task RejectedInboxReceiptDoesNotStartProcessing(string reception, string code)
    {
        var f = new Fixture { ReceptionResult = reception };
        var result = await f.Run();
        Assert.Equal("Error", result.Status); Assert.Equal(code, result.FailureCode); Assert.Equal("InboxReception", result.FailureStage);
        Assert.Equal(0, f.Processed); Assert.Null(result.DocumentId);
    }

    [Trait("Category", "REGRESSION")]
    [Fact]
    public async Task RepositoryConfirmedReplayIsNotMisreportedAsAnalysisConflict()
    {
        var f = new Fixture { Existing = true, ReturnedProcessing = new(new("Conflict"), new("AlreadyImported")) };
        var result = await f.Run();
        Assert.Equal("AlreadyReceived", result.Status); Assert.True(result.AlreadyExisted);
        Assert.Equal("AlreadyImported", result.Processing?.Consolidation.Status);
        Assert.Null(result.FailureStage); Assert.Null(result.FailureCategory);
        Assert.Single(f.Received);
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData("NewVersionConsolidated")]
    [InlineData("InvoicesLinked")]
    public async Task ExistingSuccessfulConsolidationOutcomesAreNotMisreportedAsProcessingFailures(string status)
    {
        var f = new Fixture { ReturnedProcessing = new(new("Reliable"), new(status)) };
        var result = await f.Run();
        Assert.Equal("Found", result.Status); Assert.Equal("Imported", result.InboxStatus);
        Assert.Equal(status, result.Processing?.Consolidation.Status); Assert.Null(result.FailureCode);
        Assert.Null(result.FailureStage); Assert.Null(result.FailureCategory);
    }

    // All unintended calls (including writes to the card) fail: the service has no checkpoint or financial writer.
    private sealed class Fixture : IFinancialWorkspaceRepository, IFinancialCreditCardRepository, IFinancialEmailSource,
        ICreditCardPdfPreviewService, ICardEmailDocumentReceiver, IFinancialEmailCollectorLock, IInboxStatementProcessingService
    {
        public static readonly Guid Workspace = CardEmailSearchAuthorization.AuthorizedPersonalId;
        public static readonly Guid User = Guid.NewGuid(), CardId = Guid.NewGuid();
        public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
        public FinancialCreditCardDto? Card = new(CardId, Workspace, "Synthetic", "Bank", "1234", User, "100.00", 5, 10, null, false, Now, Now, null, "configured-query");
        public bool Owner = true, Existing, Review, Known;
        public string? Failure, Query; public int Searches, Limit;
        public IReadOnlyList<FinancialEmailMessage> Messages = [new("latest", Now)];
        public List<string> Received = [], Downloads = [];
        public int Processed;
        public Exception? Error;
        public string? ErrorAt;
        public InboxStatementProcessingResult? ReturnedProcessing;
        public string? ReceptionResult;
        public ILogger<CardLatestEmailService>? Logger;
        private void Fail(string operation) { if (ErrorAt == operation) throw Error!; }
        public CardLatestEmailService Service => new(new(this), this, this, this, this, this, this, logger: Logger);
        public Task<InboxStatementProcessingResult> ProcessAsync(Guid w, Guid d, Guid c, Guid u, string? password, CancellationToken ct)
        { Processed++; Fail("process"); Assert.Equal(Workspace, w); Assert.Equal(CardId, c); Assert.Equal(User, u); return Task.FromResult(ReturnedProcessing ?? new InboxStatementProcessingResult(new("NeedsReview"), new("NeedsReview", FailureCode: "AnalysisRequired"))); }
        public Task<CardLatestEmailResult> Run() => Service.SearchAsync(Workspace, CardId, User, default);
        public Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid w, Guid u, CancellationToken ct) => Task.FromResult<FinancialWorkspaceRole?>(Owner && w == Workspace && u == User ? FinancialWorkspaceRole.Owner : null);
        public Task<FinancialWorkspace> EnsurePersonalAsync(Guid u, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<FinancialCreditCardDto?> GetAsync(Guid w, Guid c, CancellationToken ct) => Task.FromResult(w == Workspace && c == CardId ? Card : null);
        public Task AddAsync(FinancialCreditCard c, CancellationToken ct) => throw new NotSupportedException();
        public Task<FinancialCreditCard?> FindAsync(Guid w, Guid c, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FinancialCreditCardDto>> ListAsync(Guid w, bool archived, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsHolderOfWorkspaceAsync(Guid w, Guid u, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsActiveAccountOfWorkspaceAsync(Guid w, Guid a, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FinancialEmailMessage>> SearchAsync(string q, DateTimeOffset after, int max, CancellationToken ct) => throw new InvalidOperationException("Must not use checkpoint search.");
        public Task<IReadOnlyList<FinancialEmailMessage>> SearchLatestAsync(string q, int limit, CancellationToken ct)
        {
            Searches++; Query = q; Limit = limit;
            Fail("search");
            if (Failure == "provider") throw new HttpRequestException("secret");
            if (Failure == "limit") throw new LatestEmailSearchLimitException();
            return Task.FromResult(Messages);
        }
        public Task<IReadOnlyList<FinancialEmailAttachment>> ListAttachmentsAsync(string m, CancellationToken ct)
        { Fail("attachments"); return Task.FromResult<IReadOnlyList<FinancialEmailAttachment>>(Failure switch
        {
            "no-pdf" => [new("a", "1", "image.png", "image/png", 10)],
            "oversized" => [new("a", "1", "bill.pdf", "application/pdf", CreditCardPdfPreviewService.MaxFileBytes + 1)],
            "ambiguous" => [new("a", "1", "bill.pdf", "application/pdf", 10), new("b", "2", "other.pdf", "application/pdf", 10)],
            _ => [new("a", "1", "bill.pdf", "application/pdf", 10)]
        }); }
        public Task<Stream> OpenAttachmentAsync(string m, string a, CancellationToken ct)
        {
            Downloads.Add(m); Fail("download"); if (Failure == "download") throw new IOException("secret");
            return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Failure == "invalid-signature" ? "not a pdf" : "%PDF-1.4 synthetic")));
        }
        public Task<FinancialEmailBody> GetBodyAsync(string m, CancellationToken ct) => throw new NotSupportedException();
        public Task<CreditCardPdfPreviewResult> PreviewAsync(Guid w, Guid c, Guid u, Stream pdf, long length, string file, string? issuer, string? competence, string? format, string? password, CancellationToken ct)
        {
            Assert.Null(competence); Assert.Null(password);
            Fail("preview");
            if (Failure is "wrong-card" or "corrupt") return Task.FromResult(new CreditCardPdfPreviewResult(CreditCardPdfPreviewAccess.Invalid, null, null));
            if (Failure == "password") return Task.FromResult(new CreditCardPdfPreviewResult(CreditCardPdfPreviewAccess.Invalid, null, null, "PasswordRequired"));
            if (Failure == "encryption") return Task.FromResult(new CreditCardPdfPreviewResult(CreditCardPdfPreviewAccess.Invalid, null, null, "UnsupportedEncryption"));
            if (Failure == "text") return Task.FromResult(new CreditCardPdfPreviewResult(CreditCardPdfPreviewAccess.Invalid, null, null, "TextUnavailable"));
            var preview = new CreditCardPdfPreview(file, 1, "Synthetic", new("Bank", "1234", null, null, null, null), new(0, 0, 0, 0), [], [])
            { CardResolution = new(Failure == "unknown" ? "InsufficientEvidence" : "Matched", Failure == "unknown" ? null : c) };
            return Task.FromResult(new CreditCardPdfPreviewResult(CreditCardPdfPreviewAccess.Allowed, preview, null));
        }
        public Task<FinancialDocumentInboxReceiveResult> ReceiveEmailPendingAsync(Guid w, Guid u, Stream content, long length, string file, string external, DateTimeOffset date, bool review, CancellationToken ct)
        {
            Fail("receive"); Received.Add(external); Review = review;
            if (ReceptionResult is not null) return Task.FromResult(new FinancialDocumentInboxReceiveResult(ReceptionResult, null));
            return Task.FromResult(new FinancialDocumentInboxReceiveResult(Existing ? "AlreadyExists" : "Created",
                new(Guid.NewGuid(), "Email", external, file, "hash", "application/pdf", date.ToString("O"), "Received", null, null, null, null, date.ToString("O"))));
        }
        public Task<FinancialDocumentInboxDto?> FindKnownEmailAsync(Guid w, Guid u, string external, string hash, CancellationToken ct)
        { Fail("lookup"); return Task.FromResult<FinancialDocumentInboxDto?>(Known ? new(Guid.NewGuid(), "Email", external, "bill.pdf", hash, "application/pdf", Now.ToString("O"), "Received", null, null, CardId, null, Now.ToString("O")) : null); }
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid w, string name, CancellationToken ct)
        { Fail("lock"); Assert.StartsWith("card-latest:", name); return Task.FromResult<IAsyncDisposable?>(Failure == "busy" ? null : new Lease()); }
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}

using Saldo360.Application.CreditCards;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Tests.Application;

public sealed class CardLatestEmailServiceTests
{
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

    [Theory]
    [InlineData("provider", "Unavailable", "EmailSearchUnavailable")]
    [InlineData("download", "Unavailable", "EmailSearchUnavailable")]
    [InlineData("limit", "Error", "SearchLimitReached")]
    [InlineData("busy", "Busy", "AlreadyRunning")]
    public async Task FailuresAreSanitizedAndDoNotReceiveOlderMail(string failure, string status, string code)
    {
        var f = new Fixture { Failure = failure };
        var result = await f.Run();
        Assert.Equal(status, result.Status); Assert.Equal(code, result.FailureCode); Assert.Empty(f.Received);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(result));
    }

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

    [Fact]
    public async Task AmbiguousAttachmentsNeverPickArbitrarily()
    {
        var f = new Fixture { Failure = "ambiguous" };
        Assert.Equal("NeedsReview", (await f.Run()).Status); Assert.Empty(f.Received);
    }

    [Fact]
    public async Task ExistingNewestDocumentDoesNotFallBackWhenParserNoLongerRecognizesIt()
    {
        var f = new Fixture { Existing = true, Known = true, Failure = "corrupt" };
        f.Messages = [new("old", Fixture.Now.AddMonths(-1)), new("latest", Fixture.Now)];
        var result = await f.Run();
        Assert.True(result.AlreadyExisted); Assert.Equal("AlreadyReceived", result.Status);
        Assert.Equal("latest", Assert.Single(f.Downloads)); Assert.Single(f.Received);
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
        public CardLatestEmailService Service => new(new(this), this, this, this, this, this, this);
        public Task<InboxStatementProcessingResult> ProcessAsync(Guid w, Guid d, Guid c, Guid u, string? password, CancellationToken ct)
        { Processed++; Assert.Equal(Workspace, w); Assert.Equal(CardId, c); Assert.Equal(User, u); return Task.FromResult(new InboxStatementProcessingResult(new("NeedsReview"), new("NeedsReview", FailureCode: "AnalysisRequired"))); }
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
            if (Failure == "provider") throw new HttpRequestException("secret");
            if (Failure == "limit") throw new LatestEmailSearchLimitException();
            return Task.FromResult(Messages);
        }
        public Task<IReadOnlyList<FinancialEmailAttachment>> ListAttachmentsAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<FinancialEmailAttachment>>(Failure switch
        {
            "no-pdf" => [new("a", "1", "image.png", "image/png", 10)],
            "oversized" => [new("a", "1", "bill.pdf", "application/pdf", CreditCardPdfPreviewService.MaxFileBytes + 1)],
            "ambiguous" => [new("a", "1", "bill.pdf", "application/pdf", 10), new("b", "2", "other.pdf", "application/pdf", 10)],
            _ => [new("a", "1", "bill.pdf", "application/pdf", 10)]
        });
        public Task<Stream> OpenAttachmentAsync(string m, string a, CancellationToken ct)
        {
            Downloads.Add(m); if (Failure == "download") throw new IOException("secret");
            return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Failure == "invalid-signature" ? "not a pdf" : "%PDF-1.4 synthetic")));
        }
        public Task<FinancialEmailBody> GetBodyAsync(string m, CancellationToken ct) => throw new NotSupportedException();
        public Task<CreditCardPdfPreviewResult> PreviewAsync(Guid w, Guid c, Guid u, Stream pdf, long length, string file, string? issuer, string? competence, string? format, string? password, CancellationToken ct)
        {
            Assert.Null(competence); Assert.Null(password);
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
            Received.Add(external); Review = review;
            return Task.FromResult(new FinancialDocumentInboxReceiveResult(Existing ? "AlreadyExists" : "Created",
                new(Guid.NewGuid(), "Email", external, file, "hash", "application/pdf", date.ToString("O"), "Received", null, null, null, null, date.ToString("O"))));
        }
        public Task<FinancialDocumentInboxDto?> FindKnownEmailAsync(Guid w, Guid u, string external, string hash, CancellationToken ct) =>
            Task.FromResult<FinancialDocumentInboxDto?>(Known ? new(Guid.NewGuid(), "Email", external, "bill.pdf", hash, "application/pdf", Now.ToString("O"), "Received", null, null, CardId, null, Now.ToString("O")) : null);
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid w, string name, CancellationToken ct)
        { Assert.StartsWith("card-latest:", name); return Task.FromResult<IAsyncDisposable?>(Failure == "busy" ? null : new Lease()); }
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}

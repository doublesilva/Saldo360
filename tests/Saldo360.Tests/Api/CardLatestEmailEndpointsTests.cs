using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Tests.Support;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Infrastructure.Workspaces;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
public sealed class CardLatestEmailEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private sealed class PendingProcessing : IInboxStatementProcessingService
    {
        public Task<InboxStatementProcessingResult> ProcessAsync(Guid workspace, Guid document, Guid candidate, Guid user, string? password, CancellationToken ct) =>
            Task.FromResult(new InboxStatementProcessingResult(new("NeedsReview"), new("NeedsReview", FailureCode: "AgentUnavailable")));
    }
    [Fact]
    public async Task SuccessfulSearchOnlyReceivesDocumentAndPreservesCheckpointAndFinancialTables()
    {
        var session = await SignInAsync("email-flow-synthetic-owner");
        var workspace = await CreateWorkspaceAsync(session);
        using var db = Db();
        var target = CardEmailSearchAuthorization.AuthorizedPersonalId;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_workspaces SET id = {target} WHERE id = {workspace.Id}");
        var card = FinancialCreditCard.Create(target, new("Synthetic", "Bank", "1234", null, 100m, 5, 10, null, "synthetic-query"), workspace.OwnerUserId, DateTimeOffset.UtcNow);
        var checkpoint = FinancialEmailCollectorCheckpoint.Create(target, "existing-collector");
        checkpoint.Complete(DateTimeOffset.UtcNow, DateTimeOffset.Parse("2026-10-01T12:00:00Z"), "Completed", 10, 1, 1, 1, 0, 0, 0);
        db.FinancialCreditCards.Add(card); db.FinancialEmailCollectorCheckpoints.Add(checkpoint); await db.SaveChangesAsync();
        // Compare persisted snapshots on both sides (PostgreSQL timestamps have microsecond precision).
        await db.Entry(checkpoint).ReloadAsync();
        var before = JsonSerializer.Serialize(checkpoint);
        var cards = new FinancialCreditCardRepository(db);
        var preview = new SyntheticPreview(card.Id);
        var storage = new MemoryStorage();
        var inbox = new FinancialDocumentInboxService(new FinancialDocumentInboxRepository(db), storage, new ForbiddenAnalysis(), preview,
            new CreditCardStatementResolver(cards), new FinancialDocumentClassificationOptions { Enabled = true }, new ForbiddenAnalysis(), new FinancialDocumentAnalysisRouter(), TimeProvider.System);
        var service = new CardLatestEmailService(new(new FinancialWorkspaceRepository(db)), cards, new SyntheticSource(), preview, inbox, new PostgresFinancialEmailCollectorLock(ConnectionString), new PendingProcessing());
        var found = await service.SearchAsync(target, card.Id, workspace.OwnerUserId, default);
        var repeated = await service.SearchAsync(target, card.Id, workspace.OwnerUserId, default);
        Assert.Equal("Found", found.Status); Assert.Equal("AlreadyReceived", repeated.Status);
        Assert.True(repeated.AlreadyExisted); Assert.Equal(found.DocumentId, repeated.DocumentId);
        Assert.Single(storage.Objects);
        db.ChangeTracker.Clear();
        var item = await db.FinancialDocumentInboxItems.SingleAsync();
        Assert.Equal(FinancialDocumentInboxStatus.Received, item.Status); Assert.Null(item.CreditCardId); Assert.Null(item.ClassifiedAt);
        Assert.Equal(item.Id, (await inbox.GetAsync(target, item.Id, workspace.OwnerUserId, default))!.Id);
        Assert.Null(await inbox.GetAsync(target, item.Id, Guid.NewGuid(), default));
        var opened = await SendJsonAsync(HttpMethod.Get, $"/api/financial-workspaces/{target}/financial-documents/inbox/{item.Id}", session, null, null);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(await db.FinancialEmailCollectorCheckpoints.SingleAsync()));
        Assert.Empty(await db.FinancialTransactions.ToListAsync()); Assert.Empty(await db.FinancialCreditCardPurchases.ToListAsync());
        Assert.Empty(await db.FinancialCreditCardInvoices.ToListAsync()); Assert.Empty(await db.FinancialCreditCardInvoicePayments.ToListAsync());
    }

    [Fact]
    public async Task QueryRoundTripsThroughPostgresAndEndpointRequiresAuthenticationCsrfAndApprovedPersonal()
    {
        var session = await SignInAsync("card-email-owner");
        var workspace = await CreateWorkspaceAsync(session);
        var root = $"/api/financial-workspaces/{workspace.Id}/credit-cards";
        object Input(string? query) => new { name = "Synthetic card", issuer = "Synthetic bank", lastFourDigits = "1234", creditLimit = "1000.00", closingDay = 5, dueDay = 10, gmailQuery = query };
        var created = await SendWithCsrfAsync(HttpMethod.Post, root, session, Input(" from:synthetic.invalid filename:pdf "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var card = await ReadAsync<JsonElement>(created);
        Assert.Equal("from:synthetic.invalid filename:pdf", card.GetProperty("gmailQuery").GetString());
        var id = card.GetProperty("id").GetGuid();
        using (var db = Db()) Assert.Equal("from:synthetic.invalid filename:pdf", (await db.FinancialCreditCards.SingleAsync()).GmailQuery);
        var query = await SendJsonAsync(HttpMethod.Get, $"{root}/{id}", session, null, null);
        Assert.Equal("from:synthetic.invalid filename:pdf", (await ReadAsync<JsonElement>(query)).GetProperty("gmailQuery").GetString());
        var url = $"{root}/{id}/imports/email/latest";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendJsonAsync(HttpMethod.Post, url, session, null, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, url, session)).StatusCode);
        var updated = await SendWithCsrfAsync(HttpMethod.Put, $"{root}/{id}", session, Input("second-query"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("second-query", (await ReadAsync<JsonElement>(updated)).GetProperty("gmailQuery").GetString());
        var invalid = await SendWithCsrfAsync(HttpMethod.Put, $"{root}/{id}", session, Input(new string('x', 2049)));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var cleared = await SendWithCsrfAsync(HttpMethod.Put, $"{root}/{id}", session, Input(null));
        Assert.Equal(JsonValueKind.Null, (await ReadAsync<JsonElement>(cleared)).GetProperty("gmailQuery").ValueKind);
        using var check = Db();
        Assert.Empty(await check.FinancialEmailCollectorCheckpoints.ToListAsync());
        Assert.Empty(await check.FinancialDocumentInboxItems.ToListAsync());
        Assert.Empty(await check.FinancialTransactions.ToListAsync());
        Assert.Empty(await check.FinancialCreditCardPurchases.ToListAsync());
        Assert.Empty(await check.FinancialCreditCardInvoices.ToListAsync());
        Assert.Empty(await check.FinancialCreditCardInvoicePayments.ToListAsync());
    }

    [Fact]
    public async Task ApprovedPersonalWithoutQueryReturnsSafeNoticeWithoutTouchingGmail()
    {
        var session = await SignInAsync("approved-synthetic-owner");
        var workspace = await CreateWorkspaceAsync(session);
        // Only this disposable test database is changed. No production connection is used.
        using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_workspaces SET id = {CardEmailSearchAuthorization.AuthorizedPersonalId} WHERE id = {workspace.Id}");
        var root = $"/api/financial-workspaces/{CardEmailSearchAuthorization.AuthorizedPersonalId}/credit-cards";
        var created = await SendWithCsrfAsync(HttpMethod.Post, root, session, new { name = "Synthetic", issuer = "Bank", creditLimit = "100.00", closingDay = 5, dueDay = 10 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var card = await ReadAsync<JsonElement>(created);
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{root}/{card.GetProperty("id").GetGuid()}/imports/email/latest", session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadAsync<CardLatestEmailResult>(response);
        Assert.Equal("QueryMissing", result.Status); Assert.Null(result.DocumentId);
    }

    private sealed class SyntheticSource : IFinancialEmailSource
    {
        public Task<IReadOnlyList<FinancialEmailMessage>> SearchLatestAsync(string q, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<FinancialEmailMessage>>([new("synthetic", DateTimeOffset.Parse("2026-10-23T12:00:00Z"))]);
        public Task<IReadOnlyList<FinancialEmailMessage>> SearchAsync(string q, DateTimeOffset after, int max, CancellationToken ct) => throw new InvalidOperationException();
        public Task<IReadOnlyList<FinancialEmailAttachment>> ListAttachmentsAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<FinancialEmailAttachment>>([new("attachment", "1", "bill.pdf", "application/pdf", 10)]);
        public Task<Stream> OpenAttachmentAsync(string m, string a, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream("%PDF-1.4 synthetic"u8.ToArray()));
        public Task<FinancialEmailBody> GetBodyAsync(string m, CancellationToken ct) => throw new InvalidOperationException();
    }
    private sealed class SyntheticPreview(Guid cardId) : ICreditCardPdfPreviewService
    {
        public Task<CreditCardPdfPreviewResult> PreviewAsync(Guid w, Guid c, Guid u, Stream pdf, long length, string file, string? issuer, string? competence, string? format, string? password, CancellationToken ct) =>
            Task.FromResult(new CreditCardPdfPreviewResult(CreditCardPdfPreviewAccess.Allowed,
                new CreditCardPdfPreview(file, 1, "Synthetic", new("Bank", "1234", null, null, null, null), new(0, 0, 0, 0), [], []) { CardResolution = new("Matched", cardId) }, null));
    }
    private sealed class ForbiddenAnalysis : ICreditCardPdfTextExtractor, IFinancialDocumentClassifier
    {
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int max, IReadOnlyList<string> passwords, CancellationToken ct) => throw new InvalidOperationException("Automatic analysis forbidden.");
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument doc, string file, string? competence, string? format) => throw new InvalidOperationException();
        public Task<FinancialDocumentClassificationResult> ClassifyAsync(FinancialDocumentClassificationRequest request, CancellationToken ct) => throw new InvalidOperationException("Classification forbidden.");
    }
    private sealed class MemoryStorage : IFinancialDocumentStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public async Task<string> SaveAsync(Guid w, Guid d, Stream content, CancellationToken ct) { using var output = new MemoryStream(); await content.CopyToAsync(output, ct); Objects.Add(d.ToString(), output.ToArray()); return d.ToString(); }
        public Task<Stream?> OpenReadAsync(string reference, CancellationToken ct) => throw new InvalidOperationException();
        public Task DeleteAsync(string reference, CancellationToken ct) { Objects.Remove(reference); return Task.CompletedTask; }
    }
}

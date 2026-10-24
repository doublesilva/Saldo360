using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class InboxStatementAnalysisTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Theory]
    [InlineData(FinancialDocumentSource.Email, CreditCardStatementAgentMode.AgentPrimary)]
    [InlineData(FinancialDocumentSource.Manual, CreditCardStatementAgentMode.AgentPrimary)]
    [InlineData(FinancialDocumentSource.Email, CreditCardStatementAgentMode.Disabled)]
    [InlineData(FinancialDocumentSource.Email, CreditCardStatementAgentMode.Shadow)]
    public async Task StoredDocumentUsesExistingOrchestratorAndNeverWritesFinancialFacts(FinancialDocumentSource source, CreditCardStatementAgentMode mode)
    {
        var f = await Arrange(source, mode);
        using var db = f.Db;
        var first = await f.Service.AnalyzeAsync(f.Workspace, f.Item.Id, f.Card.Id, f.User, null, default);
        var repeated = await f.Service.AnalyzeAsync(f.Workspace, f.Item.Id, f.Card.Id, f.User, null, default);
        Assert.Equal("Reliable", first.Status); Assert.Equal("AlreadyAnalyzed", repeated.Status);
        Assert.Equal(mode == CreditCardStatementAgentMode.Disabled ? 0 : 1, f.Provider.Calls);
        Assert.Equal(mode == CreditCardStatementAgentMode.AgentPrimary ? "Agent" : "Parser", first.Analysis!.Extraction!.PrimarySource);
        Assert.Equal("10.00", first.Analysis.Extraction.Primary!.Reconciliation.ComponentsTotal);
        Assert.Single(first.Analysis.Categories);
        Assert.Equal(FinancialDocumentInboxStatus.ReadyForReview, f.Item.Status);
        Assert.Null(f.Item.CreditCardId); // Document metadata does not manufacture a financial association.
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.FinancialDocumentInboxItems.SingleAsync()).StatementAnalysisJson);
        Assert.Equal(2, await db.FinancialCategories.CountAsync());
        Assert.Empty(await db.FinancialTransactions.ToListAsync()); Assert.Empty(await db.FinancialCreditCardPurchases.ToListAsync());
        Assert.Empty(await db.FinancialCreditCardInvoices.ToListAsync()); Assert.Empty(await db.FinancialCreditCardInvoicePayments.ToListAsync());
        Assert.Empty(await db.FinancialEmailCollectorCheckpoints.ToListAsync());
        var endpoint = $"/api/financial-workspaces/{f.Workspace}/financial-documents/inbox/{f.Item.Id}/statement-analysis";
        var response = await SendJsonAsync(HttpMethod.Get, endpoint, f.Session, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Reliable", (await ReadAsync<JsonElement>(response)).GetProperty("analysis").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await SendJsonAsync(HttpMethod.Post, endpoint, f.Session, null, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsync(endpoint, null)).StatusCode);
    }

    [Fact]
    public async Task ConfirmedPurchaseHistoryWinsAndManualCorrectionSurvivesReanalysis()
    {
        var f = await Arrange(FinancialDocumentSource.Email, CreditCardStatementAgentMode.AgentPrimary);
        using var db = f.Db;
        f.Provider.Category = "Outros";
        var history = FinancialCreditCardPurchase.Create(f.Workspace, f.Card.Id, f.Health.Id, "SHOP", new(2026, 10, 1), 10m, 1, f.User, DateTimeOffset.UtcNow);
        db.FinancialCreditCardPurchases.Add(history); await db.SaveChangesAsync();
        var result = await f.Service.AnalyzeAsync(f.Workspace, f.Item.Id, f.Card.Id, f.User, null, default);
        var suggested = Assert.Single(result.Analysis!.Categories); Assert.Equal("History", suggested.Source); Assert.Equal(f.Health.Id, suggested.CategoryId);
        var correction = await f.Service.CorrectCategoryAsync(f.Workspace, f.Item.Id, suggested.Key, f.Other.Id, f.User, default);
        Assert.Equal("Corrected", correction.Status);
        f.Options.Model = "synthetic-model-next"; // Invalidate only the analysis cache, not any financial record.
        var reanalyzed = await f.Service.AnalyzeAsync(f.Workspace, f.Item.Id, f.Card.Id, f.User, null, default);
        Assert.Equal("Manual", Assert.Single(reanalyzed.Analysis!.Categories).Source);
        Assert.Equal(f.Other.Id, reanalyzed.Analysis.Categories[0].CategoryId);
        Assert.Equal(f.Health.Id, history.CategoryId); Assert.Single(await db.FinancialCreditCardPurchases.ToListAsync());
        Assert.Empty(await db.FinancialTransactions.ToListAsync()); Assert.Empty(await db.FinancialCreditCardInvoices.ToListAsync());
        var foreign = await f.Service.CorrectCategoryAsync(f.Workspace, f.Item.Id, suggested.Key, Guid.NewGuid(), f.User, default);
        Assert.Equal("Invalid", foreign.Status);
        var second = FinancialDocumentInboxItem.Create(Guid.NewGuid(), f.Workspace, FinancialDocumentSource.Email, "gmail:second-copy:part:1", "second.pdf", new string('b', 64), "application/pdf", "second-private", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        db.FinancialDocumentInboxItems.Add(second); await db.SaveChangesAsync();
        var secondResult = await f.Service.AnalyzeAsync(f.Workspace, second.Id, f.Card.Id, f.User, null, default);
        Assert.Equal("History", Assert.Single(secondResult.Analysis!.Categories).Source);
        Assert.Equal(f.Other.Id, secondResult.Analysis.Categories[0].CategoryId);
        var isolated = await new StatementCategoryContextRepository(db).ReadAsync(f.Workspace, Guid.NewGuid(), Guid.NewGuid(), default);
        Assert.Empty(isolated.History);
        var endpoint = $"/api/financial-workspaces/{f.Workspace}/financial-documents/inbox/{f.Item.Id}/statement-analysis/items/{suggested.Key}/category";
        Assert.Equal(HttpStatusCode.BadRequest, (await SendJsonAsync(HttpMethod.Put, endpoint, f.Session, null, new { categoryId = f.Other.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, endpoint, f.Session, new { categoryId = f.Other.Id })).StatusCode);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("json")]
    [InlineData("password")]
    [InlineData("no-text")]
    [InlineData("wrong-card")]
    public async Task FailuresRequireReviewAndDoNotWriteFinancialFacts(string failure)
    {
        var f = await Arrange(FinancialDocumentSource.Email, CreditCardStatementAgentMode.AgentPrimary);
        using var db = f.Db; f.Provider.Failure = failure; f.Extractor.Failure = failure;
        var result = await f.Service.AnalyzeAsync(f.Workspace, f.Item.Id, f.Card.Id, f.User, null, default);
        Assert.Equal("Rejected", result.Status); Assert.Equal(FinancialDocumentInboxStatus.NeedsReview, f.Item.Status);
        if (failure is "password" or "no-text" or "wrong-card") Assert.Equal(0, f.Provider.Calls);
        Assert.Empty(await db.FinancialCreditCardPurchases.ToListAsync()); Assert.Empty(await db.FinancialTransactions.ToListAsync());
        Assert.Empty(await db.FinancialEmailCollectorCheckpoints.ToListAsync()); Assert.Equal(2, await db.FinancialCategories.CountAsync());
    }

    [Fact]
    public async Task UnauthorizedContextsAndImportedDocumentNeverReachStorageOrAgent()
    {
        var f = await Arrange(FinancialDocumentSource.Email, CreditCardStatementAgentMode.AgentPrimary);
        using var db = f.Db;
        Assert.Equal("NotFound", (await f.Service.AnalyzeAsync(Guid.NewGuid(), f.Item.Id, f.Card.Id, f.User, null, default)).Status);
        Assert.Equal("NotFound", (await f.Service.AnalyzeAsync(f.Workspace, f.Item.Id, f.Card.Id, Guid.NewGuid(), null, default)).Status);
        Assert.Equal("NotFound", (await f.Service.AnalyzeAsync(f.Workspace, f.Item.Id, Guid.NewGuid(), f.User, null, default)).Status);
        f.Item.MarkImported(f.Card.Id, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        Assert.Equal("Conflict", (await f.Service.AnalyzeAsync(f.Workspace, f.Item.Id, f.Card.Id, f.User, null, default)).Status);
        Assert.Equal(0, f.Storage.Reads); Assert.Equal(0, f.Provider.Calls);
    }

    private async Task<Fixture> Arrange(FinancialDocumentSource source, CreditCardStatementAgentMode mode)
    {
        var session = await SignInAsync("statement-analysis-owner");
        var workspace = await CreateWorkspaceAsync(session); var db = Db();
        var target = CardEmailSearchAuthorization.AuthorizedPersonalId;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_workspaces SET id = {target} WHERE id = {workspace.Id}");
        var now = DateTimeOffset.UtcNow;
        var card = FinancialCreditCard.Create(target, new("Synthetic", "Bank", "1234", null, 100m, 5, 10, null), workspace.OwnerUserId, now);
        var health = FinancialCategory.Create(target, "Saúde", CategoryType.Expense, workspace.OwnerUserId, now);
        var other = FinancialCategory.Create(target, "Outros", CategoryType.Expense, workspace.OwnerUserId, now);
        var item = FinancialDocumentInboxItem.Create(Guid.NewGuid(), target, source, source == FinancialDocumentSource.Email ? "gmail:synthetic:part:1" : null,
            "synthetic.pdf", new string('a', 64), "application/pdf", "synthetic-private", now, now);
        db.FinancialCreditCards.Add(card); db.FinancialCategories.AddRange(health, other); db.FinancialDocumentInboxItems.Add(item); await db.SaveChangesAsync();
        var options = new CreditCardStatementAgentOptions { Enabled = true, Mode = mode, Model = "synthetic" };
        var extractor = new Extractor(); var provider = new Provider(); var storage = new Storage();
        var cards = new FinancialCreditCardRepository(db);
        var orchestrator = new CreditCardStatementExtractionOrchestrator(options, new ParserBackedCreditCardStatementExtractorAgent(extractor), new(provider));
        var preview = new CreditCardStatementAgentPreviewService(cards, extractor, new Passwords(), orchestrator);
        var service = new InboxStatementAnalysisService(new(new FinancialWorkspaceRepository(db)), new FinancialDocumentInboxRepository(db), storage, cards, preview,
            new CreditCardStatementResolver(cards), new StatementCategoryContextRepository(db), options, new PostgresFinancialEmailCollectorLock(ConnectionString), TimeProvider.System);
        return new(db, target, workspace.OwnerUserId, session, card, health, other, item, service, options, provider, extractor, storage);
    }
    private sealed record Fixture(Saldo360.Infrastructure.Persistence.Saldo360DbContext Db, Guid Workspace, Guid User, string Session,
        FinancialCreditCard Card, FinancialCategory Health, FinancialCategory Other, FinancialDocumentInboxItem Item,
        InboxStatementAnalysisService Service, CreditCardStatementAgentOptions Options, Provider Provider, Extractor Extractor, Storage Storage);
    private sealed class Provider : ICreditCardStatementAgentProvider
    {
        public int Calls; public string? Failure; public string Category = "Saúde";
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct)
        {
            Calls++; if (Failure == "provider") throw new CreditCardStatementAgentUnavailableException("synthetic unavailable");
            if (Failure == "json") return Task.FromResult("not-json");
            Assert.DoesNotContain("gmail:", request.DocumentText); Assert.DoesNotContain("OwnerUserId", request.DocumentText);
            var item = new CreditCardStatementAgentItem("SHOP", "10.00", "Debit", "Purchase", null, null, "CurrentInvoice", true, 1, "SHOP 10.00", "High", "2026-10-01", "SHOP", Category, "High");
            return Task.FromResult(JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", new("Bank", "1234", "2026-10", null, null, "10.00"), [item]), CreditCardStatementAgentSchema.Json));
        }
    }
    private sealed class Extractor : ICreditCardPdfTextExtractor
    {
        public string? Failure;
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken ct)
        {
            Assert.Equal(50, maxPages);
            if (Failure == "password") throw new CreditCardPdfReadException(CreditCardPdfReadFailure.PasswordRequired);
            return Task.FromResult(new CreditCardPdfExtractedDocument(Failure == "no-text" ? [] : [new(1, "01/10 SHOP 10.00\nTOTAL 10.00")]));
        }
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string file, string? competence, string? format) =>
            new(file, 1, "Synthetic", new("Bank", Failure == "wrong-card" ? "9999" : "1234", "2026-10", null, null, "10.00"), new(1, 0, 0, 0),
                [new(1, 1, "SHOP 10.00", "2026-10-01", "SHOP", "10.00", "Purchase", null, null, "2026-10", "Valid", [], []) { Nature = "Debit", IncludedInCurrentTotal = true }], []);
    }
    private sealed class Passwords : ICreditCardPdfPasswordProvider
    { public Task<CreditCardPdfPasswordResolution> ResolveAsync(Guid id, string issuer, CancellationToken ct) => Task.FromResult(CreditCardPdfPasswordResolution.Unavailable); }
    private sealed class Storage : IFinancialDocumentStorage
    {
        public int Reads;
        public Task<Stream?> OpenReadAsync(string reference, CancellationToken ct) { Reads++; return Task.FromResult<Stream?>(new MemoryStream("%PDF-1.4 synthetic"u8.ToArray())); }
        public Task<string> SaveAsync(Guid w, Guid d, Stream content, CancellationToken ct) => throw new InvalidOperationException("No upload is allowed during analysis.");
        public Task DeleteAsync(string reference, CancellationToken ct) => throw new InvalidOperationException();
    }
}

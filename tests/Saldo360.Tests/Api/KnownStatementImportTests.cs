using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Saldo360.Application.Bootstrap;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Bootstrap;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class KnownStatementImportTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task ProcessingRequiresOneDocumentConfirmationAndPreservesNineUnknownDates()
    {
        var f = await Arrange(); using var db = f.Db;
        var processing = new InboxStatementProcessingService(f.Analysis, new(f.Gate, new CreditCardPdfConfirmationRepository(db, f.Storage, f.Options), f.Locks, TimeProvider.System));
        var result = await processing.ProcessAsync(f.W, f.Doc.Id, f.Card.Id, f.User, null, default);
        Assert.Equal("Reliable", result.Analysis.Status); Assert.Equal("AwaitingConfirmation", result.Consolidation.Status);
        Assert.True(result.Analysis.Analysis!.KnownInstallmentImport!.Eligible);
        Assert.False(result.Analysis.Analysis.Extraction!.PurchaseDocumentValidation!.DatesComplete);
        Assert.Empty(await db.FinancialCreditCardImportedInstallments.ToListAsync());
        Assert.Equal("AlreadyAnalyzed", (await f.Analysis.AnalyzeAsync(f.W, f.Doc.Id, f.Card.Id, f.User, null, default)).Status);
        Assert.Equal(1, f.Pipeline.Calls);
        Assert.Equal("Conflict", (await f.Analysis.CorrectCategoryAsync(f.W, f.Doc.Id, "row0", f.Category.Id, f.User, default)).Status);
        Assert.Equal("Imported", (await Confirm(f)).Status);
        Assert.Equal("AlreadyImported", (await Confirm(f)).Status);
        var rows = await db.FinancialCreditCardImportedInstallments.OrderBy(k => k.ExternalReference).ToArrayAsync();
        Assert.Equal(9, rows.Length); Assert.All(rows, k => { Assert.Null(k.TransactionDate); Assert.Null(k.InvoiceId); Assert.Equal(10m, k.Amount); });
        Assert.Equal(new int?[] { 6, 4, 4, 3, 3, 2, 5, 5, 4 }, rows.Select(k => k.InstallmentNumber));
        Assert.Equal(new int?[] { 6, 6, 5, 3, 12, 2, 6, 5, 12 }, rows.Select(k => k.InstallmentCount));
        Assert.Equal(60m, rows.Where(k => k.Competence == new DateOnly(2026, 10, 1)).Sum(k => k.Amount));
        Assert.Equal(30m, rows.Where(k => k.Competence == new DateOnly(2026, 11, 1)).Sum(k => k.Amount));
        Assert.Equal(10, await db.FinancialBootstrapProvenance.CountAsync());
        Assert.Contains("FutureInstallments", (await db.FinancialBootstrapProvenance.SingleAsync(p => p.ExternalReference == "row:6")).MetadataJson!);
        await AssertNoInventedFinancialEntities(db);
        Assert.Single(await db.FinancialCategories.ToListAsync());
        Assert.Equal("Conflict", (await f.Analysis.CorrectCategoryAsync(f.W, f.Doc.Id, "row0", f.Category.Id, f.User, default)).Status);
    }

    [Fact]
    public async Task CompatibleInvoiceIsUsedWithoutClosingItOrCountingFutureTwice()
    {
        var f = await Arrange(); using var db = f.Db; await Analyze(f);
        var invoice = Invoice(f, "2026-10"); db.Add(invoice); await db.SaveChangesAsync();
        Assert.Equal("Imported", (await Confirm(f)).Status);
        Assert.Equal(6, await db.FinancialCreditCardImportedInstallments.CountAsync(k => k.InvoiceId == invoice.Id));
        Assert.Equal(3, await db.FinancialCreditCardImportedInstallments.CountAsync(k => k.InvoiceId == null));
        Assert.Equal(60m, await FinancialCreditCardInvoiceTotals.TotalAsync(db, f.W, invoice.Id, default));
        Assert.Equal(0, (await db.FinancialCreditCardInvoices.FindAsync(invoice.Id))!.OfficialVersion);
        var futureInvoice = Invoice(f, "2026-11"); db.Add(futureInvoice); await db.SaveChangesAsync();
        var future = await db.FinancialCreditCardImportedInstallments.FirstAsync(k => k.InvoiceId == null);
        var attach = new FinancialBootstrapExtensions(db, TimeProvider.System);
        Assert.Equal(BootstrapDisposition.Created, (await attach.AttachKnownInvoiceAsync(f.W, f.User, future.Id, futureInvoice.Id, default)).Disposition);
        Assert.Equal(BootstrapDisposition.AlreadyImported, (await attach.AttachKnownInvoiceAsync(f.W, f.User, future.Id, futureInvoice.Id, default)).Disposition);
        Assert.Equal(9, await db.FinancialCreditCardImportedInstallments.CountAsync());
        Assert.Equal(60m, await FinancialCreditCardInvoiceTotals.TotalAsync(db, f.W, invoice.Id, default));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("hash")]
    [InlineData("disabled")]
    [InlineData("invoice-due")]
    [InlineData("official")]
    [InlineData("existing-amount")]
    [InlineData("cancelled")]
    [InlineData("ambiguous")]
    public async Task ConflictsRollbackTheEntireDocument(string failure)
    {
        var f = await Arrange(); using var db = f.Db; await Analyze(f);
        if (failure == "stale") f.Options.DocumentaryImport.ReasoningEffort = "high";
        if (failure == "hash") f.Storage.Bytes = "%PDF-changed"u8.ToArray();
        if (failure == "disabled") f.Options.DocumentaryImport.Enabled = false;
        if (failure == "official") await CreateOfficialInvoice(f);
        if (failure == "invoice-due")
        {
            // The future invoice is checked after all six current rows have been staged.
            var inv = Invoice(f, "2026-11", failure == "invoice-due" ? 14 : 13);
            db.Add(inv);
        }
        if (failure is "existing-amount" or "cancelled" or "ambiguous")
        {
            var row = Known(f, 5, failure == "existing-amount" ? 11m : 10m);
            if (failure == "cancelled") row.Cancel(f.User, DateTimeOffset.UtcNow);
            db.Add(row);
            if (failure == "ambiguous") db.Add(Known(f, 5));
        }
        await db.SaveChangesAsync(); var before = await db.FinancialCreditCardImportedInstallments.CountAsync();
        var purchasesBefore = await db.FinancialCreditCardPurchases.CountAsync();
        var result = await Confirm(f);
        Assert.Contains(result.Status, new[] { "NeedsReview", "Conflict" }); Assert.NotNull(result.FailureCode);
        Assert.Equal(before, await db.FinancialCreditCardImportedInstallments.CountAsync());
        Assert.Empty(await db.FinancialBootstrapProvenance.ToListAsync());
        Assert.NotEqual(FinancialDocumentInboxStatus.Imported, (await db.FinancialDocumentInboxItems.SingleAsync(d => d.Id == f.Doc.Id)).Status);
        Assert.Equal(purchasesBefore, await db.FinancialCreditCardPurchases.CountAsync());
        Assert.Single(await db.FinancialCategories.ToListAsync());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ConcurrentConfirmationCommitsOnceAndNeverRepeatsTheProvider()
    {
        var f = await Arrange(); using var db = f.Db; await Analyze(f);
        async Task<OfficialStatementConsolidationResult> Run()
        { using var isolated = Db(); return await new CreditCardPdfConfirmationRepository(isolated, f.Storage, f.Options).ConsolidateInboxAsync(f.W, f.Card.Id, f.Doc.Id, f.User, DateTimeOffset.UtcNow, default); }
        var results = await Task.WhenAll(Run(), Run());
        Assert.Single(results, r => r.Status == "Imported"); Assert.Single(results, r => r.Status == "AlreadyImported");
        Assert.Equal(9, await db.FinancialCreditCardImportedInstallments.CountAsync()); Assert.Equal(1, f.Pipeline.Calls);
    }

    [Fact]
    public async Task FinalSaveFailureRollsBackAllRowsAndConfirmationJournal()
    {
        var f = await Arrange(); using var db = f.Db; await Analyze(f);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION synthetic_fail_known_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic commit failure'; END $$;
            CREATE TRIGGER synthetic_fail BEFORE INSERT ON financial_bootstrap_provenance FOR EACH ROW EXECUTE FUNCTION synthetic_fail_known_commit();
            """);
        var result = await Confirm(f);
        Assert.Equal("Conflict", result.Status); Assert.Equal("ConsolidationRolledBack", result.FailureCode);
        Assert.Empty(await db.FinancialCreditCardImportedInstallments.ToListAsync()); Assert.Empty(await db.FinancialBootstrapProvenance.ToListAsync());
        Assert.Equal(FinancialDocumentInboxStatus.ReadyForReview, (await db.FinancialDocumentInboxItems.SingleAsync()).Status);
        Assert.False(db.ChangeTracker.HasChanges()); await AssertNoInventedFinancialEntities(db);
    }

    [Fact]
    public async Task AuthenticatedHttpProcessingDoesNotConfirmAndConfirmationIgnoresClientAmounts()
    {
        var f = await Arrange(); using var db = f.Db;
        using var app = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.AddSingleton(f.Options); services.AddSingleton<IFinancialDocumentStorage>(f.Storage); services.AddSingleton<IStatementDocumentaryPipeline>(f.Pipeline); }));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        var root = $"/api/financial-workspaces/{f.W}/financial-documents/inbox/{f.Doc.Id}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(root + "/statement-consolidation", null)).StatusCode);
        using var badCsrf = new HttpRequestMessage(HttpMethod.Post, root + "/statement-consolidation"); badCsrf.Headers.Add("Cookie", f.Session);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(badCsrf)).StatusCode);
        var (cookie, csrf) = await GetCsrfAsync(f.Session);
        async Task<JsonElement> Post(string action)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, root + "/" + action);
            request.Headers.Add("Cookie", $"{f.Session}; {cookie}"); request.Headers.Add("X-CSRF-TOKEN", csrf);
            request.Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["creditCardId"] = f.Card.Id.ToString(), ["officialTotal"] = "999999.00", ["transactionDate"] = "2026-10-13" });
            var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await ReadAsync<JsonElement>(response);
        }
        var processed = await Post("statement-processing");
        Assert.Equal("AwaitingConfirmation", processed.GetProperty("consolidation").GetProperty("status").GetString());
        Assert.Empty(await db.FinancialCreditCardImportedInstallments.ToListAsync());
        Assert.Equal("Imported", (await Post("statement-consolidation")).GetProperty("status").GetString());
        Assert.Equal("AlreadyImported", (await Post("statement-consolidation")).GetProperty("status").GetString());
        Assert.Equal(1, f.Pipeline.Calls);
        Assert.All(await db.FinancialCreditCardImportedInstallments.ToListAsync(), k => { Assert.Equal(10m, k.Amount); Assert.Null(k.TransactionDate); });
        await AssertNoInventedFinancialEntities(db);
    }

    [Fact]
    public async Task KnownCommitmentsReachExistingProjectionsOnceBeforeAndAfterInvoiceAttachment()
    {
        var f = await Arrange(); using var db = f.Db;
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{f.W}/accounts", f.Session,
            new { name = "Synthetic account", type = "Checking", financialScope = "PF", openingBalance = "1000.00", openingBalanceDate = "2026-01-01" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var account = (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
        f.Card.Update(new("Synthetic", "Bank", "1234", null, 1000m, 5, 13, account), f.User, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        await Analyze(f); Assert.Equal("Imported", (await Confirm(f)).Status);
        async Task AssertProjection()
        {
            var projection = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{f.W}/accounts/{account}/projection?through=2026-11-30", f.Session);
            Assert.Equal(HttpStatusCode.OK, projection.StatusCode);
            Assert.Equal("90.00", (await ReadAsync<JsonElement>(projection)).GetProperty("creditCardOutstanding").GetString());
            var limit = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{f.W}/credit-cards/{f.Card.Id}/limit", f.Session);
            Assert.Equal("90.00", (await ReadAsync<JsonElement>(limit)).GetProperty("usedLimit").GetString());
        }
        await AssertProjection();
        var october = Invoice(f, "2026-10"); var november = Invoice(f, "2026-11"); db.AddRange(october, november); await db.SaveChangesAsync();
        var attach = new FinancialBootstrapExtensions(db, TimeProvider.System);
        foreach (var k in await db.FinancialCreditCardImportedInstallments.ToListAsync())
            Assert.Equal(BootstrapDisposition.Created, (await attach.AttachKnownInvoiceAsync(f.W, f.User, k.Id, k.Competence!.Value.Month == 10 ? october.Id : november.Id, default)).Disposition);
        await AssertProjection();
        Assert.Equal(60m, await FinancialCreditCardInvoiceTotals.TotalAsync(db, f.W, october.Id, default));
        Assert.Equal(30m, await FinancialCreditCardInvoiceTotals.TotalAsync(db, f.W, november.Id, default));
    }

    [Fact]
    public async Task ConfirmedDocumentCanLinkLaterInvoicesWithoutAnotherExtractionOrConfirmation()
    {
        var f = await Arrange(); using var db = f.Db; await Analyze(f); Assert.Equal("Imported", (await Confirm(f)).Status);
        var original = (await db.FinancialBootstrapProvenance.SingleAsync(p => p.ExternalReference == "document")).Id;
        db.AddRange(Invoice(f, "2026-10"), Invoice(f, "2026-11")); await db.SaveChangesAsync();
        var processing = new InboxStatementProcessingService(f.Analysis, new(f.Gate, new CreditCardPdfConfirmationRepository(db, f.Storage, f.Options), f.Locks, TimeProvider.System));
        var readOnlyReplay = await processing.ProcessAsync(f.W, f.Doc.Id, f.Card.Id, f.User, null, default);
        Assert.Equal("AlreadyImported", readOnlyReplay.Consolidation.Status);
        Assert.All(await db.FinancialCreditCardImportedInstallments.ToListAsync(), k => Assert.Null(k.InvoiceId));
        Assert.Equal("InvoicesLinked", (await Confirm(f)).Status);
        Assert.Equal("AlreadyImported", (await Confirm(f)).Status);
        Assert.All(await db.FinancialCreditCardImportedInstallments.ToListAsync(), k => { Assert.NotNull(k.InvoiceId); Assert.Null(k.TransactionDate); });
        Assert.Equal(9, await db.FinancialCreditCardImportedInstallments.CountAsync()); Assert.Equal(1, f.Pipeline.Calls);
        Assert.Equal(original, (await db.FinancialBootstrapProvenance.SingleAsync(p => p.ExternalReference == "document")).Id);
        Assert.Equal(2, await db.FinancialBootstrapProvenance.CountAsync(p => p.ExternalReference.StartsWith("invoice:")));
        var totals = new List<decimal>();
        foreach (var invoice in await db.FinancialCreditCardInvoices.OrderByDescending(i => i.Competence).ToListAsync())
            totals.Add(await FinancialCreditCardInvoiceTotals.TotalAsync(db, f.W, invoice.Id, default));
        Assert.Equal(new[] { 30m, 60m }, totals);
    }

    [Fact]
    public async Task LegacyPdfConfirmationCannotDuplicateAKnownDocument()
    {
        var f = await Arrange(); using var db = f.Db; await Analyze(f); Assert.Equal("Imported", (await Confirm(f)).Status);
        var result = await new CreditCardPdfConfirmationRepository(db, f.Storage, f.Options).ConfirmAsync(f.W, f.Card.Id, f.User,
            new(f.Doc.ContentHash, new string('a',64), "Synthetic", "2026-10", 60m, Guid.NewGuid(), []), DateTimeOffset.UtcNow, default);
        Assert.Equal(CreditCardPdfConfirmationAccess.Conflict, result.Access);
        Assert.Equal(9, await db.FinancialCreditCardImportedInstallments.CountAsync());
        Assert.Empty(await db.FinancialCreditCardImportBatches.ToListAsync()); await AssertNoInventedFinancialEntities(db);
    }

    [Fact]
    public async Task AnalysisRechecksStoredHashBeforeContactingTheAgent()
    {
        var f = await Arrange(); using var db = f.Db; f.Storage.Bytes = "%PDF-1.4 changed synthetic source"u8.ToArray();
        var result = await f.Analysis.AnalyzeAsync(f.W, f.Doc.Id, f.Card.Id, f.User, null, default);
        Assert.Equal("Rejected", result.Status); Assert.Equal("DocumentHashMismatch", result.Analysis!.FailureCode);
        Assert.Equal(0, f.Pipeline.Calls); Assert.Empty(await db.FinancialCreditCardImportedInstallments.ToListAsync());
    }

    [Fact]
    public async Task StaleTrackedDocumentCannotRepeatAnAlreadyCompletedExtraction()
    {
        var f = await Arrange(); using var db = f.Db; using var stale = Db();
        Assert.Equal(FinancialDocumentInboxStatus.Received, (await stale.FinancialDocumentInboxItems.SingleAsync()).Status);
        await Analyze(f);
        var cards = new FinancialCreditCardRepository(stale);
        var preview = new CreditCardStatementAgentPreviewService(cards, new ForbiddenExtractor(), new ManualOnlyPdfPasswordProvider(), new ForbiddenOrchestrator(), f.Options, f.Pipeline);
        var service = new InboxStatementAnalysisService(f.Gate, new FinancialDocumentInboxRepository(stale), f.Storage, cards, preview,
            new(cards), new StatementCategoryContextRepository(stale), f.Options, f.Locks, TimeProvider.System);
        Assert.Equal("AlreadyAnalyzed", (await service.AnalyzeAsync(f.W, f.Doc.Id, f.Card.Id, f.User, null, default)).Status);
        Assert.Equal(1, f.Pipeline.Calls);
    }

    [Fact]
    public async Task DisablingProfileCannotFallBackToLegacyForAnAlreadyDocumentaryDocument()
    {
        var f = await Arrange(); using var db = f.Db; await Analyze(f);
        f.Options.DocumentaryImport.Enabled = false;
        var result = await f.Analysis.AnalyzeAsync(f.W, f.Doc.Id, f.Card.Id, f.User, null, default);
        Assert.Equal("Unavailable", result.Status); Assert.Equal("DocumentaryImportDisabled", result.Analysis!.ConsolidationBlockReason);
        Assert.Equal(1, f.Pipeline.Calls); Assert.Empty(await db.FinancialCreditCardImportedInstallments.ToListAsync());
    }

    [Fact]
    public async Task AnotherDocumentWithTheSameKnownObligationsAddsOnlyProvenance()
    {
        var f = await Arrange(); using var db = f.Db; await Analyze(f); Assert.Equal("Imported", (await Confirm(f)).Status);
        var ids = await db.FinancialCreditCardImportedInstallments.OrderBy(k => k.Id).Select(k => k.Id).ToArrayAsync();
        var bytes = "%PDF-1.4 synthetic second documentary version"u8.ToArray(); f.Storage.Extra["second"] = bytes;
        var doc = FinancialDocumentInboxItem.Create(Guid.NewGuid(), f.W, FinancialDocumentSource.Manual, null, "second.pdf",
            Convert.ToHexStringLower(SHA256.HashData(bytes)), "application/pdf", "second", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        db.Add(doc); await db.SaveChangesAsync(); var second = f with { Doc = doc };
        await Analyze(second); Assert.Equal("Imported", (await Confirm(second)).Status);
        Assert.Equal(ids, await db.FinancialCreditCardImportedInstallments.OrderBy(k => k.Id).Select(k => k.Id).ToArrayAsync());
        Assert.Equal(20, await db.FinancialBootstrapProvenance.CountAsync()); Assert.Single(await db.FinancialCategories.ToListAsync());
    }

    [Fact]
    public async Task LegitimatelyEqualCurrentRowsRemainSeparateObligations()
    {
        var f = await Arrange(); using var db = f.Db; f.Pipeline.DuplicateCurrent = true;
        await Analyze(f); Assert.Equal("Imported", (await Confirm(f)).Status);
        Assert.Equal(10, await db.FinancialCreditCardImportedInstallments.CountAsync());
        Assert.Equal(2, await db.FinancialCreditCardImportedInstallments.CountAsync(k => k.Description == "SHOPA06/06"));
        Assert.Equal(70m, await db.FinancialCreditCardImportedInstallments.Where(k => k.Competence == new DateOnly(2026,10,1)).SumAsync(k => k.Amount));
    }

    [Fact]
    public async Task ExplicitPurchaseDatesArePreservedWithoutReconstructingTheOriginalPurchase()
    {
        var f = await Arrange(); using var db = f.Db; f.Pipeline.FullDates = true;
        await Analyze(f); Assert.Equal("Imported", (await Confirm(f)).Status);
        Assert.All(await db.FinancialCreditCardImportedInstallments.ToListAsync(), k => Assert.Equal(new DateOnly(2024,7,4), k.TransactionDate));
        await AssertNoInventedFinancialEntities(db);
    }

    [Fact]
    public async Task FaithfulFutureRowsWithoutUniqueMonthlyAnchorAreNotFinanciallyEligible()
    {
        var f = await Arrange(); using var db = f.Db; f.Pipeline.OrphanFuture = true;
        var result = await f.Analysis.AnalyzeAsync(f.W, f.Doc.Id, f.Card.Id, f.User, null, default);
        Assert.Equal("Reliable", result.Status); Assert.True(result.Analysis!.Extraction!.Suitability!.FaithfulExtraction);
        Assert.False(result.Analysis.KnownInstallmentImport!.Eligible);
        Assert.Equal("FutureInstallmentCompetenceUnproven", result.Analysis.ConsolidationBlockReason);
        Assert.Equal("NeedsReview", (await Confirm(f)).Status); Assert.Empty(await db.FinancialCreditCardImportedInstallments.ToListAsync());
    }

    [Fact]
    public async Task ExistingKnownAndOriginalPurchaseInstallmentsAreReusedNotDuplicated()
    {
        var f = await Arrange(); using var db = f.Db;
        db.Add(Known(f, 0));
        var invoice = Invoice(f, "2026-10"); db.Add(invoice);
        var purchase = FinancialCreditCardPurchase.Create(f.W, f.Card.Id, f.Category.Id, "SHOPF", new(2025, 1, 1), 20m, 2, f.User, DateTimeOffset.UtcNow);
        // The first installment lives in another, already known period; only the second matches this document.
        var old = Invoice(f, "2026-09"); db.Add(old);
        purchase.Installments.First().AssignInvoice(old.Id); purchase.Installments.Last().AssignInvoice(invoice.Id);
        db.Add(purchase); await db.SaveChangesAsync();
        await Analyze(f);
        Assert.Equal("Imported", (await Confirm(f)).Status);
        Assert.Equal(8, await db.FinancialCreditCardImportedInstallments.CountAsync());
        Assert.Single(await db.FinancialCreditCardPurchases.ToListAsync());
        Assert.Equal(60m, await FinancialCreditCardInvoiceTotals.TotalAsync(db, f.W, invoice.Id, default));
        Assert.Equal(20m, (await db.FinancialCreditCardPurchases.SingleAsync()).TotalAmount);
    }

    [Fact]
    public async Task AuthorizationPrecedesStorageAndAnotherCardOrInvalidDocumentCannotImport()
    {
        var f = await Arrange(); using var db = f.Db;
        Assert.Equal("NotFound", (await f.Analysis.AnalyzeAsync(f.W, f.Doc.Id, f.Card.Id, Guid.NewGuid(), null, default)).Status);
        Assert.Equal(0, f.Pipeline.Calls); Assert.Equal(0, f.Storage.Reads);
        await Analyze(f);
        var repo = new CreditCardPdfConfirmationRepository(db, f.Storage, f.Options);
        Assert.Equal("NotFound", (await repo.ConsolidateInboxAsync(f.W, f.Card.Id, f.Doc.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, default)).Status);
        Assert.Equal("NotFound", (await repo.ConsolidateInboxAsync(Guid.NewGuid(), f.Card.Id, f.Doc.Id, f.User, DateTimeOffset.UtcNow, default)).Status);
        var other = FinancialCreditCard.Create(f.W, new("Other", "Bank", "9999", null, 1000m, 5, 13, null), f.User, DateTimeOffset.UtcNow);
        db.Add(other); await db.SaveChangesAsync();
        Assert.Equal("NeedsReview", (await repo.ConsolidateInboxAsync(f.W, other.Id, f.Doc.Id, f.User, DateTimeOffset.UtcNow, default)).Status);
        Assert.Empty(await db.FinancialCreditCardImportedInstallments.ToListAsync());
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("omission")]
    [InlineData("wrong-installment")]
    [InlineData("wrong-value")]
    [InlineData("payment")]
    public async Task InvalidAgentOutputRemainsBlockedWithoutFallback(string failure)
    {
        var f = await Arrange(); using var db = f.Db; f.Pipeline.Failure = failure;
        var result = await f.Analysis.AnalyzeAsync(f.W, f.Doc.Id, f.Card.Id, f.User, null, default);
        Assert.NotEqual("Reliable", result.Status); Assert.False(result.Analysis!.KnownInstallmentImport!.Eligible);
        Assert.Equal("NeedsReview", (await Confirm(f)).Status);
        Assert.Equal(1, f.Pipeline.Calls); Assert.False(result.Analysis.Extraction!.ParserShadowExecuted);
        await AssertNoInventedFinancialEntities(db); Assert.Empty(await db.FinancialCreditCardImportedInstallments.ToListAsync());
    }

    private static async Task AssertNoInventedFinancialEntities(Saldo360DbContext db)
    {
        Assert.Empty(await db.FinancialCreditCardPurchases.ToListAsync()); Assert.Empty(await db.FinancialCreditCardInvoices.ToListAsync());
        Assert.Empty(await db.FinancialCreditCardInvoicePayments.ToListAsync()); Assert.Empty(await db.FinancialTransactions.ToListAsync());
        Assert.Empty(await db.FinancialCreditCardInvoiceAdjustments.ToListAsync()); Assert.Empty(await db.FinancialCreditCardInvoiceConsolidations.ToListAsync());
    }
    private async Task<Fixture> Arrange()
    {
        var session = await SignInAsync("known-statement-owner"); var w = await CreateWorkspaceAsync(session); var db = Db();
        var target = CardEmailSearchAuthorization.AuthorizedPersonalId;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_workspaces SET id = {target} WHERE id = {w.Id}");
        var card = FinancialCreditCard.Create(target, new("Synthetic", "Bank", "1234", null, 1000m, 5, 13, null), w.OwnerUserId, DateTimeOffset.UtcNow);
        var category = FinancialCategory.Create(target, "Outros", CategoryType.Expense, w.OwnerUserId, DateTimeOffset.UtcNow);
        var storage = new Storage();
        var doc = FinancialDocumentInboxItem.Create(Guid.NewGuid(), target, FinancialDocumentSource.Manual, null, "synthetic.pdf",
            Convert.ToHexStringLower(SHA256.HashData(storage.Bytes)), "application/pdf", "synthetic-private", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        db.AddRange(card, category, doc); await db.SaveChangesAsync();
        var options = new CreditCardStatementAgentOptions { DocumentaryImport = new() { Enabled = true } };
        var pipeline = new SyntheticPipeline(); var cards = new FinancialCreditCardRepository(db);
        var gate = new CardEmailSearchAuthorization(new FinancialWorkspaceRepository(db)); var locks = new PostgresFinancialEmailCollectorLock(ConnectionString);
        var preview = new CreditCardStatementAgentPreviewService(cards, new ForbiddenExtractor(), new ManualOnlyPdfPasswordProvider(), new ForbiddenOrchestrator(), options, pipeline);
        var service = new InboxStatementAnalysisService(gate, new FinancialDocumentInboxRepository(db), storage, cards, preview, new(cards), new StatementCategoryContextRepository(db), options, locks, TimeProvider.System);
        return new(db, target, w.OwnerUserId, session, card, category, doc, storage, options, pipeline, service, gate, locks);
    }
    private static async Task Analyze(Fixture f) => Assert.Equal("Reliable", (await f.Analysis.AnalyzeAsync(f.W, f.Doc.Id, f.Card.Id, f.User, null, default)).Status);
    private static async Task CreateOfficialInvoice(Fixture f)
    {
        // Establish a genuinely protected invoice through the existing atomic service and its DB proof.
        var now = DateTimeOffset.UtcNow; var bytes = "%PDF-1.4 synthetic official seed"u8.ToArray();
        f.Storage.Extra["official-seed"] = bytes;
        var doc = FinancialDocumentInboxItem.Create(Guid.NewGuid(), f.W, FinancialDocumentSource.Manual, null, "official-seed.pdf",
            Convert.ToHexStringLower(SHA256.HashData(bytes)), "application/pdf", "official-seed", now, now);
        f.Db.Add(doc); await f.Db.SaveChangesAsync();
        var options = new CreditCardStatementAgentOptions { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary };
        var header = new CreditCardStatementAgentHeader("Bank", "1234", "2026-11", "2026-11-05", "2026-11-13", "1.00");
        var item = new CreditCardStatementAgentItem("SEED", "1.00", "Debit", "Purchase", null, null, "CurrentInvoice", true, 1, "SEED 1.00", "High", "2026-11-01");
        var json = JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", header, [item]), CreditCardStatementAgentSchema.Json);
        Assert.True(CreditCardStatementAgentEvaluator.TryEvaluate(json, "seed.pdf", 1, out var preview, out _));
        var catalog = await new StatementCategoryContextRepository(f.Db).ReadAsync(f.W, f.Card.Id, doc.Id, default);
        var card = (await new FinancialCreditCardRepository(f.Db).GetAsync(f.W, f.Card.Id, default))!;
        var analysis = new InboxStatementAnalysis("2.0", f.Card.Id, InboxStatementAnalysisService.ComputeInputHash(doc, card, catalog, options), "Reliable",
            new("AgentPrimary", "Agent", preview, AgentExtractionAssessment.Reliable, false, null, null, null, null),
            [new("seed", 0, f.Category.Id, f.Category.Name, false, "High", "Existing")], new Dictionary<string, Guid>());
        doc.SetStatementAnalysis(JsonSerializer.Serialize(analysis), true, now); await f.Db.SaveChangesAsync();
        Assert.Equal("Imported", (await new CreditCardPdfConfirmationRepository(f.Db, f.Storage, options).ConsolidateInboxAsync(f.W, f.Card.Id, doc.Id, f.User, now, default)).Status);
        // New history legitimately changes the category catalog; prepare the untouched target again.
        await Analyze(f);
    }
    private static Task<OfficialStatementConsolidationResult> Confirm(Fixture f) => new CreditCardPdfConfirmationRepository(f.Db, f.Storage, f.Options)
        .ConsolidateInboxAsync(f.W, f.Card.Id, f.Doc.Id, f.User, DateTimeOffset.UtcNow, default);
    private static FinancialCreditCardInvoice Invoice(Fixture f, string period, int day = 13)
    { var m = DateOnly.ParseExact(period, "yyyy-MM", CultureInfo.InvariantCulture); return FinancialCreditCardInvoice.Create(f.W, f.Card.Id, period, m.AddMonths(-1).AddDays(5), m.AddDays(4), m.AddDays(day - 1), DateTimeOffset.UtcNow); }
    private static FinancialCreditCardImportedInstallment Known(Fixture f, int index, decimal amount = 10m)
    { var i = Items()[index]; return FinancialCreditCardImportedInstallment.CreateKnown(f.W, f.Card.Id, f.Category.Id, i.OriginalDescription, amount, i.InstallmentNumber, i.InstallmentCount, new(2026, 10, 1), new(2026, 10, 13), "SyntheticExisting", Guid.NewGuid().ToString(), f.User, DateTimeOffset.UtcNow); }
    private sealed record Fixture(Saldo360DbContext Db, Guid W, Guid User, string Session, FinancialCreditCard Card, FinancialCategory Category,
        FinancialDocumentInboxItem Doc, Storage Storage, CreditCardStatementAgentOptions Options, SyntheticPipeline Pipeline,
        InboxStatementAnalysisService Analysis, CardEmailSearchAuthorization Gate, PostgresFinancialEmailCollectorLock Locks);

    internal static CreditCardStatementAgentItem[] Items()
    {
        (int N, int Count, int Shop)[] ratios = [(6,6,0),(4,6,1),(4,5,2),(3,3,3),(3,12,4),(2,2,5),(5,6,1),(5,5,2),(4,12,4)];
        return ratios.Select((r,n) => new CreditCardStatementAgentItem($"SHOP{(char)('A'+r.Shop)}{r.N:00}/{r.Count:00}", "10.00", "Debit", "Installment", r.N, r.Count,
            n < 6 ? "CurrentInvoice" : "FutureInstallments", n < 6, 1, $"04/07 SHOP{(char)('A'+r.Shop)}{r.N:00}/{r.Count:00} 10.00", "High", null, null, "Outros", "High")).ToArray();
    }
    private sealed class SyntheticPipeline : IStatementDocumentaryPipeline
    {
        public int Calls; public string? Failure; public bool FullDates; public bool DuplicateCurrent; public bool OrphanFuture;
        public async Task<CreditCardStatementExtractionResult> ExtractAsync(Stream pdf, PdfPasswordContext context, string? password, string fileName, string? competence, StatementCategoryContext? categories, CancellationToken ct)
        {
            Calls++; Assert.NotEqual(Guid.Empty, context.UserId); Assert.NotNull(context.Card);
            var items = Items();
            if (FullDates) items = items.Select(i => i with { Evidence = i.Evidence.Replace("04/07 ", "04/07/2024 "), TransactionDate = "2024-07-04" }).ToArray();
            if (DuplicateCurrent) items = items.Append(items[0]).ToArray();
            if (OrphanFuture) items = items.Where((_,n) => n != 1).ToArray();
            var total = DuplicateCurrent ? "70.00" : OrphanFuture ? "50.00" : "60.00";
            var source = new CreditCardPdfExtractedDocument([new(1, "Vencimento: 13/10/2026\nLançamentos: compras e saques\n" + string.Join('\n', items.Where(i => i.Section == "CurrentInvoice").Select(i => i.Evidence))
                + "\nTotal dos lançamentos " + total + "\nCompras parceladas - próximas faturas\n" + string.Join('\n', items.Where(i => i.Section == "FutureInstallments").Select(i => i.Evidence)) + "\nTotal para próximas faturas 30.00")
                { ImagePng = [137,80,78,71,13,10,26,10], ProcessingStatus = "NativeWithImage" }]);
            var header = new CreditCardStatementAgentHeader(null, Failure == "identity" ? "9999" : null, "2026-10", null, "2026-10-13", total);
            if (Failure == "omission") items = items[..8];
            if (Failure == "wrong-installment") items[2] = items[2] with { InstallmentNumber = 5 };
            if (Failure == "wrong-value") items[2] = items[2] with { Amount = "11.00" };
            if (Failure == "payment") items[2] = items[2] with { Type = "Payment" };
            var facts = JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", header, items), CreditCardStatementAgentSchema.Json);
            var result = await new CreditCardStatementExtractionOrchestrator(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary }, new ForbiddenParser(), new(new Provider(facts)))
                .ExtractAsync(new(fileName, source, competence) { RequireAgent = true, PurchasesOnly = true, ValidatePurchaseDocument = true,
                    BatchPages = true, AllowCorrectionRetry = false, AuthorizedIdentity = new(context.Card!.Issuer, context.Card.LastFourDigits) }, ct);
            return result with { FinancialCalendarEvidence = StatementKnownInstallmentImport.CorroborateCalendar(source, result.Primary?.Header) };
        }
    }
    private sealed class Provider(string json) : ICreditCardStatementAgentProvider
    { public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest r, CancellationToken ct) { Assert.True(r.PurchasesOnly); return Task.FromResult(json); } }
    private sealed class ForbiddenParser : IParserBackedCreditCardStatementExtractorAgent
    { public Task<string> ExtractAsync(CreditCardStatementAgentRequest r, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Parser forbidden"); }
    private sealed class ForbiddenOrchestrator : ICreditCardStatementExtractionOrchestrator
    { public Task<CreditCardStatementExtractionResult> ExtractAsync(CreditCardStatementAgentRequest r, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Legacy pipeline forbidden"); }
    private sealed class ForbiddenExtractor : ICreditCardPdfTextExtractor
    {
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream s, int max, IReadOnlyList<string> p, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Legacy extractor forbidden");
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument d, string f, string? c, string? format) => throw new Xunit.Sdk.XunitException("Parser forbidden");
    }
    private sealed class Storage : IFinancialDocumentStorage
    {
        public byte[] Bytes = "%PDF-1.4 synthetic known statement"u8.ToArray(); public int Reads;
        public Dictionary<string, byte[]> Extra = [];
        public Task<Stream?> OpenReadAsync(string r, CancellationToken ct) { Reads++; return Task.FromResult<Stream?>(new MemoryStream(Extra.GetValueOrDefault(r) ?? Bytes)); }
        public Task<string> SaveAsync(Guid w, Guid d, Stream s, CancellationToken ct) => throw new InvalidOperationException();
        public Task DeleteAsync(string r, CancellationToken ct) => throw new InvalidOperationException();
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Budgets;
using Saldo360.Infrastructure.Budgets;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
[Trait("Area", "Documents")]
[Trait("Dependency", "PostgreSQL")]
public sealed class OfficialStatementConsolidationTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task OfficialImportAndReplayAreAtomicAndKeepOtherPeriods()
    {
        var f = await Arrange(); using var db = f.Db;
        var provisional = await Purchase(f, "Discarded provisional", 5m);
        var future = await Purchase(f, "Other period", 17m, new(2026, 11, 1));
        var doc = await Document(f, [Item("SHOP", "10.00")], "10.00");
        var first = await Run(f, doc);
        Assert.Equal("Imported", first.Status);
        Assert.Equal("2026-10", (await db.FinancialDocumentInboxItems.SingleAsync()).Competence);
        var replay = await Run(f, doc);
        Assert.Equal("AlreadyImported", replay.Status); Assert.Equal(first.InvoiceId, replay.InvoiceId);
        Assert.Single(await db.FinancialCreditCardInvoiceConsolidations.ToListAsync());
        Assert.Equal(10m, await FinancialCreditCardInvoiceTotals.TotalAsync(db, f.Workspace, first.InvoiceId!.Value, default));
        var invoice = await db.FinancialCreditCardInvoices.FindAsync(first.InvoiceId);
        Assert.Equal(FinancialCreditCardInvoiceStatus.Closed, invoice!.GetStatus(new(2000, 1, 1)));
        Assert.Equal(CreditCardInstallmentStatus.Cancelled, (await db.FinancialCreditCardInstallments.FindAsync(provisional.Installments.Single().Id))!.Status);
        Assert.Equal(CreditCardPurchaseStatus.Cancelled, (await db.FinancialCreditCardPurchases.FindAsync(provisional.Id))!.Status);
        Assert.Equal(CreditCardInstallmentStatus.Active, (await db.FinancialCreditCardInstallments.FindAsync(future.Installments.Single().Id))!.Status);
        Assert.Empty(await db.FinancialCreditCardInvoicePayments.ToListAsync()); Assert.Empty(await db.FinancialTransactions.ToListAsync());
    }

    [Fact]
    public async Task CorrectedDocumentVersionsWithoutDuplicatingCurrentComposition()
    {
        var f = await Arrange(); using var db = f.Db;
        var first = await Document(f, [Item("SHOP", "10.00")], "10.00");
        Assert.Equal("Imported", (await Run(f, first)).Status);
        var second = await Document(f, [Item("SHOP", "12.00")], "12.00");
        var result = await Run(f, second);
        Assert.Equal("NewVersionConsolidated", result.Status); Assert.Equal(2, result.Version);
        Assert.Equal(12m, await FinancialCreditCardInvoiceTotals.TotalAsync(db, f.Workspace, result.InvoiceId!.Value, default));
        Assert.Equal("Conflict", (await Run(f, first)).Status);
        Assert.Equal(2, await db.FinancialCreditCardInvoiceConsolidations.CountAsync());
    }

    [Theory]
    [InlineData("unmatched-installment")]
    [InlineData("ambiguous")]
    [InlineData("divergent")]
    [InlineData("stale")]
    [InlineData("hash")]
    [InlineData("category")]
    [InlineData("calendar")]
    [InlineData("card-calendar")]
    public async Task UnsafeAnalysisRollsBackAllFinancialAndCategoryWrites(string failure)
    {
        var f = await Arrange(); using var db = f.Db;
        if (failure == "ambiguous") { await Purchase(f, "SHOP", 10m); await Purchase(f, "SHOP", 10m); }
        var item = Item("SHOP", "10.00");
        if (failure == "unmatched-installment") item = item with { Type = "Installment", InstallmentNumber = 7, InstallmentCount = 10 };
        var doc = await Document(f, [item], failure == "divergent" ? "11.00" : "10.00", failure == "category" ? "New proposed" : null,
            failure == "calendar" ? "2026-10-06" : "2026-10-05");
        if (failure == "stale") f.Options.Model = "changed";
        if (failure == "card-calendar")
        {
            var card = await db.FinancialCreditCards.SingleAsync();
            card.Update(new(card.Name, card.Issuer, card.LastFourDigits, card.HolderUserId, card.CreditLimit, 6, card.DueDay, card.DefaultPaymentAccountId), f.User, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        if (failure == "hash") f.Storage.Files[doc.StorageReference] = "%PDF-different"u8.ToArray();
        // A proposed category is staged before the missing installment match is rejected.
        if (failure == "category")
        {
            var analysis = InboxStatementAnalysisService.Read(doc.StatementAnalysisJson)!;
            var changed = analysis with { Extraction = analysis.Extraction! with { Primary = analysis.Extraction.Primary! with { Items = [item with { Type = "Installment", InstallmentNumber = 7, InstallmentCount = 10 }] } } };
            doc.SetStatementAnalysis(JsonSerializer.Serialize(changed), true, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        }
        var count = await db.FinancialCreditCardPurchases.CountAsync();
        var result = await Run(f, doc);
        Assert.Contains(result.Status, new[] { "NeedsReview", "Conflict" });
        Assert.Equal(count, await db.FinancialCreditCardPurchases.CountAsync());
        Assert.Single(await db.FinancialCategories.ToListAsync());
        Assert.Empty(await db.FinancialCreditCardInvoiceConsolidations.ToListAsync());
        Assert.All(await db.FinancialCreditCardInvoices.ToListAsync(), i => Assert.Equal(0, i.OfficialVersion));
        Assert.NotEqual(FinancialDocumentInboxStatus.Imported, (await db.FinancialDocumentInboxItems.SingleAsync(d => d.WorkspaceId == f.Workspace && d.Id == doc.Id)).Status);
    }

    [Fact]
    public async Task ProposedCategoryIsCreatedOnlyWithSuccessfulOfficialComposition()
    {
        var f = await Arrange(); using var db = f.Db;
        var doc = await Document(f, [Item("SHOP", "10.00")], "10.00", "Livros");
        Assert.Equal("Imported", (await Run(f, doc)).Status);
        Assert.Equal(2, await db.FinancialCategories.CountAsync());
        Assert.Equal("AlreadyImported", (await Run(f, doc)).Status);
        Assert.Equal(2, await db.FinancialCategories.CountAsync());
    }

    [Fact]
    public async Task CurrentInstallmentAndPendingFutureAmountsChangeWithoutReconstructingPurchase()
    {
        var f = await Arrange(); using var db = f.Db;
        var purchase = await Purchase(f, "SHOP", 30m, count: 3);
        var doc = await Document(f, [Item("SHOP", "12.00") with { Type = "Installment", InstallmentNumber = 1, InstallmentCount = 3 }], "12.00");
        Assert.Equal("Imported", (await Run(f, doc)).Status);
        db.ChangeTracker.Clear(); var stored = await db.FinancialCreditCardPurchases.Include(p => p.Installments).SingleAsync();
        Assert.Equal(purchase.Id, stored.Id); Assert.Equal(30m, stored.TotalAmount);
        Assert.All(stored.Installments, i => Assert.Equal(12m, i.Amount));
        Assert.Single(await db.FinancialCreditCardInvoiceConsolidations.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentRequestsProduceOneVersion()
    {
        var f = await Arrange(); using var db = f.Db;
        var doc = await Document(f, [Item("SHOP", "10.00")], "10.00");
        async Task<OfficialStatementConsolidationResult> Request()
        {
            using var isolated = Db();
            return await new CreditCardPdfConfirmationRepository(isolated, f.Storage, f.Options).ConsolidateInboxAsync(f.Workspace, f.Card.Id, doc.Id, f.User, DateTimeOffset.UtcNow, default);
        }
        var results = await Task.WhenAll(Request(), Request());
        Assert.Single(results, r => r.Status == "Imported"); Assert.Single(results, r => r.Status == "AlreadyImported");
        Assert.Single(await db.FinancialCreditCardInvoiceConsolidations.ToListAsync());
    }

    [Fact]
    public async Task UnauthorizedUserOrCardCannotConsolidate()
    {
        var f = await Arrange(); using var db = f.Db;
        var doc = await Document(f, [Item("SHOP", "10.00")], "10.00");
        var repo = new CreditCardPdfConfirmationRepository(db, f.Storage, f.Options);
        Assert.Equal("NotFound", (await repo.ConsolidateInboxAsync(f.Workspace, f.Card.Id, doc.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, default)).Status);
        Assert.Equal("NotFound", (await repo.ConsolidateInboxAsync(Guid.NewGuid(), f.Card.Id, doc.Id, f.User, DateTimeOffset.UtcNow, default)).Status);
        Assert.Equal("NotFound", (await repo.ConsolidateInboxAsync(f.Workspace, Guid.NewGuid(), doc.Id, f.User, DateTimeOffset.UtcNow, default)).Status);
        Assert.Empty(await db.FinancialCreditCardInvoiceConsolidations.ToListAsync());
    }

    [Fact]
    public async Task OverdueProvisionalInvoiceRemainsOpenAndPaymentIsBlockedInRepository()
    {
        var f = await Arrange(); using var db = f.Db;
        var purchase = await Purchase(f, "SHOP", 10m);
        var invoice = await db.FinancialCreditCardInvoices.FindAsync(purchase.Installments.Single().InvoiceId);
        Assert.Equal(FinancialCreditCardInvoiceStatus.Open, invoice!.GetStatus(new(2030, 1, 1)));
        var result = await new FinancialCreditCardInvoicePaymentRepository(db).CreateAsync(f.Workspace, f.Card.Id, invoice.Id,
            Guid.NewGuid(), Guid.NewGuid(), new(2026, 10, 10), 10m, f.User, new(2030, 1, 1), DateTimeOffset.UtcNow, default);
        Assert.Equal(PaymentOperationResult.OpenInvoice, result.Result);
        Assert.Empty(await db.FinancialTransactions.ToListAsync());
    }

    [Fact]
    public async Task IntermediateInstallmentPreservesOfficialPastAndUsesExplicitFutureValue()
    {
        var f = await Arrange(); using var db = f.Db;
        var purchase = await Purchase(f, "SHOP", 40m, new(2026, 8, 1), 4);
        await OfficialInvoiceFixture.CloseAsync(db, purchase.Installments.Single(i => i.InstallmentNumber == 1).InvoiceId!.Value);
        var current = Item("SHOP", "12.00") with { TransactionDate = "2026-08-01", Type = "Installment", InstallmentNumber = 3, InstallmentCount = 4 };
        var future = current with { Amount = "15.00", IncludedInCurrentTotal = false, Section = "FutureInstallments", InstallmentNumber = 4 };
        var doc = await Document(f, [current, future], "12.00");
        Assert.Equal("Imported", (await Run(f, doc)).Status);
        var schedule = await db.FinancialCreditCardInstallments.OrderBy(i => i.InstallmentNumber).ToListAsync();
        Assert.Equal(new[] { 10m, 10m, 12m, 15m }, schedule.Select(i => i.Amount));
        Assert.Equal(40m, (await db.FinancialCreditCardPurchases.SingleAsync()).TotalAmount);
    }

    [Fact]
    public async Task KnownIntermediateInstallmentsKeepIdsOriginAndUnknownOriginalPurchase()
    {
        var f = await Arrange(); using var db = f.Db;
        var known = Enumerable.Range(7, 4).Select(n => FinancialCreditCardImportedInstallment.CreateKnown(f.Workspace, f.Card.Id, f.Category.Id, "SHOP", 10m, n, 10,
            new DateOnly(2026, 10, 1).AddMonths(n - 7), new DateOnly(2026, 10, 10).AddMonths(n - 7), "Synthetic", $"purchase:installment:{n}", f.User, DateTimeOffset.UtcNow)).ToArray();
        db.AddRange(known); await db.SaveChangesAsync();
        var doc = await Document(f, [Item("SHOP", "12.00") with { Type = "Installment", InstallmentNumber = 7, InstallmentCount = 10, TransactionDate = null }], "12.00");
        Assert.Equal("Imported", (await Run(f, doc)).Status);
        var stored = await db.FinancialCreditCardImportedInstallments.OrderBy(i => i.InstallmentNumber).ToListAsync();
        Assert.Equal(known.Select(i => i.Id), stored.Select(i => i.Id));
        Assert.All(stored, i => { Assert.Equal(12m, i.Amount); Assert.Null(i.TransactionDate); Assert.Equal("Synthetic", i.SourceSystem); });
        Assert.NotNull(stored[0].InvoiceId); Assert.All(stored.Skip(1), i => Assert.Null(i.InvoiceId));
        Assert.Empty(await db.FinancialCreditCardPurchases.ToListAsync());
        var limit = await new FinancialCreditCardPurchaseRepository(db).GetLimitAsync(f.Workspace, f.Card.Id, default);
        Assert.Equal("48.00", limit!.UsedLimit);
    }

    [Fact]
    public async Task CreditsRefundsFeesAndBalancesRespectSignsAndInformationalPaymentIsNotImported()
    {
        var f = await Arrange(); using var db = f.Db;
        var items = new[] { Item("SHOP", "100.00"), Item("Credit", "20.00") with { Type = "Credit", Nature = "Credit" },
            Item("Refund", "5.00") with { Type = "Refund", Nature = "Credit" }, Item("Fee", "3.00") with { Type = "Fee", Section = "Fees" },
            Item("Previous", "10.00") with { Type = "PreviousBalance", Section = "PreviousBalance" },
            Item("Financed", "2.00") with { Type = "FinancedBalance" },
            Item("Payment", "50.00") with { Type = "Payment", Nature = "Credit", Section = "Payments", IncludedInCurrentTotal = false } };
        var doc = await Document(f, items, "90.00");
        var result = await Run(f, doc); Assert.Equal("Imported", result.Status);
        Assert.Equal("90.00", result.OfficialTotal); Assert.Equal(5, await db.FinancialCreditCardInvoiceAdjustments.CountAsync());
        Assert.Empty(await db.FinancialCreditCardInvoicePayments.ToListAsync()); Assert.Empty(await db.FinancialTransactions.ToListAsync());
        var budget = FinancialBudget.Create(f.Workspace, new(2026, 10, 1), null, f.User, DateTimeOffset.UtcNow);
        db.AddRange(budget, FinancialBudgetTarget.Create(f.Workspace, budget.Id, "Synthetic", 100m, f.Category.Id, f.User, DateTimeOffset.UtcNow)); await db.SaveChangesAsync();
        var execution = await new FinancialBudgetExecutionRepository(db).GetAsync(f.Workspace, budget.Id, default);
        Assert.Equal(78m, Assert.Single(execution!.Targets).CreditCardCommitted); // 100 - 20 - 5 + 3; carried balances are not new consumption.
        Assert.Equal("90.00", (await new FinancialCreditCardPurchaseRepository(db).GetLimitAsync(f.Workspace, f.Card.Id, default))!.UsedLimit);
    }

    [Fact]
    public async Task OfficialInvoiceCanBePaidReplayPreservesPaymentAndChangedDocumentConflicts()
    {
        var f = await Arrange(); using var db = f.Db;
        var doc = await Document(f, [Item("SHOP", "10.00")], "10.00"); var result = await Run(f, doc);
        Assert.Equal("Imported", result.Status);
        var account = FinancialAccount.Create(f.Workspace, AccountDetails.TryCreate("Synthetic", null, "Checking", "PF", f.User, 100m, new(2026, 10, 1), out _)!, f.User, DateTimeOffset.UtcNow);
        db.Add(account); await db.SaveChangesAsync();
        var payment = await new FinancialCreditCardInvoicePaymentRepository(db).CreateAsync(f.Workspace, f.Card.Id, result.InvoiceId!.Value, Guid.NewGuid(), account.Id, new(2026, 10, 10), 10m, f.User, new(2026, 10, 10), DateTimeOffset.UtcNow, default);
        Assert.Equal(PaymentOperationResult.Success, payment.Result);
        Assert.Equal("AlreadyImported", (await Run(f, doc)).Status);
        var before = JsonSerializer.Serialize(await db.FinancialCreditCardInvoicePayments.AsNoTracking().ToListAsync());
        var replacement = await Document(f, [Item("SHOP", "12.00")], "12.00");
        Assert.Equal("Conflict", (await Run(f, replacement)).Status);
        Assert.Equal(before, JsonSerializer.Serialize(await db.FinancialCreditCardInvoicePayments.AsNoTracking().ToListAsync()));
        Assert.Single(await db.FinancialTransactions.ToListAsync()); Assert.Single(await db.FinancialCreditCardInvoiceConsolidations.ToListAsync());
    }

    [Fact]
    public async Task ManualCategorySurvivesChangedValueInSecondCopy()
    {
        var f = await Arrange(); using var db = f.Db;
        var corrected = FinancialCategory.Create(f.Workspace, "Saúde", CategoryType.Expense, f.User, DateTimeOffset.UtcNow); db.Add(corrected); await db.SaveChangesAsync();
        var doc = await Document(f, [Item("SHOP", "10.00")], "10.00");
        var analysis = InboxStatementAnalysisService.Read(doc.StatementAnalysisJson)!;
        doc.SetStatementAnalysis(JsonSerializer.Serialize(analysis with { Categories = [new("row0", 0, corrected.Id, corrected.Name, false, "High", "Manual")], ManualOverrides = new Dictionary<string, Guid> { ["row0"] = corrected.Id } }), true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(); Assert.Equal("Imported", (await Run(f, doc)).Status);
        var replacement = await Document(f, [Item("SHOP", "12.00")], "12.00");
        var next = InboxStatementAnalysisService.Read(replacement.StatementAnalysisJson)!;
        replacement.SetStatementAnalysis(JsonSerializer.Serialize(next with { Categories = [new("row0", 0, f.Category.Id, f.Category.Name, false, "High", "Existing")] }), true, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        Assert.Equal("NewVersionConsolidated", (await Run(f, replacement)).Status);
        Assert.Equal(corrected.Id, (await db.FinancialCreditCardInstallments.SingleAsync()).OfficialCategoryId);
        var third = await Document(f, [Item("SHOP", "14.00")], "14.00");
        var thirdAnalysis = InboxStatementAnalysisService.Read(third.StatementAnalysisJson)!;
        third.SetStatementAnalysis(JsonSerializer.Serialize(thirdAnalysis with { Categories = [new("row0", 0, f.Category.Id, f.Category.Name, false, "High", "Existing")] }), true, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        Assert.Equal("NewVersionConsolidated", (await Run(f, third)).Status);
        Assert.Equal(corrected.Id, (await db.FinancialCreditCardInstallments.SingleAsync()).OfficialCategoryId);
    }

    [Fact]
    public async Task DatabaseRejectsMutationOfOfficialCompositionAndUnprovenClosing()
    {
        var f = await Arrange(); using var db = f.Db;
        var doc = await Document(f, [Item("SHOP", "10.00")], "10.00"); Assert.Equal("Imported", (await Run(f, doc)).Status);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_credit_card_installments SET amount=11 WHERE workspace_id={f.Workspace}"));
        var immutableSource = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_document_inbox_items SET status='ReadyForReview' WHERE workspace_id={f.Workspace}"));
        Assert.Equal("OFFICIAL_DOCUMENT_IMMUTABLE", immutableSource.MessageText);
        var cycle = CreditCardInvoiceCalendarCalculator.ForInstallment(new(2027, 1, 1), 1, 5, 10);
        var invoice = FinancialCreditCardInvoice.Create(f.Workspace, f.Card.Id, cycle.Competence, cycle.CycleStartDate, cycle.ClosingDate, cycle.DueDate, DateTimeOffset.UtcNow);
        invoice.CloseOfficial(1, 0m, DateTimeOffset.UtcNow); db.Add(invoice);
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.SaveChangesAsync());
        Assert.Equal("23514", refused.SqlState); Assert.Contains("OFFICIAL_CONSOLIDATION_PROOF_REQUIRED", refused.MessageText);
    }

    [Fact]
    public async Task FailureDuringFinalSaveRollsBackCategoriesComponentsAndDocument()
    {
        var f = await Arrange(); using var db = f.Db;
        var doc = await Document(f, [Item("SHOP", "10.00")], "10.00", "Livros");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION synthetic_fail_official_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic final-save failure'; END $$;
            CREATE TRIGGER synthetic_fail BEFORE INSERT ON financial_credit_card_invoice_consolidations FOR EACH ROW EXECUTE FUNCTION synthetic_fail_official_commit();
            """);
        var result = await Run(f, doc);
        Assert.Equal("Conflict", result.Status); Assert.Equal("ConsolidationRolledBack", result.FailureCode);
        Assert.Single(await db.FinancialCategories.ToListAsync());
        Assert.Empty(await db.FinancialCreditCardInvoices.ToListAsync()); Assert.Empty(await db.FinancialCreditCardPurchases.ToListAsync());
        Assert.Empty(await db.FinancialCreditCardInvoiceConsolidations.ToListAsync());
        Assert.Equal(FinancialDocumentInboxStatus.ReadyForReview, (await db.FinancialDocumentInboxItems.SingleAsync()).Status);
    }

    [Fact]
    public async Task AuthorizedProcessingUsesStoredAnalysisAndIgnoresBrowserFinancialValues()
    {
        var f = await Arrange(); using var db = f.Db;
        var doc = await Document(f, [Item("SHOP", "10.00")], "10.00");
        // A trusted agent analysis previously validated against the stored PDF can be resumed without another provider call.
        var stored = InboxStatementAnalysisService.Read(doc.StatementAnalysisJson)!;
        var extraction = stored.Extraction! with { Mode = "AgentPrimary", PrimarySource = "Agent", Primary = stored.Extraction!.Primary! with { Header = stored.Extraction.Primary!.Header with { IdentityEvidence = "Bank 1234", IdentityPage = 1 } } };
        doc.SetStatementAnalysis(JsonSerializer.Serialize(stored with { Extraction = extraction }), true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        using var app = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IFinancialDocumentStorage>(f.Storage); services.AddSingleton(f.Options);
        }));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        var endpoint = $"/api/financial-workspaces/{f.Workspace}/financial-documents/inbox/{doc.Id}/statement-processing";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(endpoint, null)).StatusCode);
        using var noCsrf = new HttpRequestMessage(HttpMethod.Post, endpoint); noCsrf.Headers.Add("Cookie", f.Session);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(noCsrf)).StatusCode);
        var (cookie, csrf) = await GetCsrfAsync(f.Session);
        async Task<JsonElement> Process()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("Cookie", $"{f.Session}; {cookie}"); request.Headers.Add("X-CSRF-TOKEN", csrf);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["creditCardId"] = f.Card.Id.ToString(), ["officialTotal"] = "999999.00" });
            var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await ReadAsync<JsonElement>(response);
        }
        await using (var held = await new PostgresFinancialEmailCollectorLock(ConnectionString).TryAcquireAsync(f.Workspace, $"statement-analysis:{doc.Id:N}", default))
        {
            Assert.NotNull(held);
            Assert.Equal("Busy", (await Process()).GetProperty("consolidation").GetProperty("status").GetString());
            Assert.Empty(await db.FinancialCreditCardInvoiceConsolidations.ToListAsync());
        }
        var first = await Process(); Assert.Equal("Imported", first.GetProperty("consolidation").GetProperty("status").GetString());
        Assert.Equal("10.00", first.GetProperty("consolidation").GetProperty("officialTotal").GetString());
        Assert.Equal("AlreadyImported", (await Process()).GetProperty("consolidation").GetProperty("status").GetString());
        Assert.Single(await db.FinancialCreditCardInvoiceConsolidations.ToListAsync());
    }

    private async Task<Fixture> Arrange()
    {
        var session = await SignInAsync("official-owner"); var workspace = await CreateWorkspaceAsync(session); var db = Db();
        var target = CardEmailSearchAuthorization.AuthorizedPersonalId;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_workspaces SET id = {target} WHERE id = {workspace.Id}");
        var card = FinancialCreditCard.Create(target, new("Synthetic", "Bank", "1234", null, 1000m, 5, 10, null), workspace.OwnerUserId, DateTimeOffset.UtcNow);
        var category = FinancialCategory.Create(target, "Outros", CategoryType.Expense, workspace.OwnerUserId, DateTimeOffset.UtcNow);
        db.AddRange(card, category); await db.SaveChangesAsync();
        return new(db, target, workspace.OwnerUserId, card, category, new(), new() { Mode = CreditCardStatementAgentMode.Disabled }, session);
    }
    private static CreditCardStatementAgentItem Item(string description, string amount) => new(description, amount, "Debit", "Purchase", null, null, "CurrentInvoice", true, 1, $"{description} {amount}", "High", "2026-10-01", description, "Outros", "High");
    private static async Task<FinancialDocumentInboxItem> Document(Fixture f, CreditCardStatementAgentItem[] items, string total, string? proposal = null, string closing = "2026-10-05")
    {
        var bytes = Encoding.UTF8.GetBytes($"%PDF-1.4 synthetic {Guid.NewGuid()}"); var id = Guid.NewGuid(); var reference = id.ToString(); f.Storage.Files[reference] = bytes;
        var doc = FinancialDocumentInboxItem.Create(id, f.Workspace, FinancialDocumentSource.Manual, null, "synthetic.pdf", Convert.ToHexStringLower(SHA256.HashData(bytes)), "application/pdf", reference, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        f.Db.Add(doc); await f.Db.SaveChangesAsync();
        var catalog = await new StatementCategoryContextRepository(f.Db).ReadAsync(f.Workspace, f.Card.Id, doc.Id, default);
        var dto = (await new FinancialCreditCardRepository(f.Db).GetAsync(f.Workspace, f.Card.Id, default))!;
        var json = JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", new("Bank", "1234", "2026-10", closing, "2026-10-10", total), items), CreditCardStatementAgentSchema.Json);
        Assert.True(CreditCardStatementAgentEvaluator.TryEvaluate(json, "synthetic.pdf", 1, out var preview, out _));
        var categories = items.Select((_, index) => new StatementItemCategory($"row{index}", index, proposal is null ? f.Category.Id : null, proposal ?? f.Category.Name, proposal is not null, "High", proposal is null ? "Manual" : "Proposal")).ToArray();
        var analysis = new InboxStatementAnalysis("2.0", f.Card.Id, InboxStatementAnalysisService.ComputeInputHash(doc, dto, catalog, f.Options), "Reliable",
            new("Disabled", "Parser", preview, AgentExtractionAssessment.Reliable, false, null, null, null, null), categories, new Dictionary<string, Guid>());
        doc.SetStatementAnalysis(JsonSerializer.Serialize(analysis), true, DateTimeOffset.UtcNow); await f.Db.SaveChangesAsync(); return doc;
    }
    private static async Task<FinancialCreditCardPurchase> Purchase(Fixture f, string description, decimal amount, DateOnly? date = null, int count = 1)
    {
        var purchase = FinancialCreditCardPurchase.Create(f.Workspace, f.Card.Id, f.Category.Id, description, date ?? new(2026, 10, 1), amount, count, f.User, DateTimeOffset.UtcNow);
        foreach (var installment in purchase.Installments)
        {
            var cycle = CreditCardInvoiceCalendarCalculator.ForInstallment(purchase.PurchaseDate, installment.InstallmentNumber, 5, 10);
            var invoice = await f.Db.FinancialCreditCardInvoices.SingleOrDefaultAsync(i => i.WorkspaceId == f.Workspace && i.CreditCardId == f.Card.Id && i.Competence == cycle.Competence);
            if (invoice is null) { invoice = FinancialCreditCardInvoice.Create(f.Workspace, f.Card.Id, cycle.Competence, cycle.CycleStartDate, cycle.ClosingDate, cycle.DueDate, DateTimeOffset.UtcNow); f.Db.Add(invoice); }
            installment.AssignInvoice(invoice.Id);
        }
        f.Db.Add(purchase); await f.Db.SaveChangesAsync(); return purchase;
    }
    private static Task<OfficialStatementConsolidationResult> Run(Fixture f, FinancialDocumentInboxItem doc) => new CreditCardPdfConfirmationRepository(f.Db, f.Storage, f.Options).ConsolidateInboxAsync(f.Workspace, f.Card.Id, doc.Id, f.User, DateTimeOffset.UtcNow, default);
    private sealed record Fixture(Saldo360DbContext Db, Guid Workspace, Guid User, FinancialCreditCard Card, FinancialCategory Category, Storage Storage, CreditCardStatementAgentOptions Options, string Session);
    private sealed class Storage : IFinancialDocumentStorage
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public Task<Stream?> OpenReadAsync(string reference, CancellationToken ct) => Task.FromResult<Stream?>(Files.TryGetValue(reference, out var bytes) ? new MemoryStream(bytes) : null);
        public Task<string> SaveAsync(Guid workspace, Guid document, Stream content, CancellationToken ct) => throw new InvalidOperationException();
        public Task DeleteAsync(string reference, CancellationToken ct) => throw new InvalidOperationException();
    }
}

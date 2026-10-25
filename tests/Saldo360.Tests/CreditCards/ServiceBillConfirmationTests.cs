using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
public sealed class ServiceBillConfirmationServiceTests
{
    [Theory]
    [InlineData("ElectricityBill", "CEEE")]
    [InlineData("WaterBill", "DMAE")]
    [InlineData("TelecomBill", "Vivo")]
    public async Task ReliableUniqueMatchConfirmsServerAmount(string type, string issuer)
    {
        var occurrence = Guid.NewGuid(); var recurrence = Guid.NewGuid();
        var extraction = new ServiceBillExtractionOutput(new(type, issuer, "2026-10", "2026-10-20", "187.43", null, null, "High", "Deterministic", null), "Reliable");
        var match = new ServiceBillRecurringMatchResult(Guid.NewGuid(), type, "2026-10", "187.43", "Matched", occurrence, recurrence, [], [], "High");
        var store = new Store(new(ServiceBillConfirmationStoreStatus.Confirmed, occurrence, recurrence, "2026-10", 200m, 187.43m, DateTimeOffset.Parse("2026-10-19T12:00:00Z")));
        var service = new ServiceBillConfirmationService(new Preview(new(extraction, match)), store,
            new() { Enabled = true, ConfirmationEnabled = true }, TimeProvider.System);
        var result = await service.ConfirmAsync(Guid.NewGuid(), match.DocumentId, Guid.NewGuid(), null, CancellationToken.None);
        Assert.Equal("Confirmed", result.Result?.Status); Assert.Equal("200.00", result.Result?.ProjectedAmount);
        Assert.Equal("187.43", result.Result?.ConfirmedAmount); Assert.Equal("-12.57", result.Result?.Difference);
    }

    [Theory]
    [InlineData("NeedsReview", "Matched", "10.00")]
    [InlineData("Reliable", "Ambiguous", "10.00")]
    [InlineData("Reliable", "Matched", "0.00")]
    public async Task RejectsUnreliableAmbiguousOrZeroWithoutWriting(string assessment, string status, string amount)
    {
        var extraction = new ServiceBillExtractionOutput(new("ElectricityBill", "CEEE", "2026-10", null, amount, null, null, "High", "Deterministic", null), assessment);
        var match = new ServiceBillRecurringMatchResult(Guid.NewGuid(), "ElectricityBill", "2026-10", amount, status, Guid.NewGuid(), Guid.NewGuid(), [], [], "High");
        var store = new Store(new(ServiceBillConfirmationStoreStatus.Confirmed));
        var result = await new ServiceBillConfirmationService(new Preview(new(extraction, match)), store,
            new() { Enabled = true, ConfirmationEnabled = true }, TimeProvider.System)
            .ConfirmAsync(Guid.NewGuid(), match.DocumentId, Guid.NewGuid(), null, CancellationToken.None);
        Assert.Equal(FinancialDocumentClassificationAccess.Invalid, result.Access); Assert.Equal(0, store.Calls);
    }

    private sealed class Preview(ServiceBillRecurringMatchPreview value) : IServiceBillRecurringMatchPreviewService
    {
        public Task<(FinancialDocumentClassificationAccess Access, ServiceBillRecurringMatchPreview? Preview, IDictionary<string, string[]>? Errors)> PreviewAsync(Guid workspaceId, Guid id, Guid userId, string? password, CancellationToken cancellationToken) => Task.FromResult((FinancialDocumentClassificationAccess.Allowed, (ServiceBillRecurringMatchPreview?)value, (IDictionary<string, string[]>?)null));
    }
    private sealed class Store(ServiceBillConfirmationStoreResult value) : IServiceBillConfirmationRepository
    {
        public int Calls { get; private set; }
        public Task<ServiceBillConfirmationStoreResult> ConfirmAsync(Guid workspaceId, Guid documentId, Guid userId, Guid occurrenceId, Guid recurrenceId, string competence, decimal confirmedAmount, DateTimeOffset now, CancellationToken cancellationToken) { Calls++; return Task.FromResult(value); }
    }
}

[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
public sealed class ServiceBillConfirmationPersistenceTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task ConcurrentDoubleSubmitConfirmsOnceAndReturnsIdempotentResult()
    {
        var owner = await SignInAsync("service-bill-double-submit-owner");
        var family = await CreateWorkspaceAsync(owner, "Família Double Submit");
        var account = await CreateAsync(owner, $"/api/financial-workspaces/{family.Id}/accounts", new { name = "Conta", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = "2026-09-01" });
        var category = await CreateAsync(owner, $"/api/financial-workspaces/{family.Id}/categories", new { name = "Água", type = "Expense" });
        var recurrence = await CreateAsync(owner, $"/api/financial-workspaces/{family.Id}/recurrences", new { accountId = account, categoryId = category, type = "Expense", description = "DMAE", amount = "150.00", isEstimated = true, frequency = "Monthly", interval = 1, startDate = "2026-10-20", dayOfMonth = 20 });
        var materialized = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/recurrences/{recurrence}/materialize", owner, new { from = "2026-10-01", through = "2026-10-31" });
        using var body = JsonDocument.Parse(await materialized.Content.ReadAsStringAsync());
        var occurrence = body.RootElement.GetProperty("items")[0].GetProperty("plannedTransactionId").GetGuid();
        Guid userId; var documentId = Guid.NewGuid();
        await using (var setup = Db())
        {
            userId = await setup.FinancialWorkspaces.Where(x => x.Id == family.Id).Select(w => w.OwnerUserId).SingleAsync();
            setup.FinancialDocumentInboxItems.Add(FinancialDocumentInboxItem.Create(documentId, family.Id,
                FinancialDocumentSource.Manual, null, "agua.pdf", new string('d', 64), "application/pdf",
                "test/service-bill-double-submit", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await setup.SaveChangesAsync();
        }

        await using var firstDb = Db();
        await using var secondDb = Db();
        var first = new ServiceBillConfirmationRepository(firstDb).ConfirmAsync(family.Id, documentId, userId,
            occurrence, recurrence, "2026-10", 147.25m, DateTimeOffset.UtcNow, CancellationToken.None);
        var second = new ServiceBillConfirmationRepository(secondDb).ConfirmAsync(family.Id, documentId, userId,
            occurrence, recurrence, "2026-10", 147.25m, DateTimeOffset.UtcNow, CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == ServiceBillConfirmationStoreStatus.Confirmed);
        Assert.Single(results, result => result.Status == ServiceBillConfirmationStoreStatus.AlreadyConfirmed);
        await using var verification = Db();
        Assert.Equal(147.25m, (await verification.FinancialPlannedTransactions.FindAsync(occurrence))!.Amount);
        Assert.Single(await verification.FinancialDocumentInboxItems.Where(x => x.ServiceBillOccurrenceId == occurrence).ToListAsync());
        Assert.Empty(await verification.FinancialTransactions.ToListAsync());
    }

    [Fact]
    public async Task ConfirmationChangesOnlyMonthlyOccurrenceAndIsIdempotent()
    {
        var owner = await SignInAsync("service-bill-confirm-owner");
        var family = await CreateWorkspaceAsync(owner, "Família Contas");
        var account = await CreateAsync(owner, $"/api/financial-workspaces/{family.Id}/accounts", new { name = "Conta", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = "2026-09-01" });
        var category = await CreateAsync(owner, $"/api/financial-workspaces/{family.Id}/categories", new { name = "Energia", type = "Expense" });
        var recurrence = await CreateAsync(owner, $"/api/financial-workspaces/{family.Id}/recurrences", new { accountId = account, categoryId = category, type = "Expense", description = "CEEE", amount = "200.00", isEstimated = true, frequency = "Monthly", interval = 1, startDate = "2026-10-20", dayOfMonth = 20 });
        var materialized = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/recurrences/{recurrence}/materialize", owner, new { from = "2026-10-01", through = "2026-11-30" });
        using var body = JsonDocument.Parse(await materialized.Content.ReadAsStringAsync());
        var october = body.RootElement.GetProperty("items")[0].GetProperty("plannedTransactionId").GetGuid();
        var november = body.RootElement.GetProperty("items")[1].GetProperty("plannedTransactionId").GetGuid();
        Guid userId; Guid documentId = Guid.NewGuid();
        await using (var db = Db())
        {
            userId = await db.FinancialWorkspaces.Where(x => x.Id == family.Id).Select(w => w.OwnerUserId).SingleAsync();
            db.FinancialDocumentInboxItems.Add(FinancialDocumentInboxItem.Create(documentId, family.Id, FinancialDocumentSource.Manual,
                null, "energia.pdf", new string('a', 64), "application/pdf", "test/service-bill", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        await using (var db = Db())
        {
            var repository = new ServiceBillConfirmationRepository(db);
            var first = await repository.ConfirmAsync(family.Id, documentId, userId, october, recurrence, "2026-10", 187.43m, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(ServiceBillConfirmationStoreStatus.Confirmed, first.Status);
        }
        await using (var db = Db())
        {
            Assert.Equal(187.43m, (await db.FinancialPlannedTransactions.FindAsync(october))!.Amount);
            Assert.Equal(200m, (await db.FinancialPlannedTransactions.FindAsync(november))!.Amount);
            Assert.Equal(200m, (await db.FinancialRecurrences.FindAsync(recurrence))!.Amount);
            Assert.Empty(db.FinancialTransactions);
            var repository = new ServiceBillConfirmationRepository(db);
            var repeated = await repository.ConfirmAsync(family.Id, documentId, userId, october, recurrence, "2026-10", 187.43m, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(ServiceBillConfirmationStoreStatus.AlreadyConfirmed, repeated.Status);
        }
        var secondDocumentId = Guid.NewGuid();
        await using (var db = Db())
        {
            db.FinancialDocumentInboxItems.Add(FinancialDocumentInboxItem.Create(secondDocumentId, family.Id, FinancialDocumentSource.Manual,
                null, "segunda-via.pdf", new string('b', 64), "application/pdf", "test/service-bill-2", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            var conflict = await new ServiceBillConfirmationRepository(db).ConfirmAsync(family.Id, secondDocumentId, userId,
                october, recurrence, "2026-10", 187.43m, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(ServiceBillConfirmationStoreStatus.Conflict, conflict.Status);
        }
        var calendar = await SendAsync(HttpMethod.Get,
            $"/api/financial-workspaces/{family.Id}/financial-calendar?from=2026-10-01&through=2026-11-30", owner);
        using var calendarBody = JsonDocument.Parse(await calendar.Content.ReadAsStringAsync());
        var values = calendarBody.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("amount").GetString()).ToArray();
        Assert.Equal(new string?[] { "187.43", "200.00" }, values);
        var editConfirmed = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/financial-workspaces/{family.Id}/planned-transactions/{october}", owner,
            new { accountId = account, categoryId = category, type = "Expense", description = "CEEE", amount = "199.00", dueDate = "2026-10-20" });
        Assert.Equal(HttpStatusCode.Conflict, editConfirmed.StatusCode);
        var inbox = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/financial-documents/inbox", owner);
        using var inboxBody = JsonDocument.Parse(await inbox.Content.ReadAsStringAsync());
        var confirmedDocument = inboxBody.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == documentId);
        var confirmation = confirmedDocument.GetProperty("serviceBillConfirmation");
        Assert.Equal("200.00", confirmation.GetProperty("projectedAmount").GetString());
        Assert.Equal("187.43", confirmation.GetProperty("confirmedAmount").GetString());
        Assert.Equal("-12.57", confirmation.GetProperty("difference").GetString());
        var accountHash = ServiceReferenceIdentity.Hash("UC-123456")!;
        await using (var db = Db())
        {
            var bindings = new ServiceAccountBindingRepository(db);
            var created = await bindings.CreateAsync(family.Id, documentId, userId, "ElectricityBill", "CEEE",
                accountHash, "**3456", null, null, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(ServiceAccountBindingStoreStatus.Created, created.Status);
        }
        await using (var db = Db())
        {
            var bindings = new ServiceAccountBindingRepository(db);
            var repeated = await bindings.CreateAsync(family.Id, documentId, userId, "ElectricityBill", "CEEE",
                accountHash, "**3456", null, null, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(ServiceAccountBindingStoreStatus.AlreadyExists, repeated.Status);
            var stored = await db.ServiceAccountBindings.SingleAsync();
            Assert.Equal(accountHash, stored.AccountReferenceHash); Assert.Equal("**3456", stored.AccountReferenceMasked);
            Assert.DoesNotContain("123456", stored.AccountReferenceMasked!);
            var candidates = await new ServiceBillRecurringCandidateRepository(db).ListAsync(family.Id, "2026-11",
                "ElectricityBill", accountHash, null, CancellationToken.None);
            Assert.True(Assert.Single(candidates).AccountBindingMatch);
        }
        var otherRecurrence = await CreateAsync(owner, $"/api/financial-workspaces/{family.Id}/recurrences", new { accountId = account, categoryId = category, type = "Expense", description = "Energia garagem", amount = "80.00", isEstimated = true, frequency = "Monthly", interval = 1, startDate = "2026-10-25", dayOfMonth = 25 });
        var otherMaterialized = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/recurrences/{otherRecurrence}/materialize", owner, new { from = "2026-10-01", through = "2026-10-31" });
        using var otherBody = JsonDocument.Parse(await otherMaterialized.Content.ReadAsStringAsync());
        var otherOccurrence = otherBody.RootElement.GetProperty("items")[0].GetProperty("plannedTransactionId").GetGuid();
        var otherDocument = Guid.NewGuid();
        await using (var db = Db())
        {
            db.FinancialDocumentInboxItems.Add(FinancialDocumentInboxItem.Create(otherDocument, family.Id, FinancialDocumentSource.Manual,
                null, "outra-conta.pdf", new string('c', 64), "application/pdf", "test/service-bill-3", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            Assert.Equal(ServiceBillConfirmationStoreStatus.Confirmed, (await new ServiceBillConfirmationRepository(db).ConfirmAsync(
                family.Id, otherDocument, userId, otherOccurrence, otherRecurrence, "2026-10", 75m, DateTimeOffset.UtcNow, CancellationToken.None)).Status);
        }
        await using (var db = Db())
        {
            var conflict = await new ServiceAccountBindingRepository(db).CreateAsync(family.Id, otherDocument, userId,
                "ElectricityBill", "CEEE", accountHash, "**3456", null, null, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(ServiceAccountBindingStoreStatus.Conflict, conflict.Status);
        }
    }

    private async Task<Guid> CreateAsync(string session, string url, object value)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, url, session, value); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return body.RootElement.GetProperty("id").GetGuid();
    }
}

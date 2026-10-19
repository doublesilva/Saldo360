using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.CreditCards;

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
        public Task<(FinancialDocumentClassificationAccess Access, ServiceBillRecurringMatchPreview? Preview, IDictionary<string, string[]>? Errors)> PreviewAsync(Guid familyId, Guid id, Guid userId, string? password, CancellationToken cancellationToken) => Task.FromResult((FinancialDocumentClassificationAccess.Allowed, (ServiceBillRecurringMatchPreview?)value, (IDictionary<string, string[]>?)null));
    }
    private sealed class Store(ServiceBillConfirmationStoreResult value) : IServiceBillConfirmationRepository
    {
        public int Calls { get; private set; }
        public Task<ServiceBillConfirmationStoreResult> ConfirmAsync(Guid familyId, Guid documentId, Guid userId, Guid occurrenceId, Guid recurrenceId, string competence, decimal confirmedAmount, DateTimeOffset now, CancellationToken cancellationToken) { Calls++; return Task.FromResult(value); }
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ServiceBillConfirmationPersistenceTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task ConfirmationChangesOnlyMonthlyOccurrenceAndIsIdempotent()
    {
        var owner = await SignInAsync("service-bill-confirm-owner");
        var family = await CreateFamilyAsync(owner, "Família Contas");
        var account = await CreateAsync(owner, $"/api/families/{family.Id}/accounts", new { name = "Conta", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = "2026-09-01" });
        var category = await CreateAsync(owner, $"/api/families/{family.Id}/categories", new { name = "Energia", type = "Expense" });
        var recurrence = await CreateAsync(owner, $"/api/families/{family.Id}/recurrences", new { accountId = account, categoryId = category, type = "Expense", description = "CEEE", amount = "200.00", isEstimated = true, frequency = "Monthly", interval = 1, startDate = "2026-10-20", dayOfMonth = 20 });
        var materialized = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/recurrences/{recurrence}/materialize", owner, new { from = "2026-10-01", through = "2026-11-30" });
        using var body = JsonDocument.Parse(await materialized.Content.ReadAsStringAsync());
        var october = body.RootElement.GetProperty("items")[0].GetProperty("plannedTransactionId").GetGuid();
        var november = body.RootElement.GetProperty("items")[1].GetProperty("plannedTransactionId").GetGuid();
        Guid userId; Guid documentId = Guid.NewGuid();
        await using (var db = Db())
        {
            userId = await db.FamilyMembers.Where(x => x.FamilyId == family.Id).Select(x => x.UserId).SingleAsync();
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
            $"/api/families/{family.Id}/financial-calendar?from=2026-10-01&through=2026-11-30", owner);
        using var calendarBody = JsonDocument.Parse(await calendar.Content.ReadAsStringAsync());
        var values = calendarBody.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("amount").GetString()).ToArray();
        Assert.Equal(new string?[] { "187.43", "200.00" }, values);
        var editConfirmed = await SendWithCsrfAsync(HttpMethod.Put,
            $"/api/families/{family.Id}/planned-transactions/{october}", owner,
            new { accountId = account, categoryId = category, type = "Expense", description = "CEEE", amount = "199.00", dueDate = "2026-10-20" });
        Assert.Equal(HttpStatusCode.Conflict, editConfirmed.StatusCode);
        var inbox = await SendAsync(HttpMethod.Get, $"/api/families/{family.Id}/financial-documents/inbox", owner);
        using var inboxBody = JsonDocument.Parse(await inbox.Content.ReadAsStringAsync());
        var confirmedDocument = inboxBody.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == documentId);
        var confirmation = confirmedDocument.GetProperty("serviceBillConfirmation");
        Assert.Equal("200.00", confirmation.GetProperty("projectedAmount").GetString());
        Assert.Equal("187.43", confirmation.GetProperty("confirmedAmount").GetString());
        Assert.Equal("-12.57", confirmation.GetProperty("difference").GetString());
    }

    private async Task<Guid> CreateAsync(string session, string url, object value)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, url, session, value); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return body.RootElement.GetProperty("id").GetGuid();
    }
}

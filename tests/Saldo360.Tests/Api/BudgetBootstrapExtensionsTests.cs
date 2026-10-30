using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Bootstrap;
using Saldo360.Domain.Budgets;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Area", "Admin")]
[Trait("Area", "Cards")]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class BudgetBootstrapExtensionsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static BootstrapSource Source(string reference, string? metadata = null) => new("BudgetSpreadsheet", "Synthetic test workbook", reference, metadata);
    private async Task<T> Service<T>(Func<IFinancialBootstrapExtensions, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IFinancialBootstrapExtensions>());
    }
    private async Task<(string Session, WorkspaceDto Workspace, Guid Category, Guid Card, Guid Account)> Setup()
    {
        var session = await SignInAsync(); var w = await CreateWorkspaceAsync(session);
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/accounts", session,
            new { name = "Synthetic account", type = "Checking", financialScope = "PF", openingBalance = "1000.00", openingBalanceDate = "2026-01-01" });
        var accountId = (await ReadAsync<JsonElement>(account)).GetProperty("id").GetGuid();
        var category = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/categories", session, new { name = "Synthetic expense", type = "Expense" });
        var card = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/credit-cards", session,
            new { name = "Synthetic card", issuer = "Test", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = accountId });
        Assert.Equal(HttpStatusCode.Created, card.StatusCode);
        return (session, w, (await ReadAsync<JsonElement>(category)).GetProperty("id").GetGuid(), (await ReadAsync<JsonElement>(card)).GetProperty("id").GetGuid(), accountId);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task CardRecurrence_MaterializesNormalChargesOnce_WithoutBankRowsOrPayments()
    {
        var (session, w, category, card, _) = await Setup();
        var url = $"/api/financial-workspaces/{w.Id}/recurrences";
        var response = await SendWithCsrfAsync(HttpMethod.Post, url, session, new { creditCardId = card, categoryId = category, type = "Expense", description = "Synthetic subscription", amount = "19.90", frequency = "Monthly", interval = 1, startDate = "2026-10-10", endDate = "2026-12-31", dayOfMonth = 10 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var record = await ReadAsync<JsonElement>(response); var id = record.GetProperty("id").GetGuid();
        Assert.Equal(card, record.GetProperty("creditCardId").GetGuid()); Assert.Equal(JsonValueKind.Null, record.GetProperty("accountId").ValueKind);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await SendWithCsrfAsync(HttpMethod.Post, $"{url}/{id}/materialize", session, new { from = "2026-10-01", through = "2027-01-31" });
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var json = await ReadAsync<JsonElement>(result); Assert.Equal(attempt == 0 ? 3 : 0, json.GetProperty("createdCount").GetInt32());
            Assert.All(json.GetProperty("items").EnumerateArray(), item => { Assert.Equal(JsonValueKind.Null, item.GetProperty("plannedTransactionId").ValueKind); Assert.NotEqual(Guid.Empty, item.GetProperty("creditCardPurchaseId").GetGuid()); });
        }
        var view = await SendAsync(HttpMethod.Get, $"{url}/{id}/materializations?from=2026-10-01&through=2027-01-31", session);
        Assert.Equal(3, (await ReadAsync<JsonElement>(view)).GetArrayLength());
        await using var db = Db();
        Assert.Equal(3, await db.FinancialCreditCardPurchases.CountAsync());
        Assert.Equal(new[] { "2026-11", "2026-12", "2027-01" }, await db.FinancialCreditCardInvoices.OrderBy(x => x.DueDate).Select(x => x.Competence).ToArrayAsync());
        Assert.Equal(3, await db.FinancialRecurrenceMaterializations.CountAsync());
        Assert.Empty(db.FinancialPlannedTransactions); Assert.Empty(db.FinancialTransactions); Assert.Empty(db.FinancialCreditCardInvoicePayments); Assert.Empty(db.Families);
    }

    [Trait("Category", "CORE")]
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Recurrence_RejectsBothOrNeitherDestinations(bool accountDestination, bool cardDestination)
    {
        var (session, w, category, card, account) = await Setup();
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/recurrences", session,
            new { accountId = accountDestination ? (Guid?)account : null, creditCardId = cardDestination ? (Guid?)card : null, categoryId = category, type = "Expense", description = "Invalid", amount = "10.00", frequency = "Monthly", interval = 1, startDate = "2026-10-01" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = Db(); Assert.Empty(db.FinancialRecurrences);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task AggregateTargets_AreIndependentUniqueSnapshots_KeepRawRecoveryMetadata()
    {
        var (_, w, _, _, _) = await Setup();
        var month = new DateOnly(2026, 10, 1);
        var values = new[] { (MonthlyAggregateTargetType.FixedExpenses, 7000m), (MonthlyAggregateTargetType.CreditCards, 5000m), (MonthlyAggregateTargetType.TotalExpenses, 12000m), (MonthlyAggregateTargetType.RecoveryAmount, 1270.38m) };
        foreach (var (type, amount) in values)
        {
            var input = new AggregateTargetInput(month, type, amount, Source(type.ToString(), type == MonthlyAggregateTargetType.RecoveryAmount ? "{\"sourceRaw\":1270.3759999999995}" : null));
            Assert.Equal(BootstrapDisposition.Created, (await Service(s => s.SaveTargetAsync(w.Id, w.OwnerUserId, input, default))).Disposition);
            Assert.Equal(BootstrapDisposition.AlreadyImported, (await Service(s => s.SaveTargetAsync(w.Id, w.OwnerUserId, input, default))).Disposition);
        }
        var conflict = await Service(s => s.SaveTargetAsync(w.Id, w.OwnerUserId, new(month, MonthlyAggregateTargetType.FixedExpenses, 7100m, Source("FixedExpenses")), default));
        Assert.Equal(BootstrapDisposition.Conflict, conflict.Disposition);
        var duplicate = await Service(s => s.SaveTargetAsync(w.Id, w.OwnerUserId, new(month, MonthlyAggregateTargetType.FixedExpenses, 7000m, Source("different-source-row")), default));
        Assert.Equal(BootstrapDisposition.Conflict, duplicate.Disposition);
        var targets = await Service(s => s.ListTargetsAsync(w.Id, w.OwnerUserId, default));
        Assert.Equal(12000m, targets!.Single(x => x.TargetType == MonthlyAggregateTargetType.TotalExpenses).Amount);
        await using var db = Db(); Assert.Equal(4, await db.FinancialMonthlyAggregateTargets.CountAsync()); Assert.Empty(db.FinancialBudgetTargets);
        Assert.Contains("1270.3759999999995", (await db.FinancialBootstrapProvenance.SingleAsync(x => x.ExternalReference == "RecoveryAmount")).MetadataJson!);
        // A duplicate insert is also rejected by PostgreSQL, not only the service precheck.
        db.FinancialMonthlyAggregateTargets.Add(FinancialMonthlyAggregateTarget.Create(w.Id, month, MonthlyAggregateTargetType.TotalExpenses, 1m, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task KnownSchedule_PreservesCentsNumbersAndUnknowns_NoInvoiceOrPurchase_Idempotent()
    {
        var (_, w, category, card, _) = await Setup();
        var schedule = new[] { 111.78m, 111.76m, 111.76m };
        for (var i = 0; i < schedule.Length; i++)
        {
            var input = new KnownCardInstallmentInput(card, category, "Synthetic explicit schedule", i + 1, 3, new DateOnly(2026, 10, 1).AddMonths(i), null, schedule[i], Source($"schedule/{i + 1}"));
            var result = await Service(s => s.SaveKnownInstallmentAsync(w.Id, w.OwnerUserId, input, default));
            Assert.Equal(BootstrapDisposition.Created, result.Disposition);
            Assert.Equal(result.EntityId, (await Service(s => s.SaveKnownInstallmentAsync(w.Id, w.OwnerUserId, input, default))).EntityId);
            Assert.Equal(BootstrapDisposition.Conflict, (await Service(s => s.SaveKnownInstallmentAsync(w.Id, w.OwnerUserId, input with { Amount = input.Amount + 0.01m }, default))).Disposition);
        }
        var unknown = new KnownCardInstallmentInput(card, null, "Unknown numbering", null, null, new(2027, 1, 1), new(2027, 1, 12), 50m, Source("unknown"));
        Assert.Equal(BootstrapDisposition.Created, (await Service(s => s.SaveKnownInstallmentAsync(w.Id, w.OwnerUserId, unknown, default))).Disposition);
        await using var db = Db(); var rows = await db.FinancialCreditCardImportedInstallments.OrderBy(x => x.Competence).ToListAsync();
        Assert.Equal(schedule, rows.Take(3).Select(x => x.Amount)); Assert.Equal(new int?[] { 1, 2, 3, null }, rows.Select(x => x.InstallmentNumber));
        Assert.All(rows, x => { Assert.Null(x.TransactionDate); Assert.Null(x.InvoiceId); Assert.Null(x.ImportId); });
        Assert.Equal(new DateOnly(2026, 10, 10), rows[0].DueDate); Assert.Equal(new DateOnly(2027, 1, 12), rows[3].DueDate);
        Assert.Empty(db.FinancialCreditCardInvoices); Assert.Empty(db.FinancialCreditCardPurchases); Assert.Empty(db.FinancialTransactions); Assert.Empty(db.FinancialPlannedTransactions);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task KnownObligation_ParticipatesInLimitAccountProjectionAndCategoryExecution_WithoutInvoice()
    {
        var (session, w, category, card, account) = await Setup();
        var input = new KnownCardInstallmentInput(card, category, "Known", 7, 10, new(2026, 11, 1), null, 111.78m, Source("known"));
        Assert.Equal(BootstrapDisposition.Created, (await Service(s => s.SaveKnownInstallmentAsync(w.Id, w.OwnerUserId, input, default))).Disposition);
        var projection = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{w.Id}/accounts/{account}/projection?through=2027-01-01", session);
        Assert.Equal(HttpStatusCode.OK, projection.StatusCode);
        Assert.Equal("111.78", (await ReadAsync<JsonElement>(projection)).GetProperty("creditCardOutstanding").GetString());
        var limit = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{w.Id}/credit-cards/{card}/limit", session);
        Assert.Equal(HttpStatusCode.OK, limit.StatusCode); Assert.Equal("111.78", (await ReadAsync<JsonElement>(limit)).GetProperty("usedLimit").GetString());
        var budgetResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/budgets", session, new { competence = "2026-11" });
        Assert.Equal(HttpStatusCode.Created, budgetResponse.StatusCode);
        var budgetId = (await ReadAsync<JsonElement>(budgetResponse)).GetProperty("id").GetGuid();
        var targetResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/budgets/{budgetId}/targets", session, new { name = "Synthetic category", categoryId = category, plannedAmount = "1000.00", groupingType = "Category" });
        Assert.Equal(HttpStatusCode.Created, targetResponse.StatusCode);
        using var scope = Factory.Services.CreateScope();
        var execution = await scope.ServiceProvider.GetRequiredService<Saldo360.Application.Budgets.IFinancialBudgetExecutionRepository>().GetAsync(w.Id, budgetId, default);
        Assert.Equal(111.78m, execution!.Targets.Single().CreditCardCommitted);
        var balances = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{w.Id}/financial-calendar/balances?from=2026-11-01&through=2027-01-01", session);
        Assert.Equal(HttpStatusCode.OK, balances.StatusCode);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task ExtensionAuthorization_RejectsOtherOwnerAndCrossWorkspaceCard()
    {
        var (_, w, category, card, _) = await Setup(); var b = await SignInAsync("other-user"); var wb = await CreateWorkspaceAsync(b);
        var input = new KnownCardInstallmentInput(card, category, "Known", null, null, new(2026, 11, 1), null, 1m, Source("same"));
        Assert.Equal(BootstrapDisposition.Invalid, (await Service(s => s.SaveKnownInstallmentAsync(w.Id, wb.OwnerUserId, input, default))).Disposition);
        Assert.Equal(BootstrapDisposition.Invalid, (await Service(s => s.SaveKnownInstallmentAsync(wb.Id, wb.OwnerUserId, input, default))).Disposition);
        Assert.Null(await Service(s => s.ListTargetsAsync(w.Id, wb.OwnerUserId, default)));
        await using var db = Db(); Assert.Empty(db.FinancialCreditCardImportedInstallments); Assert.Empty(db.FinancialBootstrapProvenance); Assert.Empty(db.Families);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task ProvenanceInspectionAndDryRunContracts_AreZeroWrite()
    {
        var (_, w, _, _, _) = await Setup();
        var probe = await Service(s => s.InspectProvenanceAsync(w.Id, w.OwnerUserId, Source("probe"), new string('a', 64), default));
        Assert.Equal(BootstrapDisposition.Created, probe.Disposition);
        var report = new BootstrapDryRunReport(w.Id, [new("KnownInstallment", "probe", probe.Disposition), new("Purchase", "control-remote", BootstrapDisposition.Unmapped, "NeedsReview")]);
        Assert.True(report.ZeroWrite); Assert.False(report.CanExecute); Assert.Equal(1, report.Counts[BootstrapDisposition.Unmapped]);
        await using var db = Db(); Assert.Empty(db.FinancialBootstrapProvenance); Assert.Empty(db.FinancialMonthlyAggregateTargets); Assert.Empty(db.FinancialCreditCardImportedInstallments);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task ResetInventory_CoversNewTablesAndCardMaterializations()
    {
        var (session, w, category, card, _) = await Setup();
        await Service(s => s.SaveTargetAsync(w.Id, w.OwnerUserId, new(new(2026, 10, 1), MonthlyAggregateTargetType.TotalExpenses, 12000m, Source("target")), default));
        var recurrence = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/recurrences", session, new { creditCardId = card, categoryId = category, type = "Expense", description = "Synthetic", amount = "10.00", frequency = "Monthly", interval = 1, startDate = "2026-10-01", occurrenceCount = 1 });
        Assert.Equal(HttpStatusCode.Created, recurrence.StatusCode);
        var rid = (await ReadAsync<JsonElement>(recurrence)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/recurrences/{rid}/materialize", session, new { from = "2026-10-01", through = "2026-10-31" })).StatusCode);
        var preview = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{w.Id}/financial-reset/preview", session);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/financial-reset", session, new { confirmation = "APAGAR ORÇAMENTO" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = Db(); Assert.Empty(db.FinancialBootstrapProvenance); Assert.Empty(db.FinancialMonthlyAggregateTargets); Assert.Empty(db.FinancialRecurrenceMaterializations); Assert.Empty(db.FinancialCreditCardPurchases); Assert.Single(db.FinancialWorkspaces);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task KnownInstallment_CanAttachOnlyToCompatibleRealInvoice_WithoutInventingPurchaseDate()
    {
        var (session, w, category, card, _) = await Setup();
        var input = new KnownCardInstallmentInput(card, category, "Explicit historical", null, null, new(2026, 11, 1), null, 111.78m, Source("known"));
        var item = await Service(s => s.SaveKnownInstallmentAsync(w.Id, w.OwnerUserId, input, default));
        await using (var db = Db()) Assert.Empty(db.FinancialCreditCardInvoices);
        // A real purchase legitimately creates its normal invoice; the historical registration did not.
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/credit-cards/{card}/purchases", session,
            new { description = "Actual complete purchase", purchaseDate = "2026-10-10", totalAmount = "20.00", installmentCount = 1, categoryId = category })).StatusCode);
        Guid invoice;
        await using (var db = Db()) invoice = await db.FinancialCreditCardInvoices.Select(x => x.Id).SingleAsync();
        Assert.Equal(BootstrapDisposition.Created, (await Service(s => s.AttachKnownInvoiceAsync(w.Id, w.OwnerUserId, item.EntityId!.Value, invoice, default))).Disposition);
        Assert.Equal(BootstrapDisposition.AlreadyImported, (await Service(s => s.AttachKnownInvoiceAsync(w.Id, w.OwnerUserId, item.EntityId!.Value, invoice, default))).Disposition);
        var response = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{w.Id}/credit-cards/{card}/invoices/{invoice}", session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("131.78", (await ReadAsync<JsonElement>(response)).GetProperty("totalAmount").GetString());
        await using var check = Db(); Assert.Null((await check.FinancialCreditCardImportedInstallments.SingleAsync()).TransactionDate);
        Assert.Empty(check.FinancialTransactions); Assert.Empty(check.FinancialCreditCardInvoicePayments);
    }

    [Trait("Category", "CORE")]
    [Fact]
    public async Task ConcurrentSameSource_CreatesOneRecord_AndSourceIsIsolatedByWorkspace()
    {
        var (_, w, _, _, _) = await Setup();
        var input = new AggregateTargetInput(new(2026, 11, 1), MonthlyAggregateTargetType.TotalExpenses, 12000m, Source("target"));
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Service(s => s.SaveTargetAsync(w.Id, w.OwnerUserId, input, default))));
        Assert.Single(results, x => x.Disposition == BootstrapDisposition.Created); Assert.Equal(4, results.Count(x => x.Disposition == BootstrapDisposition.AlreadyImported));
        var b = await SignInAsync("other-owner"); var wb = await CreateWorkspaceAsync(b);
        Assert.Equal(BootstrapDisposition.Created, (await Service(s => s.SaveTargetAsync(wb.Id, wb.OwnerUserId, input, default))).Disposition);
        await using var db = Db(); Assert.Equal(2, await db.FinancialBootstrapProvenance.CountAsync()); Assert.Equal(2, await db.FinancialMonthlyAggregateTargets.CountAsync());
    }

    [Trait("Category", "INFRA")]
    [Fact]
    public async Task Migration_PreservesPreviouslyValidImportRowOne_WithoutBackfill()
    {
        var (_, w, _, card, _) = await Setup();
        var now = DateTimeOffset.UtcNow;
        var invoice = Saldo360.Domain.CreditCards.FinancialCreditCardInvoice.Create(w.Id, card, "2026-11", new(2026, 10, 6), new(2026, 11, 5), new(2026, 11, 10), now);
        var id = Guid.NewGuid(); var batch = Guid.NewGuid(); var date = new DateOnly(2026, 10, 10);
        await using (var db = Db())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261020232559_AddFinancialWorkspaceResetCoordination");
            // Insert through the historical schema: the current EF model now has official-state columns absent here.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO financial_credit_card_invoices (id,workspace_id,credit_card_id,competence,cycle_start_date,closing_date,due_date,created_at) VALUES ({invoice.Id},{w.Id},{card},{invoice.Competence},{invoice.CycleStartDate},{invoice.ClosingDate},{invoice.DueDate},{now})");
            // Exercise the previous schema, not the current model. Row 1 was valid in that schema.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO financial_credit_card_imported_installments (id,workspace_id,credit_card_id,import_id,row_number,invoice_id,transaction_date,description,amount,installment_number,installment_count,is_historical,status,created_at,created_by_user_id) VALUES ({id},{w.Id},{card},{batch},1,{invoice.Id},{date},'Synthetic previous import',123.45,7,10,true,'Active',{now},{w.OwnerUserId})");
            await db.Database.MigrateAsync();
        }
        await using var check = Db(); var row = await check.FinancialCreditCardImportedInstallments.SingleAsync();
        Assert.Equal(id, row.Id); Assert.Equal(1, row.RowNumber); Assert.Equal(123.45m, row.Amount);
        Assert.Equal(7, row.InstallmentNumber); Assert.Equal(10, row.InstallmentCount); Assert.Equal(date, row.TransactionDate); Assert.Equal(invoice.Id, row.InvoiceId);
        Assert.Null(row.SourceSystem); Assert.Null(row.Competence);
    }

    [Trait("Category", "INFRA")]
    [Fact]
    public async Task MigrationDown_RefusesNewSnapshotsInsteadOfLosingThem()
    {
        var (_, w, _, _, _) = await Setup();
        await Service(s => s.SaveTargetAsync(w.Id, w.OwnerUserId, new(new(2026, 11, 1), MonthlyAggregateTargetType.TotalExpenses, 12000m, Source("target")), default));
        await using (var db = Db())
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261020232559_AddFinancialWorkspaceResetCoordination"));
            Assert.Contains("BOOTSTRAP_DOWN_REQUIRES_RECOVERY_PLAN", error.MessageText);
        }
        await using var check = Db(); Assert.Single(check.FinancialMonthlyAggregateTargets); Assert.Single(check.FinancialBootstrapProvenance);
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Transactions;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Infrastructure;

[Collection(PostgresCollection.Name)]
public sealed class InvoicePaymentOperationIdentityMigrationTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private const string PreviousMigration = "20261016194924_AddFinancialGoalAllocations";

    [Fact]
    public async Task Migration_PreservesHistoricalPayments_AssignsDistinctRequiredIdentities_AndDoesNotDuplicateEffects()
    {
        var owner = await SignInAsync("migration-operation-owner");
        var family = await CreateFamilyAsync(owner, "Família Migration T16.3");
        var today = BusinessCalendar.Today(DateTimeOffset.UtcNow);
        var account = await CreateAccountAsync(family.Id, owner, today);
        var category = await CreateCategoryAsync(family.Id, owner);
        var card = await CreateCardAsync(family.Id, owner, account);
        var invoice = await CreatePurchaseAndGetInvoiceAsync(family.Id, owner, card, category);

        var first = await CreatePaymentAsync(family.Id, owner, card, invoice, account, Guid.NewGuid(), "100.00", today);
        var second = await CreatePaymentAsync(family.Id, owner, card, invoice, account, Guid.NewGuid(), "50.00", today);
        var cancel = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{family.Id}/credit-cards/{card}/invoices/{invoice}/payments/{second}/cancel", owner);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        await using (var before = Db())
        {
            Assert.Equal(2, await before.FinancialCreditCardInvoicePayments.CountAsync());
            Assert.Equal(2, await before.FinancialTransactions.CountAsync(x => x.Origin == FinancialTransactionOrigin.CreditCardInvoicePayment));
        }

        NpgsqlConnection.ClearAllPools();
        await using (var migrationContext = Db())
        {
            var migrator = migrationContext.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await migrator.MigrateAsync();
        }
        NpgsqlConnection.ClearAllPools();

        await using var after = Db();
        var payments = await after.FinancialCreditCardInvoicePayments.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, payments.Count);
        Assert.Equal(2, payments.Select(x => x.OperationId).Distinct().Count());
        Assert.All(payments, payment => Assert.NotEqual(Guid.Empty, payment.OperationId));
        Assert.Equal(1, payments.Count(payment => payment.Status == FinancialCreditCardInvoicePaymentStatus.Cancelled));
        Assert.Contains(payments, payment => payment.Id == first);
        Assert.Contains(payments, payment => payment.Id == second);
        Assert.Equal(2, await after.FinancialTransactions.CountAsync(x => x.Origin == FinancialTransactionOrigin.CreditCardInvoicePayment));

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var column = new NpgsqlCommand("""
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'financial_credit_card_invoice_payments'
              AND column_name = 'operation_id'
            """, connection);
        Assert.Equal("NO", await column.ExecuteScalarAsync());
        await using var index = new NpgsqlCommand("""
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'financial_credit_card_invoice_payments'
              AND indexdef ILIKE '%UNIQUE%family_id%operation_id%'
            """, connection);
        Assert.NotNull(await index.ExecuteScalarAsync());
    }

    private async Task<Guid> CreateAccountAsync(Guid familyId, string owner, DateOnly today)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/accounts", owner,
            new { name = "Conta migration", type = "Checking", financialScope = "PF", openingBalance = "1000.00", openingBalanceDate = today });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCategoryAsync(Guid familyId, string owner)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/categories", owner,
            new { name = "Compras migration", type = "Expense" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCardAsync(Guid familyId, string owner, Guid account)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/credit-cards", owner,
            new { name = "Cartão migration", issuer = "Banco", creditLimit = "5000.00", closingDay = 10, dueDay = 20, defaultPaymentAccountId = account });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreatePurchaseAndGetInvoiceAsync(Guid familyId, string owner, Guid card, Guid category)
    {
        var purchase = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{familyId}/credit-cards/{card}/purchases", owner,
            new { description = "Compra histórica", purchaseDate = "2026-09-01", totalAmount = "300.00", installmentCount = 1, categoryId = category });
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/families/{familyId}/credit-cards/{card}/invoices?from=2026-09&through=2026-09");
        request.Headers.Add("Cookie", owner);
        var invoiceResponse = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, invoiceResponse.StatusCode);
        var invoices = await ReadAsync<JsonElement>(invoiceResponse);
        return invoices[0].GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreatePaymentAsync(Guid familyId, string owner, Guid card, Guid invoice, Guid account, Guid operationId, string amount, DateOnly today)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{familyId}/credit-cards/{card}/invoices/{invoice}/payments", owner,
            new { operationId, financialAccountId = account, paymentDate = today, amount });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
    }
}

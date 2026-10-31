using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Domain.Debts;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Integridade referencial da exclusão de categorias: toda tabela que referencia categoria tem FK Restrict (inclusive parcelas de dívida), vínculos novos
/// são recusados ou a exclusão é recusada (nunca ambos com sucesso) e a inativação não retira limites nem registros.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Categories")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CategoryIntegrityTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private WorkspaceDto _family = null!;
    private string Base => $"/api/financial-workspaces/{_family.Id}";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("integridade-dona");
        _family = await CreateWorkspaceAsync(_owner);
    }

    [Fact]
    public async Task EveryTableThatReferencesCategories_HasARestrictingForeignKey()
    {
        await ArrangeAsync();
        await using var db = Db();
        var rows = new List<(string Table, string Rule)>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT conrelid::regclass::text, confdeltype::text FROM pg_constraint "
            + "WHERE contype = 'f' AND confrelid = 'financial_categories'::regclass AND conrelid <> 'financial_categories'::regclass";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.All(rows, r => Assert.Contains(r.Rule, new[] { "r", "a" })); // RESTRICT ou NO ACTION: nunca cascata nem SET NULL
        foreach (var table in new[]
        {
            "financial_transactions", "financial_planned_transactions", "financial_recurrences", "financial_credit_card_purchases",
            "financial_credit_card_installments", "financial_credit_card_imported_installments", "financial_credit_card_invoice_adjustments",
            "financial_debt_installments", "financial_plan_lines", "financial_budget_targets", "pluggy_postings",
        })
        {
            Assert.Contains(rows, r => r.Table == table);
        }
    }

    [Fact]
    public async Task DebtInstallmentWithAnUnknownCategory_IsRejectedByTheDatabase()
    {
        await ArrangeAsync();
        var debt = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/debts", _owner,
            new { name = "Dívida", debtType = "Other", originalPrincipal = "1000.00", contractDate = "2026-01-01" });
        var debtId = (await ReadAsync<IdDto>(debt)).Id;
        await using var db = Db();
        db.FinancialDebtInstallments.Add(FinancialDebtInstallment.Create(_family.Id, debtId,
            new(1, new DateOnly(2026, 11, 10), 1000m, null, null, null, null, Guid.CreateVersion7()), DateTimeOffset.UtcNow));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)failure.InnerException!).SqlState);
    }

    [Theory]
    [InlineData("plannedTransactions")]
    [InlineData("cardPurchases")]
    public async Task Delete_IsRefused_WhenAPlannedTransactionOrACardPurchaseUsesTheCategory(string source)
    {
        await ArrangeAsync();
        var category = (await CreateCategoryAsync("Compras")).Id;
        var account = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/accounts", _owner, new { name = "Conta", type = "Checking", financialScope = "PF" }))).Id;
        if (source == "plannedTransactions")
        {
            Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/planned-transactions", _owner,
                new { accountId = account, categoryId = category, type = "Expense", description = "Luz", amount = "10.00", dueDate = "2026-10-20" })).StatusCode);
        }
        else
        {
            var card = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/credit-cards", _owner,
                new { name = "Cartão", issuer = "Banco", creditLimit = "10000.00", closingDay = 10, dueDay = 20 });
            var cardId = (await ReadAsync<IdDto>(card)).Id;
            Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/credit-cards/{cardId}/purchases", _owner,
                new { description = "Compra", purchaseDate = "2026-09-05", totalAmount = "120.00", installmentCount = 2, categoryId = category })).StatusCode);
        }

        var refused = await SendWithCsrfAsync(HttpMethod.Delete, $"{Base}/categories/{category}", _owner);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Contains(body.RootElement.GetProperty("impact").GetProperty("references").EnumerateArray(), r => r.GetProperty("source").GetString() == source);
        await using var db = Db();
        Assert.True(await db.FinancialCategories.AnyAsync(c => c.Id == category));
    }

    [Fact]
    public async Task ConcurrentDeleteAndLink_NeverBothSucceed_AndNeverLeaveAnOrphanReference()
    {
        await ArrangeAsync();
        var account = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/accounts", _owner, new { name = "Conta", type = "Checking", financialScope = "PF" }))).Id;
        var linked = 0;
        var deleted = 0;
        for (var i = 0; i < 8; i++)
        {
            var category = (await CreateCategoryAsync($"Corrida {i}")).Id;
            var delete = SendWithCsrfAsync(HttpMethod.Delete, $"{Base}/categories/{category}", _owner);
            var link = SendWithCsrfAsync(HttpMethod.Post, $"{Base}/transactions", _owner,
                new { accountId = account, categoryId = category, type = "Expense", description = "Corrida", amount = "1.00", transactionDate = "2026-10-06" });
            var responses = await Task.WhenAll(delete, link);
            var didDelete = responses[0].StatusCode == HttpStatusCode.NoContent;
            var didLink = responses[1].StatusCode == HttpStatusCode.Created;
            Assert.False(didDelete && didLink, $"iteração {i}: exclusão e vínculo tiveram sucesso juntos");
            Assert.True(didDelete || didLink, $"iteração {i}: delete={responses[0].StatusCode}, link={responses[1].StatusCode}");
            if (didDelete) deleted++; else linked++;
        }

        await using var db = Db();
        Assert.False(await db.FinancialTransactions.AnyAsync(t => t.CategoryId != null && !db.FinancialCategories.Any(c => c.Id == t.CategoryId)));
        Assert.Equal(linked, await db.FinancialTransactions.CountAsync());
        Assert.Equal(8, linked + deleted);
    }

    [Fact]
    public async Task Inactivating_KeepsBudgetLimits()
    {
        await ArrangeAsync();
        var food = (await CreateCategoryAsync("Mercado")).Id;
        var budget = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets", _owner, new { competence = "2026-11", description = "Novembro" });
        var budgetId = (await ReadAsync<IdDto>(budget)).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/budgets/{budgetId}/targets", _owner,
            new { name = "Mercado", groupingType = "Category", categoryId = food, plannedAmount = "500.00" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{Base}/categories/{food}", _owner, new { name = "Mercado", isActive = false })).StatusCode);

        await using var db = Db();
        var target = await db.FinancialBudgetTargets.AsNoTracking().SingleAsync();
        Assert.Equal((food, false, 500m), (target.CategoryId, target.IsArchived, target.PlannedAmount));
    }

    private async Task<IdDto> CreateCategoryAsync(string name)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Base}/categories", _owner, new { name, type = "Expense" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<IdDto>(response);
    }

    private sealed record IdDto(Guid Id);
}

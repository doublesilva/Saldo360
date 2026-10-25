using System.Text.Json;
using System.Text.Json.Nodes;
using Saldo360.Application.Categories;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
public sealed class StatementCategorizationTests
{
    private static readonly FinancialCategoryDto Health = new(Guid.NewGuid(), "Saúde", CategoryType.Expense, true, DateTimeOffset.UtcNow);
    private static readonly FinancialCategoryDto Online = new(Guid.NewGuid(), "Compras Online", CategoryType.Expense, true, DateTimeOffset.UtcNow);
    private static readonly FinancialCategoryDto Other = new(Guid.NewGuid(), "Outros", CategoryType.Expense, true, DateTimeOffset.UtcNow);
    private static CreditCardStatementAgentItem Item(string description = "Farmácia", string? category = "SAUDE") => new(description, "10.00", "Debit", "Purchase", null, null, "CurrentInvoice", true, 1, description + " 10.00", "High", "2026-10-01", description, category, "High");
    private static CreditCardStatementAgentPreview Preview(params CreditCardStatementAgentItem[] items) => new("synthetic.pdf", 1, new("Bank", "1234", "2026-10", null, null, "10.00"), items, new("Reconciled", "10.00", "10.00", "0.00", false));
    private static StatementCategoryCatalog Catalog(params StatementCategoryHistory[] history) => new([Health, Online, Other], history);

    [Fact]
    public void ExistingCategoryResolvesRealIdIgnoringCaseAccentsAndSpacing()
    {
        var result = Assert.Single(StatementCategorization.Categorize(Preview(Item(category: " S a ú d e ")), Catalog(), new Dictionary<string, Guid>()));
        Assert.Equal(Health.Id, result.CategoryId); Assert.Equal("Existing", result.Source); Assert.False(result.IsProposal);
    }
    [Fact]
    public void ConfirmedHistoryOverridesAgentOnlyOnExactPurchaseIdentity()
    {
        var catalog = Catalog(new StatementCategoryHistory("Farmácia", "2026-10-01", "10.00", "Outros"));
        var result = Assert.Single(StatementCategorization.Categorize(Preview(Item()), catalog, new Dictionary<string, Guid>()));
        Assert.Equal(Other.Id, result.CategoryId); Assert.Equal("History", result.Source);
        var distinct = Assert.Single(StatementCategorization.Categorize(Preview(Item() with { TransactionDate = "2026-10-02" }), catalog, new Dictionary<string, Guid>()));
        Assert.Equal(Health.Id, distinct.CategoryId);
    }
    [Theory]
    [InlineData("Mercado Livre")]
    [InlineData("Shopee")]
    [InlineData("Mercado Pago")]
    public void GenericMarketplaceCannotGainSpecificCategoryWithoutConfirmedHistory(string description)
    {
        var result = Assert.Single(StatementCategorization.Categorize(Preview(Item(description, "Eletrônicos")), Catalog(), new Dictionary<string, Guid>()));
        Assert.Equal(Online.Id, result.CategoryId); Assert.Equal("Generic", result.Source);
    }
    [Fact]
    public void SpecificNewCategoryIsOnlyAProposalAndLowConfidenceFallsBack()
    {
        var result = Assert.Single(StatementCategorization.Categorize(Preview(Item("Loja de instrumentos", "Instrumentos")), Catalog(), new Dictionary<string, Guid>()));
        Assert.Null(result.CategoryId); Assert.True(result.IsProposal); Assert.Equal("Proposed", result.Source);
        var generic = Assert.Single(StatementCategorization.Categorize(Preview(Item("Loja de instrumentos", "Instrumentos") with { CategoryConfidence = "Low" }), Catalog(), new Dictionary<string, Guid>()));
        Assert.Equal(Other.Id, generic.CategoryId);
    }
    [Fact]
    public void ManualCorrectionHasPriorityAndConflictingHistoryDoesNotGuess()
    {
        var preview = Preview(Item("Shopee", "Saúde"));
        var previous = Assert.Single(StatementCategorization.Categorize(preview, Catalog(), new Dictionary<string, Guid>()));
        var manual = Assert.Single(StatementCategorization.Categorize(preview, Catalog(), new Dictionary<string, Guid> { [previous.Key] = Health.Id }));
        Assert.Equal("Manual", manual.Source); Assert.Equal(Health.Id, manual.CategoryId);
        var conflict = Assert.Single(StatementCategorization.Categorize(preview, Catalog(new("Shopee", "2026-10-01", "10.00", "Saúde"), new("Shopee", "2026-10-01", "10.00", "Outros")), new Dictionary<string, Guid>()));
        Assert.Null(conflict.CategoryId); Assert.Equal("HistoryConflict", conflict.Source);
    }
    [Fact]
    public void MissingGenericIsProposedButInactiveNameIsNeverProposedAgain()
    {
        var item = Item() with { SuggestedCategory = null };
        var empty = Assert.Single(StatementCategorization.Categorize(Preview(item), new([], []), new Dictionary<string, Guid>()));
        Assert.True(empty.IsProposal); Assert.Equal("Outros", empty.CategoryName);
        var inactive = Assert.Single(StatementCategorization.Categorize(Preview(item), new([Other with { IsActive = false }], []), new Dictionary<string, Guid>()));
        Assert.False(inactive.IsProposal); Assert.Equal("InactiveCategory", inactive.Source);
    }
    [Fact]
    public void VersionTwoValidatesDatesAndRejectsInventedIdsWhileVersionOneRemainsSupported()
    {
        var document = new CreditCardStatementAgentDocument("2.0", Preview().Header, [Item()]);
        var json = JsonSerializer.Serialize(document, CreditCardStatementAgentSchema.Json);
        Assert.True(CreditCardStatementAgentEvaluator.TryEvaluate(json, "f.pdf", 1, out var valid, out _));
        Assert.Equal("2026-10-01", valid!.Items[0].TransactionDate);
        var legacy = JsonSerializer.Serialize(document with { SchemaVersion = "1.0" }, CreditCardStatementAgentSchema.Json);
        Assert.True(CreditCardStatementAgentEvaluator.TryEvaluate(legacy, "f.pdf", 1, out _, out _));
        var badDate = JsonSerializer.Serialize(document with { Items = [Item() with { TransactionDate = "2026-02-30" }] }, CreditCardStatementAgentSchema.Json);
        Assert.False(CreditCardStatementAgentEvaluator.TryEvaluate(badDate, "f.pdf", 1, out _, out _));
        var unknown = JsonNode.Parse(json)!; unknown["items"]![0]!["categoryId"] = Guid.NewGuid().ToString();
        Assert.False(CreditCardStatementAgentEvaluator.TryEvaluate(unknown.ToJsonString(), "f.pdf", 1, out _, out _));
        var missing = JsonNode.Parse(json)!; missing["items"]![0]!.AsObject().Remove("transactionDate");
        Assert.False(CreditCardStatementAgentEvaluator.TryEvaluate(missing.ToJsonString(), "f.pdf", 1, out _, out _));
        var nullItem = JsonNode.Parse(json)!; nullItem["items"]![0] = null;
        Assert.False(CreditCardStatementAgentEvaluator.TryEvaluate(nullItem.ToJsonString(), "f.pdf", 1, out _, out _));
        var excessive = JsonSerializer.Serialize(document with { Items = [Item() with { Amount = "10000000000000.00" }] }, CreditCardStatementAgentSchema.Json);
        Assert.False(CreditCardStatementAgentEvaluator.TryEvaluate(excessive, "f.pdf", 1, out _, out _));
    }

    [Fact]
    public void ProposalNamesAreCanonicalAndInactiveManualDecisionIsNotSilentlyOverwritten()
    {
        var preview = Preview(Item("SHOP A", "Áudio"), Item("SHOP B", " audio "));
        var proposals = StatementCategorization.Categorize(preview, Catalog(), new Dictionary<string, Guid>());
        Assert.Single(proposals.Select(c => c.CategoryName).Distinct());
        var first = Assert.Single(StatementCategorization.Categorize(Preview(Item()), Catalog(), new Dictionary<string, Guid>()));
        var inactive = StatementCategorization.Categorize(Preview(Item()), new([Health with { IsActive = false }, Other], []), new Dictionary<string, Guid> { [first.Key] = Health.Id });
        Assert.Equal("ManualUnavailable", Assert.Single(inactive).Source); Assert.Null(inactive[0].CategoryId);
    }
}

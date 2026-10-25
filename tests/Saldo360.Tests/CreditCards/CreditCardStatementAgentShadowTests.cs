using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
public sealed class CreditCardStatementAgentShadowTests
{
    [Fact]
    public void EqualResultsHaveNoDifferences()
    {
        var preview = Preview("726.94", Item("SHEIN 02/06", "726.94", "Installment", true));
        var comparison = CreditCardStatementAgentEvaluator.Compare(preview, preview);
        Assert.True(comparison.HeaderMatch); Assert.True(comparison.OfficialTotalMatch);
        Assert.Empty(comparison.ItemsOnlyInParser); Assert.Empty(comparison.ItemsOnlyInAgent);
        Assert.Empty(comparison.ClassificationDifferences); Assert.Empty(comparison.IncludedInCurrentTotalDifferences);
    }

    [Fact]
    public void ReportsTotalItemClassificationAndInclusionDifferencesWithoutFuzzyMatching()
    {
        var parser = Preview("100.00", Item("LOJA", "100.00", "Purchase", true), Item("ONLY PARSER", "2.00", "Fee", false));
        var agent = Preview("101.00", Item("LOJA", "100.00", "Fee", false), Item("ONLY AGENT", "3.00", "Fee", false));
        var comparison = CreditCardStatementAgentEvaluator.Compare(parser, agent);
        Assert.False(comparison.OfficialTotalMatch);
        Assert.Single(comparison.ClassificationDifferences);
        Assert.Single(comparison.IncludedInCurrentTotalDifferences);
        Assert.Contains(comparison.ItemsOnlyInParser, value => value.Contains("ONLY PARSER", StringComparison.Ordinal));
        Assert.Contains(comparison.ItemsOnlyInAgent, value => value.Contains("ONLY AGENT", StringComparison.Ordinal));
    }

    [Fact]
    public void FutureInstallmentsAndPaymentCannotEnterAgentComponentsTotal()
    {
        var document = new CreditCardStatementAgentDocument("1.0", Header("726.94"),
        [
            Item("CURRENT", "726.94", "Installment", true),
            Item("SHEIN FUTURA", "100.00", "Installment", true) with { Section = "FutureInstallments" },
            Item("PAGAMENTO", "726.94", "Payment", true) with { Section = "Payments", Nature = "Credit" },
        ]);
        var json = JsonSerializer.Serialize(document, CreditCardStatementAgentSchema.Json);
        Assert.True(CreditCardStatementAgentEvaluator.TryEvaluate(json, "file.pdf", 1, out var preview, out _));
        Assert.Equal("726.94", preview!.Reconciliation.ComponentsTotal);
        Assert.Equal("Reconciled", preview.Reconciliation.Status);
    }

    [Fact]
    public void EmptyNonStatementFailsSafely()
    {
        var json = JsonSerializer.Serialize(new CreditCardStatementAgentDocument("1.0", new(null, null, null, null, null, null), []), CreditCardStatementAgentSchema.Json);
        Assert.False(CreditCardStatementAgentEvaluator.TryEvaluate(json, "energy.pdf", 1, out _, out var error));
        Assert.Contains("evidência", error, StringComparison.OrdinalIgnoreCase);
    }

    private static CreditCardStatementAgentPreview Preview(string official, params CreditCardStatementAgentItem[] items)
    {
        var components = items.Where(item => item.IncludedInCurrentTotal).Sum(item => decimal.Parse(item.Amount));
        var difference = decimal.Parse(official) - components;
        return new("file.pdf", 1, Header(official), items, new(difference == 0 ? "Reconciled" : "Divergent", official,
            components.ToString("0.00"), difference.ToString("0.00"), false));
    }
    private static CreditCardStatementAgentHeader Header(string official) => new("Itaú", "1234", "2026-10", "2026-10-05", "2026-10-12", official);
    private static CreditCardStatementAgentItem Item(string description, string amount, string type, bool included) =>
        new(description, amount, "Debit", type, type == "Installment" ? 2 : null, type == "Installment" ? 6 : null,
            "CurrentInvoice", included, 1, description, "High");
}

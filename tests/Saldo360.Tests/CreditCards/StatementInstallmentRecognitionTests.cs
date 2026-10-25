using System.Globalization;
using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "REGRESSION")]
public sealed class StatementInstallmentRecognitionTests
{
    // Anonymized shape of the reference: six current rows, then three individual future rows.
    private static readonly (int Number, int Count)[] Current = [(6, 6), (4, 6), (4, 5), (3, 3), (3, 12), (2, 2)];
    private static readonly (int Number, int Count)[] Future = [(5, 6), (5, 5), (4, 12)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferenceShapePreservesSixCurrentAndThreeFutureInstallmentsDatesAndIndividualValues(bool batch)
    {
        int[] futureOrigins = [1, 2, 4];
        var items = Current.Select((ratio, n) => Item(n, ratio)).Concat(Future.Select((ratio, n) => Item(futureOrigins[n], ratio, future: true))).ToArray();
        var total = items.Where(i => i.IncludedInCurrentTotal).Sum(i => decimal.Parse(i.Amount, CultureInfo.InvariantCulture)).ToString("0.00", CultureInfo.InvariantCulture);
        var provider = new Provider(Facts(items, total));
        var source = new CreditCardPdfExtractedDocument([new(1, "COVER"), new(2, "Lançamentos: compras e saques\n" + string.Join('\n', items[..6].Select(i => i.Evidence))
            + "\nCompras parceladas - próximas faturas\n" + string.Join('\n', items[6..].Select(i => i.Evidence)) + "\nTOTAL " + total)]);
        var result = await Service(provider).ExtractAsync(new("synthetic.pdf", source, null) { PurchasesOnly = true, RequireAgent = true, BatchPages = batch }, default);
        // A cover page with no items remains explicitly unproven in batched coverage.
        Assert.Equal(batch ? AgentExtractionAssessment.NeedsReview : AgentExtractionAssessment.Reliable, result.Assessment); Assert.Null(result.AgentError);
        if (batch) Assert.Contains("PageWithoutItemsUnproven", result.DocumentWarnings!);
        Assert.False(result.ParserShadowExecuted); Assert.Null(result.Comparison); Assert.Null(result.ParserShadow);
        Assert.Empty(result.InstallmentDiagnostics ?? []);
        Assert.Equal(items, result.Primary!.Items); Assert.Equal(total, result.Primary.Reconciliation.ComponentsTotal);
        Assert.Equal(6, result.Primary.Items.Count(i => i.Section == "CurrentInvoice"));
        Assert.Equal(3, result.Primary.Items.Count(i => i.Section == "FutureInstallments"));
        Assert.All(result.Primary.Items.Skip(6), i => Assert.False(i.IncludedInCurrentTotal));
        Assert.Equal(Current, result.Primary.Items.Take(6).Select(i => (i.InstallmentNumber!.Value, i.InstallmentCount!.Value)));
        Assert.Equal(Future, result.Primary.Items.Skip(6).Select(i => (i.InstallmentNumber!.Value, i.InstallmentCount!.Value)));
        for (var n = 0; n < futureOrigins.Length; n++)
        {
            Assert.Equal(items[futureOrigins[n]].Amount, result.Primary.Items[n + 6].Amount);
            Assert.Equal(items[futureOrigins[n]].TransactionDate, result.Primary.Items[n + 6].TransactionDate);
        }
        Assert.Equal(1, provider.Calls); Assert.Contains("LOJA06/06", provider.Last!.SystemPrompt);
        Assert.Contains("Não concatene", provider.Last.SystemPrompt);
    }

    [Theory]
    [InlineData("06/06")]
    [InlineData("2/3")]
    [InlineData("07/10")]
    public void ExplicitMarkerAttachedToMerchantCannotRemainUnnumberedPurchase(string marker)
    {
        var item = Item(0, (6, 6)) with { OriginalDescription = "MERCHANT" + marker, Evidence = "MERCHANT" + marker + " 10.00",
            Type = "Purchase", InstallmentNumber = null, InstallmentCount = null };
        Assert.False(CreditCardStatementAgentSchema.TryValidate(Facts([item]), 2, out _, out var error, purchasesOnly: true));
        Assert.Equal("PurchaseInstallmentMetadataMissing:$.items[].installmentNumber", error);
    }


    [Theory]
    [InlineData("Purchase", 6, 6, "PurchaseInstallmentTypeMismatch:$.items[].type")]
    public void WrongNumberingOrTypeIsRejectedWithoutChangingFacts(string type, int number, int count, string code)
    {
        var item = Item(0, (6, 6)) with { Type = type, InstallmentNumber = number, InstallmentCount = count };
        Assert.False(CreditCardStatementAgentSchema.TryValidate(Facts([item]), 2, out _, out var error, purchasesOnly: true)); Assert.Equal(code, error);
        Assert.Equal(number, item.InstallmentNumber); Assert.Equal("11.11", item.Amount);
    }


    [Theory]
    [InlineData("Próxima fatura")]
    [InlineData("Demais faturas")]
    [InlineData("Total para próximas faturas")]
    public void FutureSectionTotalsCannotBecomePurchases(string label)
    {
        var item = Item(0, (6, 6)) with { OriginalDescription = label, Evidence = label + " 11.11" };
        Assert.False(CreditCardStatementAgentSchema.TryValidate(Facts([item]), 2, out _, out var error, purchasesOnly: true));
        Assert.Equal("PurchaseSummaryNotAllowed:$.items[]", error);
    }

    [Fact]
    public void SafeDiagnosticsPinpointLiteralAndMoneyBoundaryFailuresWithoutReturningContent()
    {
        var items = Current.Select((ratio, n) => Item(n, ratio) with { Type = "Purchase", InstallmentNumber = null, InstallmentCount = null }).ToArray();
        var source = new CreditCardPdfExtractedDocument([new(2, string.Join('\n', items.Select(i => i.Evidence)))]);
        var malformed = items.Select(i => i with { Evidence = i.Evidence.Replace(" " + i.Amount, i.Amount, StringComparison.Ordinal) }).ToArray();
        var preview = new CreditCardStatementAgentPreview("synthetic.pdf", 2, new(null, null, null, null, null, null), malformed, null!);
        var diagnostics = CreditCardStatementExtractionOrchestrator.GroundingDiagnostics(preview, source);
        Assert.Equal(6, diagnostics.Count(d => d.Code == "EvidenceQuoteNotFound"));
        Assert.Equal(6, diagnostics.Count(d => d.Code == "EvidenceAmountNotFound"));
        Assert.All(diagnostics, d => Assert.Equal(2, d.Page));
        var metadata = JsonSerializer.Serialize(diagnostics); Assert.DoesNotContain("MERCHANT", metadata); Assert.DoesNotContain("11.11", metadata);
    }

    private static CreditCardStatementAgentItem Item(int index, (int Number, int Count) ratio, bool future = false)
    {
        var description = "MERCHANT" + (char)('A' + index) + $"{ratio.Number:00}/{ratio.Count:00}";
        var amount = ((index + 1) * 11.11m).ToString("0.00", CultureInfo.InvariantCulture);
        var date = $"2026-01-{index + 1:00}";
        return new(description, amount, "Debit", "Installment", ratio.Number, ratio.Count, future ? "FutureInstallments" : "CurrentInvoice", !future,
            2, $"{index + 1:00}/01 {description} {amount}", "High", date);
    }
    private static string Facts(CreditCardStatementAgentItem[] items, string? total = null) => JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", new(null, null, null, null, null, total), items), CreditCardStatementAgentSchema.Json);
    private static CreditCardStatementExtractionOrchestrator Service(Provider provider) => new(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary }, new ForbiddenParser(), new(provider));
    private sealed class ForbiddenParser : IParserBackedCreditCardStatementExtractorAgent
    { public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Traditional parser forbidden"); }
    private sealed class Provider(string response) : ICreditCardStatementAgentProvider
    {
        public int Calls; public CreditCardStatementAgentProviderRequest? Last;
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct) { Calls++; Last = request; return Task.FromResult(response); }
    }
}

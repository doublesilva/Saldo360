using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class StatementPurchaseExtractionTests
{
    [Theory]
    [InlineData("Payment")]
    [InlineData("Credit")]
    [InlineData("Refund")]
    [InlineData("Fee")]
    [InlineData("PreviousBalance")]
    [InlineData("FinancedBalance")]
    [InlineData("Unknown")]
    public async Task NonPurchaseTypesAreRejectedEvenWhenSchemaAndAmountsAreValid(string type)
    {
        var result = await Run(Item() with { Type = type }, batch: true);
        Assert.Null(result.Primary); Assert.Null(result.StructuredDocument);
        Assert.Equal(AgentExtractionAssessment.Rejected, result.Assessment);
        Assert.Equal("PurchaseScopeInvalidType:$.items[].type", result.AgentError);
    }

    [Theory]
    [InlineData("Lançamentos atuais")]
    [InlineData("Total desta fatura")]
    [InlineData("Valor do Documento")]
    [InlineData("Total das compras")]
    [InlineData("Subtotal")]
    [InlineData("Pagamento mínimo")]
    [InlineData("Pagamento efetuado")]
    [InlineData("Saldo anterior")]
    [InlineData("Limite disponível")]
    [InlineData("Total de parcelas futuras")]
    public async Task SummaryCannotBecomePurchaseEvenWhenExactlyGroundedAndTotalReconciles(string label)
    {
        var item = Item() with { OriginalDescription = label, Evidence = label + " 10.00" };
        var result = await Run(item, source: item.Evidence);
        Assert.Equal(AgentExtractionAssessment.Rejected, result.Assessment);
        Assert.Equal("PurchaseSummaryNotAllowed:$.items[]", result.AgentError);
        Assert.Null(result.Primary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidCurrentPurchaseAndDocumentedFutureInstallmentRemainSeparate(bool batch)
    {
        var current = Item() with { TransactionDate = "2026-09-11" };
        var future = Item() with { Type = "Installment", InstallmentNumber = 7, InstallmentCount = 10,
            Section = "FutureInstallments", IncludedInCurrentTotal = false, Evidence = "FUTURE SHOP 10.00", TransactionDate = null };
        var provider = new Provider(Facts("10.00", current, future));
        var parser = new ForbiddenParser();
        var result = await Service(provider, parser).ExtractAsync(Request(batch) with
        { Document = new([new(1, "SHOP 10.00\nFUTURE SHOP 10.00\nTOTAL 10.00")]) }, default);
        Assert.Equal(AgentExtractionAssessment.Reliable, result.Assessment);
        Assert.Equal(2, result.Primary!.Items.Count); Assert.Equal("10.00", result.Primary.Reconciliation.ComponentsTotal);
        Assert.Null(result.Primary.Items[1].TransactionDate); Assert.Equal(7, result.Primary.Items[1].InstallmentNumber);
        Assert.Equal(1, provider.Calls); Assert.Equal(0, parser.Calls); Assert.Contains("PurchasesOnlyScope", result.DocumentWarnings!);
        Assert.True(provider.Last!.PurchasesOnly); Assert.Equal(CreditCardStatementAgentPrompt.PurchasesOnly, provider.Last.SystemPrompt.Split("\nEste é um lote independente.")[0]);
    }

    [Theory]
    [InlineData("Credit", "CurrentInvoice", true, "PurchaseScopeInvalidNature:$.items[].nature")]
    [InlineData("Debit", "Payments", false, "PurchaseScopeInvalidSection:$.items[].section")]
    [InlineData("Debit", "CurrentInvoice", false, "PurchaseScopeInvalidInclusion:$.items[].includedInCurrentTotal")]
    [InlineData("Debit", "FutureInstallments", true, "PurchaseScopeInvalidInclusion:$.items[].includedInCurrentTotal")]
    public async Task NatureSectionAndInclusionAreEnforced(string nature, string section, bool included, string code)
    {
        var result = await Run(Item() with { Nature = nature, Section = section, IncludedInCurrentTotal = included }, batch: true);
        Assert.Equal(code, result.AgentError); Assert.Null(result.Primary);
    }

    [Fact]
    public async Task HeaderSummaryCannotSupplyEvidenceForASeparateMerchant()
    {
        var result = await Run(Item() with { Evidence = "Valor do Documento 10.00" }, source: "SHOP 10.00\nValor do Documento 10.00");
        Assert.Equal("PurchaseSummaryNotAllowed:$.items[]", result.AgentError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferenceFromOfficialTotalRemainsVisibleWithoutPaymentOrAutomaticCorrection(bool batch)
    {
        var provider = new Provider(Facts("12.00", Item()));
        var parser = new ForbiddenParser();
        var result = await Service(provider, parser).ExtractAsync(Request(batch) with
        { ReconcileWithRetry = true, AllowCorrectionRetry = true, Document = new([new(1, "SHOP 10.00\nTOTAL 12.00")]) }, default);
        Assert.Equal(AgentExtractionAssessment.NeedsReview, result.Assessment);
        Assert.Equal("Divergent", result.Primary!.Reconciliation.Status); Assert.Equal("10.00", result.Primary.Reconciliation.ComponentsTotal);
        Assert.NotEqual("0.00", result.Primary.Reconciliation.Difference); Assert.Single(result.Primary.Items);
        Assert.Equal(1, provider.Calls); Assert.Equal(0, parser.Calls);
    }

    [Fact]
    public async Task RewrittenEvidenceIsStillRejectedAndNoParserFallbackOccurs()
    {
        var result = await Run(Item() with { Evidence = "SHOP purchase 10.00" }, source: "SHOP 10.00");
        Assert.Equal("FinancialEvidenceInvalid", result.AgentError); Assert.Equal(AgentExtractionAssessment.Rejected, result.Assessment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LowConfidencePaymentCannotEscapeScopeThroughCompatibilityNormalization(bool batch)
    {
        var result = await Run(Item() with { Type = "Payment", Confidence = "Low", IncludedInCurrentTotal = false }, batch);
        Assert.Equal("PurchaseScopeInvalidType:$.items[].type", result.AgentError); Assert.Null(result.Primary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LowConfidencePurchaseRemainsReviewableWithoutInventingOrDiscardingAmount(bool batch)
    {
        var result = await Run(Item() with { Confidence = "Low" }, batch);
        Assert.Null(result.AgentError); Assert.Equal(AgentExtractionAssessment.NeedsReview, result.Assessment);
        var item = Assert.Single(result.Primary!.Items); Assert.Equal("10.00", item.Amount); Assert.Equal("Unknown", item.Type);
    }

    private static Task<CreditCardStatementExtractionResult> Run(CreditCardStatementAgentItem item, bool batch = false, string source = "SHOP 10.00")
        => Service(new(Facts("10.00", item)), new()).ExtractAsync(Request(batch) with { Document = new([new(1, source)]) }, default);
    private static CreditCardStatementAgentRequest Request(bool batch) => new("synthetic.pdf", new([]), null) { PurchasesOnly = true, RequireAgent = true, BatchPages = batch };
    private static CreditCardStatementExtractionOrchestrator Service(Provider provider, ForbiddenParser parser) => new(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary }, parser, new(provider));
    private static CreditCardStatementAgentItem Item() => new("SHOP", "10.00", "Debit", "Purchase", null, null, "CurrentInvoice", true, 1, "SHOP 10.00", "High");
    private static string Facts(string total, params CreditCardStatementAgentItem[] items) => JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", new(null, null, null, null, null, total), items), CreditCardStatementAgentSchema.Json);
    private sealed class ForbiddenParser : IParserBackedCreditCardStatementExtractorAgent
    { public int Calls; public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken ct) { Calls++; throw new InvalidOperationException(); } }
    private sealed class Provider(string response) : ICreditCardStatementAgentProvider
    {
        public int Calls; public CreditCardStatementAgentProviderRequest? Last;
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct) { Calls++; Last = request; return Task.FromResult(response); }
    }
}

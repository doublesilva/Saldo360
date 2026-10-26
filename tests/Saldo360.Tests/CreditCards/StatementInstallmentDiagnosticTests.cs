using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "REGRESSION")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
[Trait("Profile", "FAST")]
public sealed class StatementInstallmentDiagnosticTests
{

    [Theory]
    [InlineData(5, 6, "installmentNumber")]
    [InlineData(6, 7, "installmentCount")]
    public void MismatchReportsSpecificFieldIndexPageMarkerAndReturnedMetadata(int number, int count, string field)
    {
        var wrong = Item(6, 6) with { InstallmentNumber = number, InstallmentCount = count };
        Assert.False(CreditCardStatementAgentSchema.TryValidate(Facts([Item(4, 6), wrong]), 2, out var document, out var error, out var diagnostics, true));
        Assert.Null(document); Assert.Equal("PurchaseInstallmentMetadataMismatch:$.items[]", error);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(new(1, 2, "PurchaseInstallmentMetadataMismatch", "$.items[]." + field, field, "06/06", number, count), diagnostic);
        Assert.Equal(number, wrong.InstallmentNumber); Assert.Equal(count, wrong.InstallmentCount);
    }

    [Fact]
    public void AllMismatchedItemsAndBothContradictingFieldsAreDiagnosedWithoutPartialAcceptance()
    {
        var items = new[] { Item(6, 6) with { InstallmentNumber = 5, InstallmentCount = 7 },
            Item(5, 5, true) with { InstallmentNumber = 4 } };
        Assert.False(CreditCardStatementAgentSchema.TryValidate(Facts(items), 2, out var document, out _, out var diagnostics, true));
        Assert.Null(document); Assert.Equal(3, diagnostics.Count);
        Assert.Equal(new[] { 0, 0, 1 }, diagnostics.Select(d => d.ItemIndex));
        Assert.Equal(new[] { "installmentNumber", "installmentCount", "installmentNumber" }, diagnostics.Select(d => d.Field));
        Assert.Equal(new[] { "06/06", "06/06", "05/05" }, diagnostics.Select(d => d.Marker));
    }

    [Fact]
    public void ObservedRealMismatchHasTwoWrongFieldsAtIndexTwoAndWrongNumberAtIndexFour()
    {
        // Only safe numbering metadata from the authorized run; all financial content is synthetic.
        (int Number, int Count)[] current = [(6, 6), (4, 6), (4, 5), (3, 3), (3, 12), (2, 2)];
        var items = current.Select(ratio => Item(ratio.Number, ratio.Count)).ToArray();
        items[2] = items[2] with { InstallmentNumber = 5, InstallmentCount = 6 };
        items[4] = items[4] with { InstallmentNumber = 12 };
        Assert.False(CreditCardStatementAgentSchema.TryValidate(Facts(items), 2, out var document, out _, out var diagnostics, true));
        Assert.Null(document);
        Assert.Equal(new[] {
            new StatementInstallmentDiagnostic(2, 2, "PurchaseInstallmentMetadataMismatch", "$.items[].installmentNumber", "installmentNumber", "04/05", 5, 6),
            new StatementInstallmentDiagnostic(2, 2, "PurchaseInstallmentMetadataMismatch", "$.items[].installmentCount", "installmentCount", "04/05", 5, 6),
            new StatementInstallmentDiagnostic(4, 2, "PurchaseInstallmentMetadataMismatch", "$.items[].installmentNumber", "installmentNumber", "03/12", 12, 12)
        }, diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateReportRetainsDiagnosticsWhenPrimaryAndStructuredDocumentAreNull(bool batch)
    {
        var provider = new Provider(Facts([Item(4, 6), Item(6, 6) with { InstallmentCount = 7 }]));
        var result = await Service(provider).ExtractAsync(Request(batch), default);
        Assert.Null(result.Primary); Assert.Null(result.StructuredDocument);
        Assert.Equal(AgentExtractionAssessment.Rejected, result.Assessment); Assert.Equal("Rejected", result.ValidationStatus);
        Assert.Equal("PurchaseInstallmentMetadataMismatch:$.items[]", result.AgentError);
        var diagnostic = Assert.Single(result.InstallmentDiagnostics!);
        Assert.Equal(1, diagnostic.ItemIndex); Assert.Equal(2, diagnostic.Page);
        Assert.Equal(batch ? 1 : (int?)null, diagnostic.Batch);
        Assert.Equal("installmentCount", diagnostic.Field); Assert.Equal("06/06", diagnostic.Marker);
        Assert.Equal(6, diagnostic.InstallmentNumber); Assert.Equal(7, diagnostic.InstallmentCount);
        // Serialize exactly the lab's report envelope/options; no rejected financial content escapes.
        var report = JsonSerializer.Serialize(new { extraction = result, financialMutationExecuted = false }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var json = JsonDocument.Parse(report);
        Assert.Equal(1, json.RootElement.GetProperty("extraction").GetProperty("installmentDiagnostics").GetArrayLength());
        Assert.DoesNotContain("PRIVATE-MERCHANT", report); Assert.DoesNotContain("11.11", report);
        Assert.DoesNotContain("2026-01-01", report); Assert.DoesNotContain("originalDescription", report);
        Assert.DoesNotContain("raw", report, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.ParserShadowExecuted); Assert.Null(result.ParserShadow); Assert.Null(result.Comparison);
        Assert.Equal(1, provider.Calls); Assert.Equal(1, result.Attempts); // No correction retry or parser fallback.
    }

    [Fact]
    public async Task BatchNumberDisambiguatesResponseLocalIndexFromOriginalPage()
    {
        var provider = new Provider(Facts([Item(6, 6) with { InstallmentNumber = 5, Page = 7 }]));
        var request = Request(true) with { Document = new(Enumerable.Range(1, 7).Select(n => new CreditCardPdfExtractedPage(n, "prepared")).ToArray()) };
        var result = await new LlmCreditCardStatementExtractorAgent(provider).ExtractBatchesAsync(request, default);
        Assert.Null(result.Document);
        Assert.Equal(new int?[] { 1, 2 }, result.InstallmentDiagnostics.Select(d => d.Batch));
        Assert.All(result.InstallmentDiagnostics, d => { Assert.Equal(0, d.ItemIndex); Assert.Equal(7, d.Page); });
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void ConflictingMarkersAreNotReportedAsAnUnequivocalDocumentMarker()
    {
        var item = Item(6, 6) with { Evidence = "PRIVATE-MERCHANT05/06 11.11" };
        Assert.False(CreditCardStatementAgentSchema.TryValidate(Facts([item]), 2, out _, out var error, out var diagnostics, true));
        Assert.Equal("PurchaseInstallmentEvidenceConflict:$.items[]", error);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("evidence", diagnostic.Field); Assert.Null(diagnostic.Marker);
        Assert.Equal("PurchaseInstallmentEvidenceConflict", diagnostic.Code);
    }

    [Fact]
    public void MissingMetadataReportsEachMissingFieldWithoutFillingIt()
    {
        var item = Item(3, 12) with { Type = "Purchase", InstallmentNumber = null, InstallmentCount = null };
        Assert.False(CreditCardStatementAgentSchema.TryValidate(Facts([item]), 2, out _, out _, out var diagnostics, true));
        Assert.Equal(new[] { "installmentNumber", "installmentCount" }, diagnostics.Select(d => d.Field));
        Assert.All(diagnostics, d => { Assert.Equal("03/12", d.Marker); Assert.Null(d.InstallmentNumber); Assert.Null(d.InstallmentCount); });
    }

    [Theory]
    [InlineData("04/06 PRIVATE-MERCHANT 11.11", "PRIVATE-MERCHANT")]
    [InlineData("04/06/2026 PRIVATE-MERCHANT 11.11", "PRIVATE-MERCHANT")]
    [InlineData("2026-06-04 PRIVATE-MERCHANT 11.11", "PRIVATE-MERCHANT")]
    [InlineData("PRIVATE-MERCHANT CODIGO06/06 11.11", "PRIVATE-MERCHANT CODIGO06/06")]
    [InlineData("PRIVATE-MERCHANT em 04/06 11.11", "PRIVATE-MERCHANT em 04/06")]
    [InlineData("PRIVATE-MERCHANT 04/06/2026 11.11", "PRIVATE-MERCHANT 04/06/2026")]
    [InlineData("PRIVATE-MERCHANT 2026-06-04 11.11", "PRIVATE-MERCHANT 2026-06-04")]
    [InlineData("PRIVATE-MERCHANT DATA: 04/06 11.11", "PRIVATE-MERCHANT DATA: 04/06")]
    [InlineData("PRIVATE-MERCHANT CÓDIGO06/06 11.11", "PRIVATE-MERCHANT CÓDIGO06/06")]
    [InlineData("PRIVATE-MERCHANT ref: 05/06 11.11", "PRIVATE-MERCHANT ref: 05/06")]
    [InlineData("PRIVATE-MERCHANT REFERÊNCIA 04/12 11.11", "PRIVATE-MERCHANT REFERÊNCIA 04/12")]
    [InlineData("PRIVATE-MERCHANT PEDIDO03/12 11.11", "PRIVATE-MERCHANT PEDIDO03/12")]
    [InlineData("PRIVATE-MERCHANT123/456 11.11", "PRIVATE-MERCHANT123/456")]
    [InlineData("PRIVATE-MERCHANT 10,02/03", "PRIVATE-MERCHANT")]
    public void DatesCodesReferencesAndMoneyFragmentsDoNotGenerateInstallmentErrors(string evidence, string description)
    {
        var item = Item(6, 6) with { OriginalDescription = description, Evidence = evidence, Type = "Purchase", InstallmentNumber = null, InstallmentCount = null };
        Assert.True(CreditCardStatementAgentSchema.TryValidate(Facts([item]), 2, out var document, out var error, out var diagnostics, true), error);
        Assert.Empty(diagnostics); Assert.Equal(item, Assert.Single(document!.Items));
    }

    [Fact]
    public void InvalidJsonAndProviderPropertyNamesCannotLeakThroughDiagnostics()
    {
        Assert.False(CreditCardStatementAgentSchema.TryValidate("{PRIVATE-MERCHANT", 2, out _, out _, out var diagnostics, true));
        Assert.Empty(diagnostics);
        var invalid = Facts([Item(6, 6)]).Replace("\"schemaVersion\":", "\"PRIVATE-MERCHANT\":", StringComparison.Ordinal);
        Assert.False(CreditCardStatementAgentSchema.TryValidate(invalid, 2, out _, out var error, out diagnostics, true));
        Assert.Empty(diagnostics); Assert.DoesNotContain("PRIVATE-MERCHANT", error);
    }

    private static CreditCardStatementAgentItem Item(int number, int count, bool future = false) => new(
        $"PRIVATE-MERCHANT{number:00}/{count:00}", "11.11", "Debit", "Installment", number, count,
        future ? "FutureInstallments" : "CurrentInvoice", !future, 2, $"01/01 PRIVATE-MERCHANT{number:00}/{count:00} 11.11", "High", "2026-01-01");
    private static string Facts(CreditCardStatementAgentItem[] items) => JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", new(null, null, null, null, null, null), items), CreditCardStatementAgentSchema.Json);
    private static CreditCardStatementAgentRequest Request(bool batch) => new("synthetic.pdf", new([new(1, "prepared"), new(2, "prepared")]), null)
    { PurchasesOnly = true, RequireAgent = true, BatchPages = batch, ReconcileWithRetry = true };
    private static CreditCardStatementExtractionOrchestrator Service(Provider provider) => new(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary }, new ForbiddenParser(), new(provider));
    private sealed class ForbiddenParser : IParserBackedCreditCardStatementExtractorAgent
    { public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Traditional parser forbidden"); }
    private sealed class Provider(string response) : ICreditCardStatementAgentProvider
    {
        public int Calls;
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct) { Calls++; return Task.FromResult(response); }
    }
}

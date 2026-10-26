using System.Globalization;
using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
[Trait("Profile", "FAST")]
public sealed class StatementPurchaseDocumentValidationTests
{
    [Theory]
    [InlineData("04/07/2021", "Período da fatura: 01/10/2026 a 31/10/2026", true, "2021-07-04", "PurchaseDateExplicit")]
    [InlineData("2024-02-29", "", false, "2024-02-29", "PurchaseDateExplicit")]
    [InlineData("20/09", "Período da fatura: 01/09/2026 a 30/09/2026", false, "2026-09-20", "PurchaseDateDocumentPeriod")]
    [InlineData("31/12", "Período da fatura: 16/12/2026 a 15/01/2027", false, "2026-12-31", "PurchaseDateDocumentPeriod")]
    [InlineData("03/01", "Período da fatura: 16/12/2026 a 15/01/2027", false, "2027-01-03", "PurchaseDateDocumentPeriod")]
    [InlineData("04/07", "Período das compras: 01/01/2021 a 31/12/2021", true, "2021-07-04", "PurchaseDateDocumentPeriod")]
    [InlineData("04/07", "Período de compras: 01/01/2021 à 31/12/2021", true, "2021-07-04", "PurchaseDateDocumentPeriod")]
    [InlineData("04/07", "Período da fatura: 01/07/2026 a 31/07/2026", true, null, "PurchaseDateYearUnproven")]
    [InlineData("04/07", "Vencimento: 13/10/2026", false, null, "PurchaseDateYearUnproven")]
    [InlineData("04/07", "Período das compras: 01/01/2025 a 31/12/2026", true, null, "PurchaseDateYearAmbiguous")]
    [InlineData("29/02", "Período das compras: 01/01/2024 a 31/12/2024", false, "2024-02-29", "PurchaseDateDocumentPeriod")]
    [InlineData("29/02", "Período das compras: 01/01/2025 a 31/12/2025", false, null, "PurchaseDateOutsideDocumentPeriod")]
    [InlineData("31/02/2026", "", false, null, "PurchaseDateInvalid")]
    [InlineData("", "", false, null, "PurchaseDateAbsent")]
    public void DatesUseOnlyApplicableExplicitDocumentaryProof(string token, string context, bool installment, string? expected, string code)
    {
        var result = StatementPurchaseDateEvidence.Resolve(token, new([Page(1, context)]), installment);
        Assert.Equal(expected, result.Date); Assert.Equal(code, result.Code);
    }

    [Fact]
    public void ConflictingPeriodBoundsCannotInventAYearFromTheConvenientRange()
    {
        var source = new CreditCardPdfExtractedDocument([Page(1, "Período das compras: 01/01/2021 a 31/12/2021\nPeríodo da fatura: 01/07/2026 a 31/07/2026")]);
        Assert.Equal("PurchaseDatePeriodConflict", StatementPurchaseDateEvidence.Resolve("04/07", source, false).Code);
    }

    [Fact]
    public async Task SixCurrentThreeFutureAndExcludedPagesHaveIndependentScopeCoverage()
    {
        var items = ReferenceItems();
        var source = ReferenceSource(items);
        var provider = new Provider(Facts(items));
        var result = await Service(provider).ExtractAsync(Request(source), default);
        Assert.Equal(AgentExtractionAssessment.Reliable, result.Assessment); Assert.Equal("Valid", result.ValidationStatus);
        Assert.Null(result.AgentError); Assert.Equal(9, result.Primary!.Items.Count);
        Assert.Equal(items, result.Primary.Items); Assert.Equal("60.00", result.Primary.Reconciliation.ComponentsTotal);
        Assert.True(result.OutputCoverage!.RecognitionComplete);
        var document = result.PurchaseDocumentValidation!;
        Assert.True(document.CoverageComplete); Assert.True(document.DatesComplete); Assert.False(document.HasContradiction);
        Assert.Equal(new[] { 0, 9, 0 }, document.Pages.Select(p => p.EligibleRows));
        Assert.Equal("Absent", document.Identity.Status); Assert.False(document.Identity.AuthorizedIdentityConfirmed);
        Assert.DoesNotContain("PageWithoutItemsUnproven", result.DocumentWarnings!);
        Assert.DoesNotContain("UnextractedDocumentRows", result.DocumentWarnings!);
        Assert.False(result.ParserShadowExecuted); Assert.Null(result.Comparison); Assert.Equal(1, provider.Calls);
        Assert.Contains("dd/MM", provider.Request!.SystemPrompt); Assert.Contains("ano", provider.Request.SystemPrompt);
    }

    [Fact]
    public async Task MissingPurchaseRemainsUnprovenEvenIfModelOfficialTotalAndSumAgree()
    {
        var items = ReferenceItems();
        var result = await Service(new Provider(Facts(items[..8]))).ExtractAsync(Request(ReferenceSource(items)), default);
        Assert.Equal("Reconciled", result.Primary!.Reconciliation.Status);
        Assert.Equal(AgentExtractionAssessment.NeedsReview, result.Assessment);
        Assert.False(result.OutputCoverage!.RecognitionComplete);
        Assert.Contains(result.PurchaseDocumentValidation!.Diagnostics, d => d.Code == "PurchaseCoverageIncomplete" && d.Page == 2);
    }

    [Fact]
    public async Task ExtraCopyAndItemInWrongSectionRemainRejected()
    {
        var items = ReferenceItems(); var source = ReferenceSource(items);
        foreach (var wrong in new[] { items.Concat([items[6]]).ToArray(), items.Select((i, n) => n == 6 ? i with { Section = "CurrentInvoice", IncludedInCurrentTotal = true } : i).ToArray() })
        {
            var result = await Service(new Provider(Facts(wrong))).ExtractAsync(Request(source), default);
            Assert.Equal(AgentExtractionAssessment.Rejected, result.Assessment);
            Assert.Contains(result.PurchaseDocumentValidation!.Diagnostics, d => d.Contradiction);
            Assert.False(result.OutputCoverage!.RecognitionComplete);
        }
    }

    [Theory]
    [InlineData(null, "PurchaseDateMissing")]
    [InlineData("2025-09-20", "PurchaseDateMismatch")]
    public async Task UniquelyProvenYearIsRequiredButNeverFilledOrCorrected(string? returned, string code)
    {
        var item = Item(0, (1, 2)) with { TransactionDate = returned, Evidence = "20/09 SHOPA01/02 10.00" };
        var source = new CreditCardPdfExtractedDocument([Page(1, "Período das compras: 01/09/2026 a 30/09/2026\nLançamentos: compras e saques\n" + item.Evidence + "\nTotal 10.00")]);
        item = item with { Page = 1 };
        var result = await Service(new Provider(Facts([item], "10.00"))).ExtractAsync(Request(source), default);
        Assert.Equal(AgentExtractionAssessment.Rejected, result.Assessment); Assert.Equal(returned, result.Primary!.Items[0].TransactionDate);
        Assert.Contains(result.PurchaseDocumentValidation!.Diagnostics, d => d.Code == code && d.ItemIndex == 0 && d.Contradiction);
    }

    [Fact]
    public async Task AmbiguousYearStaysNullWhileFinancialLinesAndIdentityAreAssessedSeparately()
    {
        var item = Item(0, (3, 12)) with { Page = 1, TransactionDate = null, Evidence = "04/07 SHOPA03/12 10.00" };
        var source = new CreditCardPdfExtractedDocument([Page(1, "Vencimento 13/10/2026\nLançamentos: compras e saques\n" + item.Evidence + "\nTotal 10.00")]);
        var result = await Service(new Provider(Facts([item], "10.00"))).ExtractAsync(Request(source), default);
        Assert.Equal(AgentExtractionAssessment.Reliable, result.Assessment); Assert.Null(result.Primary!.Items[0].TransactionDate);
        Assert.True(result.PurchaseDocumentValidation!.CoverageComplete); Assert.False(result.PurchaseDocumentValidation.DatesComplete);
        Assert.Contains(result.PurchaseDocumentValidation.Diagnostics, d => d.Code == "PurchaseDateYearUnproven" && !d.Contradiction);
        var fabricated = item with { TransactionDate = "2026-07-04" };
        var rejected = await Service(new Provider(Facts([fabricated], "10.00"))).ExtractAsync(Request(source), default);
        Assert.Equal(AgentExtractionAssessment.Rejected, rejected.Assessment);
        Assert.Contains(rejected.PurchaseDocumentValidation!.Diagnostics, d => d.Code == "PurchaseDateUnsupportedYear" && d.Contradiction);
    }

    [Fact]
    public async Task DateOutsideEvidenceCannotBeReplacedByDueDate()
    {
        var item = Item(0, (1, 2)) with { Page = 1, TransactionDate = "2026-10-13", Evidence = "SHOPA01/02 10.00" };
        var source = new CreditCardPdfExtractedDocument([Page(1, "Vencimento 13/10/2026\nLançamentos: compras e saques\n" + item.Evidence + "\nTotal 10.00")]);
        var result = await Service(new Provider(Facts([item], "10.00"))).ExtractAsync(Request(source), default);
        Assert.Equal(AgentExtractionAssessment.Rejected, result.Assessment);
        Assert.False(result.OutputCoverage!.RecognitionComplete);
    }

    [Theory]
    [InlineData("Pagamentos efetuados\n10/09 PAGAMENTO 10.00\nTotal 10.00")]
    [InlineData("Créditos e estornos\n10/09 ESTORNO 10.00\nSubtotal 10.00")]
    [InlineData("Encargos\n10/09 IOF 10.00\nLimite disponível 1000.00")]
    [InlineData("Boletos\n10/09/2026 DOCUMENTO 10.00\nOfertas\n10/09 SIMULAÇÃO 20.00")]
    public async Task NonPurchasePagesDoNotRequireFakeItemsToProveTheirScope(string text)
    {
        var result = await Service(new Provider(Facts([], "0.00"))).ExtractAsync(Request(new([Page(1, text + "\nTotal 0.00")])), default);
        Assert.True(result.OutputCoverage!.RecognitionComplete); Assert.True(result.PurchaseDocumentValidation!.CoverageComplete);
        Assert.Empty(result.Primary!.Items); Assert.DoesNotContain("PageWithoutItemsUnproven", result.DocumentWarnings!);
    }

    [Fact]
    public async Task UnknownLayoutAndUnreadablePageCannotClaimRecognition()
    {
        foreach (var page in new[] { Page(1, "04/07 UNKNOWN PURCHASE 10.00"), Page(1, "") with { TextSource = "OCR", ProcessingStatus = "Unreadable" },
            Page(1, "native footer only") with { HasUntranscribedImageRegion = true } })
        {
            var result = await Service(new Provider(Facts([], "0.00"))).ExtractAsync(Request(new([page])), default);
            Assert.False(result.OutputCoverage!.RecognitionComplete);
        }
    }

    [Fact]
    public async Task NativeColumnGeometryDoesNotCountChargesBesideThePurchaseTable()
    {
        var item = Item(0, (1, 2)) with { Page = 1 };
        CreditCardPdfExtractedWord[] words = [new("Lançamentos: compras e saques", 10, 0, 180, 10), new("Encargos cobrados", 300, 0, 430, 10),
            new("04/07/2024", 10, 30, 45, 40), new("SHOPA01/02", 50, 30, 160, 40), new("10.00", 200, 30, 230, 40),
            new("13/10/2026", 300, 30, 345, 40), new("IOF", 350, 30, 380, 40), new("5.00", 400, 30, 430, 40)];
        var page = Page(1, "Lançamentos: compras e saques Encargos cobrados\n" + item.Evidence + " 13/10/2026 IOF 5.00\nTotal 10.00") with { Words = words };
        var result = await Service(new Provider(Facts([item], "10.00"))).ExtractAsync(Request(new([page])), default);
        Assert.True(result.OutputCoverage!.RecognitionComplete); Assert.Equal(1, result.PurchaseDocumentValidation!.Pages[0].EligibleRows);
        Assert.Empty(result.PurchaseDocumentValidation.CoverageIssues);
    }

    [Fact]
    public async Task LegitimateIdenticalPurchaseRowsRequireDistinctEvidenceOccurrencesAndAreNotDeduplicated()
    {
        var item = Item(0, (1, 2)) with { Page = 1 };
        var page = Page(1, "Lançamentos: compras e saques\n" + item.Evidence + "\n" + item.Evidence + "\nTotal 20.00");
        var result = await Service(new Provider(Facts([item, item], "20.00"))).ExtractAsync(Request(new([page])), default);
        Assert.Equal(AgentExtractionAssessment.Reliable, result.Assessment); Assert.True(result.OutputCoverage!.RecognitionComplete);
        Assert.Equal(2, result.Primary!.Items.Count); Assert.Equal(2, result.PurchaseDocumentValidation!.Pages[0].MatchedRows);
    }

    [Theory]
    [InlineData("L Total dos lançamentos atuais 10.00")]
    [InlineData("Ⓛ Total dos lançamentos atuais 10.00")]
    [InlineData("P Total dos pagamentos 10.00")]
    public async Task LegendPrefixedSummaryIsNotAnOmittedPurchase(string summary)
    {
        var item = Item(0, (1, 2)) with { Page = 1 };
        var page = Page(1, "Lançamentos: compras e saques\n" + item.Evidence + "\n" + summary);
        var result = await Service(new Provider(Facts([item], "10.00"))).ExtractAsync(Request(new([page])), default);
        Assert.True(result.OutputCoverage!.RecognitionComplete); Assert.Equal(1, result.PurchaseDocumentValidation!.Pages[0].EligibleRows);
    }

    [Fact]
    public async Task DatedMerchantBeginningWithTotalIsNotSilentlyRemovedAsASummary()
    {
        var item = Item(0, (1, 2)) with { Page = 1, OriginalDescription = "TOTAL MARKET01/02", Evidence = "04/07/2024 TOTAL MARKET01/02 10.00" };
        var page = Page(1, "Lançamentos: compras e saques\n" + item.Evidence + "\nTotal 10.00");
        var result = await Service(new Provider(Facts([item], "10.00"))).ExtractAsync(Request(new([page])), default);
        Assert.True(result.OutputCoverage!.RecognitionComplete); Assert.Single(result.Primary!.Items);
    }

    [Fact]
    public async Task VisualIssuerIsReportedAsUnverifiedWithoutInvalidatingFinancialLines()
    {
        var items = ReferenceItems();
        var header = Header() with { Issuer = "Synthetic Bank", IdentityEvidence = "VISUAL: clear bank logo", IdentityPage = 1 };
        var result = await Service(new Provider(Facts(items, header: header))).ExtractAsync(Request(ReferenceSource(items)), default);
        Assert.Equal(AgentExtractionAssessment.Reliable, result.Assessment);
        Assert.Equal("VisualUnverified", result.PurchaseDocumentValidation!.Identity.Status);
        Assert.False(result.PurchaseDocumentValidation.Identity.AuthorizedIdentityConfirmed);
        Assert.Contains("IdentityEvidenceUnverified", result.DocumentWarnings!);
    }

    [Fact]
    public async Task ContradictoryAuthorizedContextBlocksWithoutChoosingAnotherCard()
    {
        var items = ReferenceItems(); var header = Header() with { Issuer = "Other Bank", CardLastDigits = "9999" };
        var request = Request(ReferenceSource(items)) with { AuthorizedIdentity = new("Synthetic Bank", "1234") };
        var result = await Service(new Provider(Facts(items, header: header))).ExtractAsync(request, default);
        Assert.Equal(AgentExtractionAssessment.Rejected, result.Assessment);
        Assert.Equal("Contradictory", result.PurchaseDocumentValidation!.Identity.Status);
        Assert.Contains(result.PurchaseDocumentValidation.Diagnostics, d => d.Code == "DocumentIdentityContradiction");
        var json = JsonSerializer.Serialize(result.PurchaseDocumentValidation);
        Assert.DoesNotContain("Other Bank", json); Assert.DoesNotContain("9999", json); Assert.DoesNotContain("SHOP", json); Assert.DoesNotContain("10.00", json);
    }

    private static CreditCardStatementAgentItem[] ReferenceItems()
    {
        (int Number, int Count)[] current = [(6, 6), (4, 6), (4, 5), (3, 3), (3, 12), (2, 2)];
        return current.Select((r, n) => Item(n, r)).Concat(new[] { Item(1, (5, 6), true), Item(2, (5, 5), true), Item(4, (4, 12), true) }).ToArray();
    }
    private static CreditCardStatementAgentItem Item(int index, (int Number, int Count) ratio, bool future = false)
    {
        var name = $"SHOP{(char)('A' + index)}{ratio.Number:00}/{ratio.Count:00}";
        return new(name, "10.00", "Debit", "Installment", ratio.Number, ratio.Count, future ? "FutureInstallments" : "CurrentInvoice", !future,
            2, $"04/07/2024 {name} 10.00", "High", "2024-07-04");
    }
    private static CreditCardPdfExtractedPage Page(int page, string text) => new(page, text) { ImagePng = [137, 80, 78, 71, 13, 10, 26, 10], ProcessingStatus = "NativeWithImage" };
    private static CreditCardPdfExtractedDocument ReferenceSource(CreditCardStatementAgentItem[] items) => new([
        Page(1, "Pagamentos efetuados\n10/09 PAGAMENTO 60.00\nTotal dos pagamentos 60.00"),
        Page(2, "Lançamentos: compras e saques\n" + string.Join('\n', items[..6].Select(i => i.Evidence))
            + "\nTotal dos lançamentos 60.00\nCompras parceladas - próximas faturas\n" + string.Join('\n', items[6..].Select(i => i.Evidence)) + "\nTotal para próximas faturas 30.00"),
        Page(3, "Boletos\n13/10/2026 Valor do documento 60.00\nOfertas e simulações\n10/10 Opção 20.00")]);
    private static CreditCardStatementAgentHeader Header() => new(null, null, "2026-10", null, "2026-10-13", "60.00");
    private static string Facts(CreditCardStatementAgentItem[] items, string? total = null, CreditCardStatementAgentHeader? header = null)
        => JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", header ?? Header() with { OfficialTotal = total ?? "60.00" }, items), CreditCardStatementAgentSchema.Json);
    private static CreditCardStatementAgentRequest Request(CreditCardPdfExtractedDocument source) => new("synthetic.pdf", source, null)
    { PurchasesOnly = true, ValidatePurchaseDocument = true, BatchPages = true, RequireAgent = true, AllowCorrectionRetry = false };
    private static CreditCardStatementExtractionOrchestrator Service(Provider provider) => new(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary }, new ForbiddenParser(), new(provider));
    private sealed class ForbiddenParser : IParserBackedCreditCardStatementExtractorAgent
    { public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Traditional parser forbidden"); }
    private sealed class Provider(string facts) : ICreditCardStatementAgentProvider
    {
        public int Calls; public CreditCardStatementAgentProviderRequest? Request;
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct) { Calls++; Request = request; return Task.FromResult(facts); }
    }
}

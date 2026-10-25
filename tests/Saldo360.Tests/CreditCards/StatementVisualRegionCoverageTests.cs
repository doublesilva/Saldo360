using System.Text.Json;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class StatementVisualRegionCoverageTests
{
    private const string Legal = "O pagamento da fatura deve observar as condicoes estabelecidas no contrato.\nOs encargos e juros seguem as regras de financiamento previamente informadas ao cliente.";
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public async Task NativePageAndLegalRasterAreAutomaticallyCoveredWithoutAddingPurchases()
    {
        var page = Hybrid("Lançamentos: compras e saques\nTotal 0.00", Region(Legal));
        var result = await Extract(page, []);
        Assert.Equal(AgentExtractionAssessment.Reliable, result.Assessment);
        Assert.True(result.OutputCoverage!.RecognitionComplete);
        Assert.Equal("ExcludedLegal", Assert.Single(result.PurchaseDocumentValidation!.VisualRegions).Classification);
        Assert.Empty(result.Primary!.Items); Assert.True(result.Suitability!.FaithfulExtraction);
        Assert.False(result.Suitability.PersistenceEligible); Assert.False(result.ParserShadowExecuted);
    }

    [Fact]
    public async Task RasterPurchaseMustBeInterpretedByAgentAndCannotDisappearIntoLegalText()
    {
        var line = "04/07 SHOP03/12 10.00";
        var page = Hybrid("Lançamentos: compras e saques\nTotal 10.00", Region(line));
        var item = Item(line);
        var result = await Extract(page, [item], "10.00");
        Assert.True(result.OutputCoverage!.RecognitionComplete);
        Assert.Equal(item, Assert.Single(result.Primary!.Items));
        Assert.True(result.PurchaseDocumentValidation!.UsesOcrPurchaseEvidence);
        Assert.Contains("OCRRequiresReview", result.DocumentWarnings!); // Existing OCR financial guard is not relaxed.
        Assert.Equal(AgentExtractionAssessment.NeedsReview, result.Assessment);
        var omitted = await Extract(page, [], "10.00");
        Assert.False(omitted.OutputCoverage!.RecognitionComplete);
        var mixed = Region(Legal + "\n" + line);
        Assert.Equal("PurchaseRows", StatementVisualRegionText.Classify(mixed).Classification);
    }

    [Theory]
    [InlineData("Unreadable")]
    [InlineData("Unavailable")]
    [InlineData("Pending")]
    [InlineData("RegionLimitExceeded")]
    [InlineData("RegionBudgetExceeded")]
    public async Task FailedOrUnprocessedRasterCannotProveCoverage(string status)
    {
        var result = await Extract(Hybrid("Lançamentos: compras e saques\nTotal 0.00", Region(Legal) with { Status = status }), []);
        Assert.False(result.OutputCoverage!.RecognitionComplete); Assert.False(result.Suitability!.FaithfulExtraction);
        Assert.False(result.Suitability.PersistenceEligible);
        Assert.Contains(result.PurchaseDocumentValidation!.Diagnostics, d => d.Code == "VisualRegionInconclusive");
    }

    [Fact]
    public async Task NativeOcrOverlapUsesOnePhysicalOccurrenceButEqualPurchasesElsewhereRemainDistinct()
    {
        var line = "04/07 SHOP03/12 10.00";
        var native = Native("Lançamentos: compras e saques\nTotal 20.00") with
        { Words = [.. Words("Lançamentos: compras e saques\nTotal 20.00", 10), .. Words(line, 200), .. Words(line, 230)],
            Text = "Lançamentos: compras e saques\nTotal 20.00\n" + line + "\n" + line };
        var page = StatementVisualRegionText.Merge(native with { HasUntranscribedImageRegion = true, VisualRegions = [Region(line + "\n" + line)] });
        Assert.Equal("NativeOverlay", page.VisualRegions[0].Status);
        Assert.Equal(native.Text, page.Text); Assert.Equal(native.Words!.Count, page.Words!.Count);
        var result = await Extract(page, [Item(line), Item(line)], "20.00");
        Assert.True(result.OutputCoverage!.RecognitionComplete); Assert.Equal(2, result.Primary!.Items.Count);
        Assert.False(result.PurchaseDocumentValidation!.UsesOcrPurchaseEvidence);
        var extra = await Extract(page, [Item(line), Item(line), Item(line)], "30.00");
        Assert.Equal(AgentExtractionAssessment.Rejected, extra.Assessment);
    }

    [Fact]
    public async Task ConflictingNativeAndRasterTextIsNotSilentlyCorrectedOrDuplicated()
    {
        var native = Native("Lançamentos: compras e saques\nTotal 10.00") with
        { Words = [.. Words("Lançamentos: compras e saques\nTotal 10.00", 10), .. Words("04/07 SHOP03/12 10.00", 200)] };
        var page = StatementVisualRegionText.Merge(native with { VisualRegions = [Region("04/07 SHOP03/12 11.00")] });
        Assert.Equal("NativeConflict", page.VisualRegions[0].Status); Assert.Equal(native.Text, page.Text);
        var result = await Extract(page, [], "10.00");
        Assert.False(result.OutputCoverage!.RecognitionComplete);
        Assert.Contains(result.PurchaseDocumentValidation!.Diagnostics, d => d.Code == "VisualRegionNativeConflict");
    }

    [Fact]
    public async Task VisualTotalizersAreExcludedButUnknownRegionsAreNotWhitelisted()
    {
        var result = await Extract(Hybrid("Lançamentos: compras e saques\nTotal 0.00", Region("Total 10.00\nSubtotal 5.00")), []);
        Assert.True(result.OutputCoverage!.RecognitionComplete); Assert.Empty(result.Primary!.Items);
        Assert.Equal("ExcludedSummary", result.PurchaseDocumentValidation!.VisualRegions[0].Classification);
        var unknown = await Extract(Hybrid("Lançamentos: compras e saques\nTotal 0.00", Region("UNCLASSIFIED CONTENT 10.00")), []);
        Assert.False(unknown.OutputCoverage!.RecognitionComplete);
    }

    [Fact]
    public async Task NineDocumentedInstallmentsRemainFaithfulWithoutInventingDatesOrPersistencePermission()
    {
        (int, int)[] ratios = [(6, 6), (4, 6), (4, 5), (3, 3), (3, 12), (2, 2), (5, 6), (5, 5), (4, 12)];
        var items = ratios.Select((r, n) => Item($"04/07 SHOP{(char)('A' + n)}{r.Item1:00}/{r.Item2:00} 10.00") with
        { OriginalDescription = $"SHOP{(char)('A' + n)}{r.Item1:00}/{r.Item2:00}", InstallmentNumber = r.Item1, InstallmentCount = r.Item2,
            Section = n < 6 ? "CurrentInvoice" : "FutureInstallments", IncludedInCurrentTotal = n < 6 }).ToArray();
        var native = "Lançamentos: compras e saques\n" + string.Join('\n', items[..6].Select(i => i.Evidence))
            + "\nTotal 60.00\nCompras parceladas - próximas faturas\n" + string.Join('\n', items[6..].Select(i => i.Evidence));
        var result = await Extract(Hybrid(native, Region(Legal, 600)), items, "60.00");
        Assert.Equal(AgentExtractionAssessment.Reliable, result.Assessment); Assert.True(result.OutputCoverage!.RecognitionComplete);
        Assert.Equal(items, result.Primary!.Items); Assert.All(result.Primary.Items, i => Assert.Null(i.TransactionDate));
        Assert.True(result.Suitability!.FaithfulExtraction); Assert.False(result.Suitability.PurchaseDatesComplete);
        Assert.True(result.Suitability.KnownInstallmentAllowsMissingTransactionDate); Assert.False(result.Suitability.PersistenceEligible);
        Assert.Contains("PurchaseDateRequired", result.Suitability.PersistenceBlockers);
        Assert.Equal(9, result.PurchaseDocumentValidation!.Diagnostics.Count(d => d.Code == "PurchaseDateYearUnproven"));
        var safe = JsonSerializer.Serialize(result.PurchaseDocumentValidation);
        Assert.DoesNotContain("SHOP", safe); Assert.DoesNotContain("10.00", safe); Assert.DoesNotContain("contrato", safe);
    }

    [Fact]
    public void RegionalOcrKeepsSourceCoordinatesAndRejectsBadConfidenceOrBounds()
    {
        const string header = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";
        var region = new StatementVisualRegion(2, 7, 10, 20, 100, 100);
        var tsv = header + "5\t1\t1\t1\t1\t1\t4\t6\t20\t10\t95\tTotal\n";
        var read = LocalStatementPageReader.ReadRegionTsv(region, tsv, 10, 20, 2);
        Assert.Equal("Readable", read.Status); Assert.Equal(new CreditCardPdfExtractedWord("Total", 12, 23, 22, 28), Assert.Single(read.Words));
        Assert.Equal(2, read.Page); Assert.Equal(7, read.Index);
        Assert.Equal("Unreadable", LocalStatementPageReader.ReadRegionTsv(region, tsv.Replace("95", "10"), 10, 20, 2).Status);
        Assert.Equal("Unreadable", LocalStatementPageReader.ReadRegionTsv(region, tsv, 1000, 20, 2).Status);
        var json = JsonSerializer.Serialize(read); Assert.DoesNotContain("Total", json); Assert.DoesNotContain("Words", json); Assert.DoesNotContain("Text", json);
    }

    [Fact]
    public void LegalProseMentioningInstallmentsIsNotAHeadingButAnEmbeddedPurchaseRemainsEligible()
    {
        var prose = Region(Legal + "\nO contrato contempla compras parceladas com ou sem juros conforme as condicoes gerais.");
        Assert.Equal("ExcludedLegal", StatementVisualRegionText.Classify(prose).Classification);
        var actualRow = Region(Legal + "\n04/07 SHOP03/12 10.00");
        Assert.Equal("PurchaseRows", StatementVisualRegionText.Classify(actualRow).Classification);
        var iso = Region(Legal + "\n2026-07-04 SHOP03/12 10.00");
        Assert.Equal("PurchaseRows", StatementVisualRegionText.Classify(iso).Classification);
        var undated = Region(Legal + "\nA VERY LONG MERCHANT DESCRIPTION WITH MANY WORDS SHOP03/12 10.00");
        Assert.Equal("PurchaseRows", StatementVisualRegionText.Classify(undated).Classification);
    }

    [Fact]
    public void ContactInformationAndVerticalLabelsNeedPositiveOcrAndCannotHideMonetaryRows()
    {
        var contact = Region("SAC 0800 700 0031\nOuvidoria 0800 570 0011") with { Status = "NativeConflict" };
        Assert.Equal("ExcludedContactInformation", StatementVisualRegionText.Classify(contact, Words("SAC", 200)).Classification);
        Assert.False(StatementVisualRegionText.Classify(contact).Complete);
        Assert.False(StatementVisualRegionText.Classify(contact, Words("04/07 SHOP03/12 10.00", 200)).Complete);
        Assert.False(StatementVisualRegionText.Classify(contact with { Status = "Unreadable" }).Complete);
        Assert.False(StatementVisualRegionText.Classify(contact with { Words = Words("SAC Ouvidoria 04/07 SHOP03/12 10.00", 200) }).Complete);
        var label = Region("GENERIC BRAND") with { Right = 38, Bottom = 342 };
        Assert.Equal("ExcludedVerticalLabel", StatementVisualRegionText.Classify(label).Classification);
        Assert.False(StatementVisualRegionText.Classify(label with { Status = "Unreadable" }).Complete);
    }

    [Fact]
    public async Task OverlappingRasterRepresentationsDoNotCreateAnExtraSourceOccurrence()
    {
        var line = "04/07 SHOP03/12 10.00";
        var region = Region(line);
        var page = StatementVisualRegionText.Merge(Native("Lançamentos: compras e saques\nTotal 10.00") with
        { VisualRegions = [region, region with { Index = 1, Left = 9, Right = 591 }] });
        Assert.Equal("NativeOverlay", page.VisualRegions[1].Status);
        var result = await Extract(page, [Item(line)], "10.00");
        Assert.True(result.OutputCoverage!.RecognitionComplete); Assert.Equal(1, result.PurchaseDocumentValidation!.Pages[0].EligibleRows);
    }

    [Fact]
    public void HeaderLabelMustBeAboveTheFirstFinancialAnchorNotJustOneLaterHeading()
    {
        var region = Region("GENERIC PRODUCT", 50) with { Status = "NativeConflict" };
        Assert.Equal("ExcludedHeaderLabel", StatementVisualRegionText.Classify(region, Words("Vencimento", 200)).Classification);
        var anchors = Words("Vencimento", 10).Concat(Words("Lançamentos: compras e saques", 400)).ToArray();
        Assert.False(StatementVisualRegionText.Classify(region, anchors).Complete);
        Assert.False(StatementVisualRegionText.Classify(region with { Words = Words("04/07 SHOP03/12 10.00", 50) }, Words("Vencimento", 200)).Complete);
    }

    [Fact]
    public void RotatedOcrCoordinatesReturnToTheOriginalPageAndMissingDateContractStaysOptional()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n5\t1\t1\t1\t1\t1\t4\t6\t20\t10\t95\tLabel\n";
        var region = new StatementVisualRegion(2, 1, 10, 20, 100, 100);
        var read = LocalStatementPageReader.ReadRegionTsv(region, tsv, 10, 20, 2, 100);
        Assert.Equal(new CreditCardPdfExtractedWord("Label", 13, 58, 18, 68), Assert.Single(read.Words));
        var obligation = Saldo360.Domain.CreditCards.FinancialCreditCardImportedInstallment.CreateKnown(
            Guid.NewGuid(), Guid.NewGuid(), null, "Synthetic known obligation", 10m, 3, 12, new(2026, 10, 1), null,
            "Synthetic", "row-1", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        Assert.Null(obligation.TransactionDate); Assert.Null(obligation.InvoiceId);
        // Contract inspection only; this test does not invoke a repository or financial writer.
    }

    private static CreditCardStatementAgentItem Item(string evidence) => new("SHOP03/12", "10.00", "Debit", "Installment", 3, 12,
        "CurrentInvoice", true, 1, evidence, "High", null);
    private static CreditCardPdfExtractedWord[] Words(string text, double top) => text.Split('\n').SelectMany((line, row) =>
    {
        double x = 10;
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(token =>
        { var word = new CreditCardPdfExtractedWord(token, x, top + row * 30, x + token.Length * 5, top + row * 30 + 10); x = word.Right + 5; return word; }).ToArray();
    }).ToArray();
    private static StatementVisualRegion Region(string text, double top = 200) => new(1, 0, 10, top, 590, top + text.Split('\n').Length * 30)
    { Status = "Readable", Words = Words(text, top), Text = text };
    private static CreditCardPdfExtractedPage Native(string text) => new(1, text, Words(text, 10))
    { PageWidth = 595, PageHeight = 842, ProcessingStatus = "NativeWithImage", ImagePng = Png };
    private static CreditCardPdfExtractedPage Hybrid(string text, StatementVisualRegion region) => StatementVisualRegionText.Merge(Native(text) with
    { VisualRegions = [region], HasUntranscribedImageRegion = true });
    private static async Task<CreditCardStatementExtractionResult> Extract(CreditCardPdfExtractedPage page, CreditCardStatementAgentItem[] items, string total = "0.00")
    {
        var facts = new CreditCardStatementAgentDocument("2.0", new(null, null, "2026-10", null, "2026-10-13", total), items);
        var provider = new Provider(JsonSerializer.Serialize(facts, CreditCardStatementAgentSchema.Json));
        var result = await new CreditCardStatementExtractionOrchestrator(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary }, new ForbiddenParser(), new(provider))
            .ExtractAsync(new("synthetic.pdf", new([page]), null) { PurchasesOnly = true, ValidatePurchaseDocument = true, BatchPages = true, RequireAgent = true, AllowCorrectionRetry = false }, default);
        Assert.Equal(1, provider.Calls); Assert.False(result.ParserShadowExecuted); return result;
    }
    private sealed class Provider(string facts) : ICreditCardStatementAgentProvider
    { public int Calls; public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct) { Calls++; return Task.FromResult(facts); } }
    private sealed class ForbiddenParser : IParserBackedCreditCardStatementExtractorAgent
    { public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Traditional parser forbidden"); }
}

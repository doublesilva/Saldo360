using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Saldo360.Tests.CreditCards;

// Explicitly executed inside Dockerfile.ocr-tests with native dependencies. Never silently skipped.
[Trait("Category", "INFRA")]
[Trait("Dependency", "NativeOCR")]
[Trait("Area", "Documents")]
[Trait("Area", "Infrastructure")]
public sealed class RealOcrToolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePageRasterRegionIsOcrReadWithOriginalCoordinates(bool purchase)
    {
        var reader = new LocalStatementPageReader(Environment.GetEnvironmentVariable("SALDO360_TEST_OCR_DOCKER_IMAGE"));
        var raster = new PdfDocumentBuilder();
        var font = raster.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var imagePage = raster.AddPage(595, 200);
        string[] lines = purchase ? ["Lancamentos: compras e saques", "04/07 LOJA03/12 10,00"]
            : ["Pagamento da fatura: observe o contrato.", "Encargos e juros seguem as condicoes gerais.", "Operacoes de financiamento exigem atencao."];
        foreach (var (line, index) in lines.Select((line, index) => (line, index)))
            imagePage.AddText(line, 15, new(30, 160 - index * 35), font);
        var image = await reader.ReadAsync(raster.Build(), new(1, "synthetic native image"), false, default);
        Assert.NotNull(image.ImagePng);
        var hybrid = new PdfDocumentBuilder();
        var nativeFont = hybrid.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = hybrid.AddPage(595, 842);
        page.AddText("Native document header and financial table outside the raster region.", 12, new(30, 800), nativeFont);
        page.AddPng(image.ImagePng!, new(30, 360, 330, 460)); // <15% of the page: cannot be ignored as small.
        await using var pdf = new MemoryStream(hybrid.Build());
        var read = await new CreditCardPdfTextExtractor(reader, batchPages: true, renderAllPages: true).ExtractAsync(pdf, 50, [], default);
        var extracted = Assert.Single(read.Pages); var region = Assert.Single(extracted.VisualRegions);
        Assert.Equal("Readable", region.Status); Assert.False(extracted.HasUntranscribedImageRegion);
        Assert.Equal(1, region.Page); Assert.Equal(30, region.Left, 1); Assert.Equal(382, region.Top, 1);
        Assert.Equal(330, region.Right, 1); Assert.Equal(482, region.Bottom, 1);
        Assert.NotEmpty(region.Words); Assert.All(region.Words, w => { Assert.InRange(w.Top, 380, 484); Assert.InRange(w.X, 28, 332); });
        Assert.Contains("Native document header", extracted.Text); Assert.NotNull(extracted.NativeWords);
        Assert.Equal(purchase ? "PurchaseRows" : "ExcludedLegal", StatementVisualRegionText.Classify(region).Classification);
    }

    [Fact]
    public async Task RealPopplerAndPortugueseTesseractRecoverProtectedSyntheticPage()
    {
        var source = (string)typeof(CreditCardPdfPreviewTests).GetField("ProtectedPdfBase64", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
        using var document = PdfDocument.Open(Convert.FromBase64String(source), new ParsingOptions { Passwords = ["12345"] });
        var builder = new PdfDocumentBuilder(); builder.AddPage(document, 1);
        var native = document.GetPage(1).Text;
        // Optional host invocation exercises the lab's Docker wrapper; unset inside the native-tools image.
        var reader = new LocalStatementPageReader(Environment.GetEnvironmentVariable("SALDO360_TEST_OCR_DOCKER_IMAGE"));
        var visual = await reader.ReadAsync(builder.Build(), new(1, native), false, default);
        Assert.Equal("NativeWithImage", visual.ProcessingStatus); Assert.Equal(native, visual.Text); Assert.NotNull(visual.ImagePng);
        var recovered = await reader.ReadAsync(builder.Build(), new(1, ""), true, default);
        Assert.True(recovered.ProcessingStatus == "OCRWithImage", recovered.PreparationFailureCode); Assert.Equal("OCR", recovered.TextSource); Assert.NotEmpty(recovered.Text);
        Assert.NotNull(recovered.ImagePng); Assert.Equal(1, recovered.Number);
        var mixed = new PdfDocumentBuilder(); mixed.AddPage(document, 1);
        mixed.AddPage(595, 842).AddPng(visual.ImagePng!, new UglyToad.PdfPig.Core.PdfRectangle(0, 0, 595, 842));
        await using var pdf = new MemoryStream(mixed.Build());
        var extracted = await new CreditCardPdfTextExtractor(reader).ExtractAsync(pdf, 50, [], default);
        Assert.Equal(2, extracted.Pages.Count);
        Assert.Equal("Native", extracted.Pages[0].TextSource); Assert.True(extracted.Pages[1].TextSource == "OCR", extracted.Pages[1].PreparationFailureCode);
        Assert.Equal(2, extracted.Pages[1].Number); Assert.Equal("OCRWithImage", extracted.Pages[1].ProcessingStatus);
        Assert.NotEmpty(extracted.Pages[1].Text);

        var allPages = new PdfDocumentBuilder(); allPages.AddPage(document, 1); allPages.AddPage(document, 1);
        allPages.AddPage(595, 842).AddPng(visual.ImagePng!, new UglyToad.PdfPig.Core.PdfRectangle(0, 0, 595, 842));
        await using var tables = new MemoryStream(allPages.Build());
        var lab = await new CreditCardPdfTextExtractor(reader, batchPages: true, renderAllPages: true).ExtractAsync(tables, 50, [], default);
        Assert.Equal(3, lab.Pages.Count); Assert.All(lab.Pages, page => Assert.NotNull(page.ImagePng));
        Assert.All(document.GetPage(1).GetWords(), word => Assert.Contains(word.Text, lab.Pages[1].Text, StringComparison.Ordinal));
        Assert.Equal("Native", lab.Pages[1].TextSource);
        Assert.Equal("NativeWithImage", lab.Pages[1].ProcessingStatus); // Native table on page 2, not just the cover.
        Assert.Equal("OCR", lab.Pages[2].TextSource); Assert.Equal("OCRWithImage", lab.Pages[2].ProcessingStatus);
    }
}

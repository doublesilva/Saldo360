using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Saldo360.Tests.CreditCards;

// Explicitly executed inside Dockerfile.ocr-tests with native dependencies. Never silently skipped.
public sealed class RealOcrToolTests
{
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
    }
}

using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
public sealed class CreditCardPdfPreviewTests
{
    [Fact]
    public async Task SharedReaderUnlocksRealSyntheticPdfWithoutChangingExtractedFinancialItems()
    {
        await using var pdf = new MemoryStream(Convert.FromBase64String(ProtectedPdfBase64));
        var read = await new PdfPasswordReader(_extractor, new ReaderPasswords()).ReadAsync(pdf,
            new(Guid.NewGuid(), Guid.NewGuid(), Card: new("Synthetic", Guid.NewGuid())), null, default);
        await using var direct = new MemoryStream(Convert.FromBase64String(ProtectedPdfBase64));
        var expected = await _extractor.ExtractAsync(direct, 50, ["12345"], default);
        Assert.True(read.Unlocked);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(_extractor.Parse(expected, "synthetic.pdf", "2026-10", null)),
            System.Text.Json.JsonSerializer.Serialize(_extractor.Parse(read.Document, "synthetic.pdf", "2026-10", null)));
    }
    private sealed class ReaderPasswords : ICreditCardPdfPasswordProvider
    {
        public Task<CreditCardPdfPasswordResolution> ResolveAsync(Guid holder, string issuer, CancellationToken ct) => Task.FromResult(new CreditCardPdfPasswordResolution(["123", "12345"]));
    }
    private const string ProtectedPdfBase64 = "JVBERi0xLjMKJeLjz9MKMSAwIG9iago8PAovUHJvZHVjZXIgPDNhYTcyNzNmODM+Cj4+CmVuZG9iagoyIDAgb2JqCjw8Ci9UeXBlIC9QYWdlcwovQ291bnQgMgovS2lkcyBbIDQgMCBSIDggMCBSIF0KPj4KZW5kb2JqCjMgMCBvYmoKPDwKL1R5cGUgL0NhdGFsb2cKL1BhZ2VzIDIgMCBSCj4+CmVuZG9iago0IDAgb2JqCjw8Ci9Db250ZW50cyA1IDAgUgovTWVkaWFCb3ggWyAwIDAgNTk1LjI3NTYgODQxLjg4OTggXQovUmVzb3VyY2VzIDw8Ci9Gb250IDYgMCBSCi9Qcm9jU2V0IFsgL1BERiAvVGV4dCAvSW1hZ2VCIC9JbWFnZUMgL0ltYWdlSSBdCj4+Ci9Sb3RhdGUgMAovVHJhbnMgPDwKPj4KL1R5cGUgL1BhZ2UKL1BhcmVudCAyIDAgUgo+PgplbmRvYmoKNSAwIG9iago8PAovRmlsdGVyIFsgL0FTQ0lJODVEZWNvZGUgL0ZsYXRlRGVjb2RlIF0KL0xlbmd0aCAxNDcKPj4Kc3RyZWFtCrsbzPE6Y1iccKJknO3JCw4DuHXWqd4kGAWwwivkJ58Tn1BzMqzauLRLovoMRdTErhs9x1B5dUHBFrPoHerV+Sn7sUlf8PTxlfDdP7q6fd7GAEn482VszT4GUmfIxNA67QgGS4zcdNx3sT1KoVr8gwBtse/hGBCedB3BUfQRUA0mPMo94yf8YwR0uuaFAlUI7VLK9AplbmRzdHJlYW0KZW5kb2JqCjYgMCBvYmoKPDwKL0YxIDcgMCBSCj4+CmVuZG9iago3IDAgb2JqCjw8Ci9CYXNlRm9udCAvSGVsdmV0aWNhCi9FbmNvZGluZyAvV2luQW5zaUVuY29kaW5nCi9OYW1lIC9GMQovU3VidHlwZSAvVHlwZTEKL1R5cGUgL0ZvbnQKPj4KZW5kb2JqCjggMCBvYmoKPDwKL0NvbnRlbnRzIDkgMCBSCi9NZWRpYUJveCBbIDAgMCA1OTUuMjc1NiA4NDEuODg5OCBdCi9SZXNvdXJjZXMgPDwKL0ZvbnQgNiAwIFIKL1Byb2NTZXQgWyAvUERGIC9UZXh0IC9JbWFnZUIgL0ltYWdlQyAvSW1hZ2VJIF0KPj4KL1JvdGF0ZSAwCi9UcmFucyA8PAo+PgovVHlwZSAvUGFnZQovUGFyZW50IDIgMCBSCj4+CmVuZG9iago5IDAgb2JqCjw8Ci9GaWx0ZXIgWyAvQVNDSUk4NURlY29kZSAvRmxhdGVEZWNvZGUgXQovTGVuZ3RoIDExMQo+PgpzdHJlYW0K/F7gnkVdYPvBfbjTxq+550Cs2eHbJi4MwoXm/0bMSTOQ2nSzZM8dDin/cCcKNkmPVF2rV4Wc3TIqtGTqKXKNd2WVGPJzr4UnNlqZqSAm9wdhY09P5MbBgh9hO6iUatLrqYuHL9T0n3M95N9nWa48CmVuZHN0cmVhbQplbmRvYmoKMTAgMCBvYmoKPDwKL1YgMgovUiAzCi9MZW5ndGggMTI4Ci9QIDQyOTQ5NjcyOTIKL0ZpbHRlciAvU3RhbmRhcmQKL08gPDE3Mzc2NDYyOWU5MDY2YmViMjZmMGRlYmM3ZDBhNzFlNDBkMzg2MjQzYmMyODQzNWNjOTgzNWY3YmY1NTI5NDM+Ci9VIDw4ODc4OTBjYjcwZDFhZGE2MWM1MGEzYzY4ZWIzNGRmZjI4YmY0ZTVlNGU3NThhNDE2NDAwNGU1NmZmZmEwMTA4Pgo+PgplbmRvYmoKeHJlZgowIDExCjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMDU5IDAwMDAwIG4gCjAwMDAwMDAxMjQgMDAwMDAgbiAKMDAwMDAwMDE3MyAwMDAwMCBuIAowMDAwMDAwMzcyIDAwMDAwIG4gCjAwMDAwMDA2MTAgMDAwMDAgbiAKMDAwMDAwMDY0MSAwMDAwMCBuIAowMDAwMDAwNzQ4IDAwMDAwIG4gCjAwMDAwMDA5NDcgMDAwMDAgbiAKMDAwMDAwMTE0OSAwMDAwMCBuIAp0cmFpbGVyCjw8Ci9TaXplIDExCi9Sb290IDMgMCBSCi9JbmZvIDEgMCBSCi9JRCBbIDwzODMwMzQ2MTMwMzg2MjM4MzE2NjYzNjUzNzMzNjIzNjM4MzkzMjYyNjQzNTM0NjU2MjMxMzYzODM4MzczMTYyPiA8MzgzMDM0NjEzMDM4NjIzODMxNjY2MzY1MzczMzYyMzYzODM5MzI2MjY0MzUzNDY1NjIzMTM2MzgzODM3MzE2Mj4gXQovRW5jcnlwdCAxMCAwIFIKPj4Kc3RhcnR4cmVmCjEzNjUKJSVFT0YK";
    private readonly CreditCardPdfTextExtractor _extractor = new();

    [Fact]
    public void ParsesPurchasesInstallmentsPaymentsCreditsAndFeesConservatively()
    {
        var document = new CreditCardPdfExtractedDocument([
            new CreditCardPdfExtractedPage(1, "Competência: 2026-10\nVencimento: 20/10/2026\nTotal da fatura: R$ 1.500,00\nLOJA X 02/06 150,00\n05/10/2026 PAGAMENTO 400,00\n06/10/2026 CRÉDITO 10,00\n07/10/2026 ESTORNO 20,00\n08/10/2026 JUROS 5,25"),
            new CreditCardPdfExtractedPage(2, "09/10/2026 Linha sem classificação 1.234,56")]);

        var preview = _extractor.Parse(document, "fatura.pdf", null, null);

        Assert.Equal(2, preview.PageCount);
        Assert.Equal("2026-10", preview.Statement.Competence);
        Assert.Equal("2026-10-20", preview.Statement.DueDate);
        Assert.Equal("1500.00", preview.Statement.TotalAmount);
        Assert.Contains(preview.Items, item => item.Type == "Installment" && item.InstallmentNumber == 2 && item.InstallmentCount == 6 && item.Page == 1);
        Assert.Contains(preview.Items, item => item.Type == "Payment");
        Assert.Contains(preview.Items, item => item.Type == "Credit");
        Assert.Contains(preview.Items, item => item.Type == "Refund");
        Assert.Contains(preview.Items, item => item.Type == "Fee");
        Assert.DoesNotContain(preview.Items, item => item.Type == "Purchase" && item.Description.Contains("02/06", StringComparison.Ordinal));
    }

    [Fact]
    public void PreservesExactBrazilianDecimalsAndUnknownLines()
    {
        var preview = _extractor.Parse(new CreditCardPdfExtractedDocument([
            new CreditCardPdfExtractedPage(3, "12/10/2026 MERCADO 1.234,56\nTexto sem data ou valor")]), "fatura.pdf", "2026-10", null);

        var item = Assert.Single(preview.Items);
        Assert.Equal("1234.56", item.Amount);
        Assert.Equal("2026-10-12", item.TransactionDate);
        Assert.Equal(3, item.Page);
    }

    [Fact]
    public async Task OpensSyntheticProtectedPdfAndPreservesPagesAndItems()
    {
        await using var protectedPdf = new MemoryStream(Convert.FromBase64String(ProtectedPdfBase64));
        var required = await Assert.ThrowsAsync<CreditCardPdfReadException>(() =>
            _extractor.ExtractAsync(protectedPdf, 50, [], CancellationToken.None));
        Assert.Equal(CreditCardPdfReadFailure.PasswordRequired, required.Failure);

        var rejected = await Assert.ThrowsAsync<CreditCardPdfReadException>(() =>
            _extractor.ExtractAsync(protectedPdf, 50, ["999", "99999"], CancellationToken.None));
        Assert.Equal(CreditCardPdfReadFailure.PasswordRejected, rejected.Failure);

        var document = await _extractor.ExtractAsync(protectedPdf, 50, ["000", "12345"], CancellationToken.None);
        var preview = _extractor.Parse(document, "fatura-sintetica.pdf", "2026-10", null);

        Assert.Equal(2, document.Pages.Count);
        Assert.Contains(preview.Items, item => item.Page == 1 && item.Amount == "123.45" && item.SourceText.Contains("MERCADO", StringComparison.Ordinal));
        Assert.Contains(preview.Items, item => item.Page == 2 && item.Type == "Fee" && item.Amount == "5.25");
    }

    [Fact]
    public async Task ReportsMalformedPdfAsCorruptInsteadOfPasswordProtected()
    {
        await using var malformed = new MemoryStream("not-a-pdf"u8.ToArray());

        var exception = await Assert.ThrowsAsync<CreditCardPdfReadException>(() =>
            _extractor.ExtractAsync(malformed, 50, [], CancellationToken.None));

        Assert.Equal(CreditCardPdfReadFailure.Corrupt, exception.Failure);
    }

}

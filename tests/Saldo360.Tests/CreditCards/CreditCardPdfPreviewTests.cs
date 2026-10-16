using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class CreditCardPdfPreviewTests
{
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

}

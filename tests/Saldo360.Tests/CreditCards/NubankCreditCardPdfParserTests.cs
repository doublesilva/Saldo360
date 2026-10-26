using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "REGRESSION")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
public sealed class NubankCreditCardPdfParserTests
{
    private readonly CreditCardPdfTextExtractor _extractor = new();

    [Fact]
    public void ParsesObservedLayoutAndReconcilesExactComposition()
    {
        var preview = _extractor.Parse(CreateDocument(), "nubank-sintetica.pdf", null, null);

        Assert.Equal("Nubank", preview.Parser);
        Assert.Equal("Reconciled", preview.Reconciliation.Status);
        Assert.Equal("785.33", preview.Reconciliation.OfficialTotal);
        Assert.Equal("785.33", preview.Reconciliation.ComponentsTotal);
        Assert.Equal("0.00", preview.Reconciliation.Difference);
        Assert.Equal("2026-10", preview.Statement.Competence);
        Assert.Equal("2026-10-05", preview.Statement.ClosingDate);
        Assert.Equal("2026-10-13", preview.Statement.DueDate);
        Assert.Null(preview.Statement.CardLastDigits);
        Assert.Contains(preview.Warnings, warning => warning.Contains("mais de um final", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(6, preview.Items.Count(item => item.Type is "Purchase" or "InternationalPurchase" or "Installment"));
        Assert.Equal(4, preview.Items.Count(item => item.Type == "Installment"));
        Assert.Contains(preview.Items, item => item.Type == "InternationalPurchase" && item.Amount == "114.85");
        Assert.Contains(preview.Items, item => item.Type == "Fee" && item.Amount == "4.02");
        Assert.Contains(preview.Items, item => item.Type == "Payment" && item.Amount == "-339.52" && item.Nature == "Credit");
        Assert.Contains(preview.Items, item => item.Type == "PreviousBalance" && item.Amount == "339.52");
        Assert.All(preview.Items.Where(item => item.Type is "PreviousBalance" or "Payment"), item => Assert.False(item.IncludedInCurrentTotal));
    }

    [Fact]
    public void UnknownFinancialLinePreventsReconciliation()
    {
        var preview = _extractor.Parse(CreateDocument(includeUnknown: true), "nubank-sintetica.pdf", null, null);
        Assert.Equal("NeedsReview", preview.Reconciliation.Status);
        Assert.True(preview.Reconciliation.HasUnknownCurrentItems);
    }

    [Fact]
    public void ResolverKeepsItauAndGenericSelectionStable()
    {
        var generic = _extractor.Parse(new([new(1, "DOCUMENTO DESCONHECIDO", Line(1, 100, "01/10/2026 ITEM 10,00"))]), "outro.pdf", null, null);
        Assert.Equal("Generic", generic.Parser);

        var itau = _extractor.Parse(new([new(1, "Resumo da fatura em R$ Total dos lançamentos atuais Itaú Cartões", [])]), "itau.pdf", null, null);
        Assert.Equal("ItauMastercard", itau.Parser);
    }

    private static CreditCardPdfExtractedDocument CreateDocument(bool includeUnknown = false)
    {
        var page1 = new CreditCardPdfExtractedPage(1, "nu Esta é a sua fatura de outubro, no valor de R$ 785,33 Data de vencimento: 13 OUT 2026", [
            .. Line(1, 50, "Olá, TITULAR ANONIMO. Esta é a sua fatura de outubro, no valor de R$ 785,33"),
            .. Line(1, 70, "Data de vencimento: 13 OUT 2026"),
        ]);
        var page2 = new CreditCardPdfExtractedPage(2, "NU PAGAMENTOS S.A", Line(2, 50, "NU PAGAMENTOS S.A"));
        var page3 = new CreditCardPdfExtractedPage(3, "FATURA 13 OUT 2026 EMISSÃO E ENVIO 05 OUT 2026", [
            .. Line(3, 50, "FATURA 13 OUT 2026"), .. Line(3, 70, "EMISSÃO E ENVIO 05 OUT 2026")]);
        var page4 = new CreditCardPdfExtractedPage(4, "RESUMO DA FATURA ATUAL PRÓXIMAS FATURAS", [
            .. Line(4, 40, "RESUMO DA FATURA ATUAL"),
            .. Line(4, 60, "Fatura anterior"), .. Line(4, 61, "R$ 339,52", 500),
            .. Line(4, 80, "Pagamento recebido"), .. Line(4, 81, "−R$ 339,52", 500),
            .. Line(4, 100, "Total de compras de todos os cartões, 05 SET a 05 OUT"), .. Line(4, 101, "R$ 781,31", 500),
            .. Line(4, 120, "IOF de compras internacionais"), .. Line(4, 121, "R$ 4,02", 500),
            .. Line(4, 140, "Total a pagar"), .. Line(4, 141, "R$ 785,33", 500),
            .. Line(4, 180, "PRÓXIMAS FATURAS"),
        ]);
        var page5Words = new List<CreditCardPdfExtractedWord>();
        page5Words.AddRange(Line(5, 100, "05 SET •••• 5418 LOJA A - Parcela 2/3 R$ 41,33"));
        page5Words.AddRange(Line(5, 120, "05 SET •••• 5418 LOJA B - Parcela 2/3 R$ 222,61"));
        page5Words.AddRange(Line(5, 140, "06 SET •••• 5418 LOJA C - Parcela 1/2 R$ 143,86"));
        page5Words.AddRange(Line(5, 160, "08 SET •••• 5418 RESTAURANTE R$ 146,88"));
        page5Words.AddRange(Line(5, 180, "14 SET •••• 5418 PAPELARIA - Parcela 1/3 R$ 111,78"));
        page5Words.AddRange(Line(5, 200, "02 OUT •••• 0145 SERVICO EXTERIOR R$ 114,85"));
        page5Words.AddRange(Line(5, 210, "BRL 110.00 = USD 21.14"));
        page5Words.AddRange(Line(5, 220, "02 OUT IOF de SERVICO EXTERIOR R$ 4,02"));
        page5Words.AddRange(Line(5, 240, "14 SET Pagamento em 14 SET −R$ 339,52"));
        if (includeUnknown) page5Words.AddRange(Line(5, 260, "03 OUT LINHA FINANCEIRA R$ INVALIDO"));
        var page5 = new CreditCardPdfExtractedPage(5, "TRANSAÇÕES DE 05 SET A 05 OUT", page5Words);
        return new([page1, page2, page3, page4, page5]);
    }

    private static IReadOnlyList<CreditCardPdfExtractedWord> Line(int page, double top, string text, double startX = 100)
    {
        var x = startX; var words = new List<CreditCardPdfExtractedWord>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var width = Math.Max(8, token.Length * 4.5);
            words.Add(new(token, x, top, x + width, top + 8)); x += width + 4;
        }
        return words;
    }
}

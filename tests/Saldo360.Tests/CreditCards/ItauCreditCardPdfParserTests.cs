using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class ItauCreditCardPdfParserTests
{
    private readonly CreditCardPdfTextExtractor _extractor = new();

    [Fact]
    public void ReconcilesFaithfulSyntheticTwoColumnLayoutAndPreservesAuditFields()
    {
        var preview = _extractor.Parse(CreateDocument("143,00"), "fatura-itau-sintetica.pdf", null, null);

        Assert.Equal("ItauMastercard", preview.Parser);
        Assert.Equal("Reconciled", preview.Reconciliation.Status);
        Assert.Equal("143.00", preview.Reconciliation.OfficialTotal);
        Assert.Equal("143.00", preview.Reconciliation.ComponentsTotal);
        Assert.Equal("0.00", preview.Reconciliation.Difference);
        Assert.Equal("2026-10-15", preview.Statement.DueDate);
        Assert.Equal("2026-10-08", preview.Statement.ClosingDate);
        Assert.Equal("TESTE ANONIMO", preview.Statement.HolderName);
        Assert.Equal("9876", preview.Statement.CardLastDigits);

        var credit = Assert.Single(preview.Items, item => item.Type == "Credit");
        Assert.Equal("-10.00", credit.Amount);
        Assert.Equal("Credit", credit.Nature);
        Assert.True(credit.IncludedInCurrentTotal);
        Assert.Equal(2, credit.Page);
        Assert.Contains("CREDITO TESTE", credit.SourceText, StringComparison.Ordinal);

        var installment = Assert.Single(preview.Items, item => item.Type == "Installment" && item.OriginalDescription == "LOJA TESTE");
        Assert.Equal(2, installment.InstallmentNumber);
        Assert.Equal(5, installment.InstallmentCount);
        Assert.Equal("LOJA TESTE", installment.NormalizedDescription);

        Assert.Equal(2, preview.Items.Count(item => item.Type == "FutureInstallment" && !item.IncludedInCurrentTotal));
        Assert.Contains(preview.Items, item => item.Type == "Fee" && item.Amount == "2.00" && item.Page == 3);
        Assert.Contains(preview.Items, item => item.Type == "Payment" && item.Amount == "-100.00");
        Assert.Contains(preview.Items, item => item.Type == "PreviousBalance" && item.Amount == "100.00");
    }

    [Fact]
    public void MarksDifferentCompositionAsDivergent()
    {
        var preview = _extractor.Parse(CreateDocument("144,00"), "fatura.pdf", null, null);

        Assert.Equal("Divergent", preview.Reconciliation.Status);
        Assert.Equal("1.00", preview.Reconciliation.Difference);
        Assert.Contains(preview.Warnings, warning => warning.Contains("diverge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NeverReconcilesUnknownDatedCurrentLine()
    {
        var document = CreateDocument("143,00", includeUnknown: true);
        var preview = _extractor.Parse(document, "fatura.pdf", null, null);

        Assert.Equal("NeedsReview", preview.Reconciliation.Status);
        Assert.True(preview.Reconciliation.HasUnknownCurrentItems);
        Assert.Contains(preview.Items, item => item.Type == "Unknown" && item.IncludedInCurrentTotal && item.Page == 2);
    }

    private static CreditCardPdfExtractedDocument CreateDocument(string officialTotal, bool includeUnknown = false)
    {
        const string page1Text = """
            Black
            TESTE ANONIMO
            Titular
            1111.XXXX.XXXX.9876
            Cartão
            Resumo da fatura em R$
            Vencimento: 15/10/2026
            Emissão: 08/10/2026
            Total dos lançamentos atuais 143,00
            Itaú Cartões
            """;
        var page1 = new CreditCardPdfExtractedPage(1, $"{page1Text}\nTotal desta fatura {officialTotal}", [
            .. Line(1, 100, "Total da fatura anterior 100,00"),
            .. Line(1, 120, "Saldo financiado 0,00"),
        ]);
        var page2Words = new List<CreditCardPdfExtractedWord>();
        page2Words.AddRange(Line(0, 200, "15/09 PAGAMENTO -100,00"));
        page2Words.AddRange(Line(0, 300, "Lançamentos: compras e saques"));
        page2Words.AddRange(Line(0, 320, "31/08 LOJA TESTE 02/05 40,00"));
        page2Words.AddRange(Line(1, 320, "08/09 COMPRA DIREITA 50,00"));
        page2Words.AddRange(Line(0, 340, "09/09 CREDITO TESTE -10,00"));
        if (includeUnknown) page2Words.AddRange(Line(0, 360, "12/09 LINHA SEM VALOR"));
        var page2 = new CreditCardPdfExtractedPage(2, "Pagamentos efetuados\nLançamentos: compras e saques", page2Words);

        var page3Words = new List<CreditCardPdfExtractedWord>();
        page3Words.AddRange(Line(0, 100, "10/09 COMPRA FINAL 20,00"));
        page3Words.AddRange(Line(0, 200, "Lançamentos no cartão 100,00"));
        page3Words.AddRange(Line(0, 250, "Lançamentos internacionais"));
        page3Words.AddRange(Line(0, 280, "11/09 COMPRA EXTERIOR 10,00"));
        page3Words.AddRange(Line(0, 300, "Repasse de IOF em R$ 2,00"));
        page3Words.AddRange(Line(0, 350, "Lançamentos: produtos e serviços"));
        page3Words.AddRange(Line(0, 380, "12/09 SERVICO TESTE 01/03 30,00"));
        page3Words.AddRange(Line(0, 400, "Lançamentos produtos e serviços 30,00"));
        page3Words.AddRange(Line(0, 500, "Compras parceladas - próximas faturas"));
        page3Words.AddRange(Line(0, 520, "13/09 FUTURA ESQUERDA 03/06 25,00"));
        page3Words.AddRange(Line(1, 100, "14/09 FUTURA DIREITA 04/06 15,00"));
        page3Words.AddRange(Line(1, 250, "Encargos cobrados nesta fatura"));
        page3Words.AddRange(Line(1, 280, "IOF de financiamento 1,00"));
        page3Words.AddRange(Line(1, 300, "Total de encargos em R$ 1,00"));
        var page3 = new CreditCardPdfExtractedPage(3, "Total dos lançamentos atuais 143,00", page3Words);
        return new([page1, page2, page3]);
    }

    private static IReadOnlyList<CreditCardPdfExtractedWord> Line(int column, double top, string text)
    {
        var x = column == 0 ? 140d : 360d;
        var words = new List<CreditCardPdfExtractedWord>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var width = Math.Max(8, token.Length * 4.5);
            words.Add(new(token, x, top, x + width, top + 8));
            x += width + 4;
        }
        return words;
    }
}

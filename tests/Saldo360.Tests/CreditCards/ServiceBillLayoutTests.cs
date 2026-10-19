using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class ServiceBillLayoutTests
{
    // Synthetic values and structural labels only; no personal identifiers or actual financial values.
    private const string ElectricityFixture = """
        Consumo em kWh
        Consumo TOTAL 123 456 0,1234 600 kWh
        Competência: Vencimento: Valor cobrado (R$):
        02/2028 12/03/2028 1.234,56
        """;
    private const string WaterFixture = """
        DMAE-Departamento Municipal de Agua Esgotos
        FATURA
        02/2028
        VENCIMENTO
        12/03/2028
        AGUA 90,00
        ESGOTO 20,50
        TOTAL R$ 110,50
        FATURA
        02/2028
        VENCIMENTO 12/03/2028
        VALOR R$ 110,50
        """;

    [Fact]
    public async Task ElectricityColumnsExtractExactFieldsButNeverInventRasterOnlyIssuer()
    {
        var result = await Extract("ElectricityBill", ElectricityFixture);
        Assert.Equal("2028-02", result.Result.Competence);
        Assert.Equal("2028-03-12", result.Result.DueDate);
        Assert.Equal("1234.56", result.Result.TotalAmount);
        Assert.Null(result.Result.Issuer);
        Assert.Equal("IssuerNotFound", result.Result.FailureCode);
        Assert.Equal("NeedsReview", result.Assessment);
        Assert.Equal("Medium", result.Result.Confidence);
    }

    [Theory]
    [InlineData("CEEE Distribuição")]
    [InlineData("Equatorial Energia")]
    public async Task ElectricityWithExplicitTextIssuerIsReliable(string issuerText)
    {
        var result = await Extract("ElectricityBill", issuerText + "\n" + ElectricityFixture);
        Assert.Equal("Equatorial", result.Result.Issuer);
        Assert.Equal("Reliable", result.Assessment);
        Assert.Equal("High", result.Result.Confidence);
        Assert.Null(result.Result.FailureCode);
    }

    [Fact]
    public async Task WaterLabelsExtractIssuerAndRepeatedConsistentTotalsWithoutSumming()
    {
        var result = await Extract("WaterBill", WaterFixture);
        Assert.Equal("DMAE", result.Result.Issuer);
        Assert.Equal("2028-02", result.Result.Competence);
        Assert.Equal("2028-03-12", result.Result.DueDate);
        Assert.Equal("110.50", result.Result.TotalAmount);
        Assert.Equal("Reliable", result.Assessment);
        Assert.Equal("High", result.Result.Confidence);
        Assert.Null(result.Result.FailureCode);
        Assert.Null(result.Result.AccountReference);
    }

    [Theory]
    [InlineData("VALOR R$ 110,50", "VALOR R$ 111,50", "AmbiguousTotalAmount")]
    [InlineData("02/2028", "13/2028", "InvalidCompetence")]
    [InlineData("12/03/2028", "31/02/2028", "InvalidDueDate")]
    [InlineData("TOTAL R$ 110,50", "TOTAL R$ 99,00", "AmbiguousTotalAmount")]
    public async Task InvalidOrConflictingWaterFieldsRemainInReview(string before, string after, string failure)
    {
        var result = await Extract("WaterBill", WaterFixture.Replace(before, after));
        Assert.Equal("NeedsReview", result.Assessment);
        Assert.Equal(failure, result.Result.FailureCode);
    }

    [Fact]
    public async Task DifferentRepeatedCompetencesAreAmbiguousNotChosenByDueDate()
    {
        var result = await Extract("WaterBill", WaterFixture + "\nFATURA\n03/2028");
        Assert.Null(result.Result.Competence);
        Assert.Equal("AmbiguousCompetence", result.Result.FailureCode);
        Assert.Equal("NeedsReview", result.Assessment);
    }

    [Fact]
    public async Task WrongDocumentTypeDoesNotActivateWaterLayout()
    {
        var result = await Extract("ElectricityBill", WaterFixture);
        Assert.NotEqual("Reliable", result.Assessment);
        Assert.Null(result.Result.Issuer);
        Assert.Null(result.Result.TotalAmount);
    }

    [Fact]
    public async Task SpatialColumnsWorkWhenPdfTextHasNoSeparators()
    {
        var words = new List<CreditCardPdfExtractedWord>();
        void Word(string text, double x, double top) => words.Add(new(text, x, top, x + text.Length * 4, top + 5));
        Word("FATURA", 400, 100); Word("02/2028", 400, 112);
        Word("VENCIMENTO", 400, 70); Word("12/03/2028", 400, 81);
        Word("TOTAL", 300, 300); Word("R$", 350, 300); Word("110,50", 450, 299);
        var result = await Run(new("synthetic.pdf", new([new(1, "DMAE-DepartamentoMunicipaldeAguaEsgotosFATURA02/2028TOTAL", words)]), "WaterBill"));
        Assert.Equal("Reliable", result.Assessment);
        Assert.Equal("2028-02", result.Result.Competence);
        Assert.Equal("2028-03-12", result.Result.DueDate);
        Assert.Equal("110.50", result.Result.TotalAmount);
    }

    [Fact]
    public async Task ConsumptionTaxesAndUnlabelledAmountsCannotReplaceTotal()
    {
        var text = "DMAE-Departamento Municipal de Agua Esgotos\nFATURA\n02/2028\nVENCIMENTO 12/03/2028\nConsumo 123,45\nImpostos 8,50";
        var result = await Extract("WaterBill", text);
        Assert.Null(result.Result.TotalAmount);
        Assert.Equal("TotalAmountNotFound", result.Result.FailureCode);
        Assert.Equal("NeedsReview", result.Assessment);
    }

    [Fact]
    public async Task ElectricityUsesSpatialRemittanceRowRatherThanConcatenatedPdfText()
    {
        var words = new List<CreditCardPdfExtractedWord>();
        void Word(string text, double x, double top) => words.Add(new(text, x, top, x + text.Length * 3, top + 4));
        Word("Competência:", 300, 760); Word("Vencimento:", 365, 760);
        Word("Valor", 425, 760); Word("cobrado", 445, 760); Word("(R$):", 470, 760);
        Word("02/2028", 300, 766); Word("12/03/2028", 365, 766); Word("0,01", 425, 766);
        var result = await Run(new("synthetic.pdf", new([new(1, "CEEECompetência:Vencimento:Valorcobrado(R$):", words)]), "ElectricityBill"));
        Assert.Equal("2028-02", result.Result.Competence);
        Assert.Equal("2028-03-12", result.Result.DueDate);
        Assert.Equal("0.01", result.Result.TotalAmount);
        Assert.Equal("NeedsReview", result.Assessment); // CEEE concatenated with another word is not explicit issuer evidence.
        Assert.Equal("IssuerNotFound", result.Result.FailureCode);
    }

    private static Task<ServiceBillExtractionOutput> Extract(string type, string text) => Run(new("synthetic.pdf", new([new(1, text)]), type));
    private static Task<ServiceBillExtractionOutput> Run(ServiceBillExtractionRequest request) =>
        new CompositeServiceBillExtractor(new(), new ForbiddenAgent()).ExtractAsync(request, CancellationToken.None);
    private sealed class ForbiddenAgent : IServiceBillExtractionAgent
    {
        public Task<string> ExtractAsync(ServiceBillExtractionRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Agent must not run");
    }
}

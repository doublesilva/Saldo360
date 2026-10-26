using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Services")]
public sealed class ServiceBillExtractionTests
{
    [Theory]
    [InlineData("ElectricityBill", "Unidade consumidora: UC-123 Referência: 10/2026 Vencimento: 20/10/2026 Valor a pagar: R$ 187,43", "187.43")]
    [InlineData("WaterBill", "Matrícula: MAT-123 Referência: 2026-10 Vencimento: 21/10/2026 Total a pagar: 99,01 Hidrômetro: HID-9", "99.01")]
    [InlineData("TelecomBill", "Número da conta: CT-123 Competência: 10/2026 Vencimento: 22/10/2026 Valor total a pagar: R$ 120,00 Linha: 51999999999", "120.00")]
    public async Task ExtractsSupportedBillsWithExactDecimals(string type, string text, string amount)
    {
        var result = await Extractor().ExtractAsync(Request(type, text), CancellationToken.None);
        Assert.Equal("Reliable", result.Assessment); Assert.Equal(amount, result.Result.TotalAmount);
        Assert.Equal("2026-10", result.Result.Competence); Assert.StartsWith("2026-10-", result.Result.DueDate);
        Assert.Equal("Deterministic", result.Result.ExtractionSource);
    }

    [Fact]
    public async Task MissingCompetenceOrDueDateNeedsReviewAndIdentifiersAreOptional()
    {
        var result = await Extractor().ExtractAsync(Request("WaterBill", "Valor a pagar: R$ 10,50"), CancellationToken.None);
        Assert.Equal("NeedsReview", result.Assessment); Assert.Equal("10.50", result.Result.TotalAmount);
        Assert.Null(result.Result.Competence); Assert.Null(result.Result.AccountReference);
    }

    [Fact]
    public async Task UnsupportedTypeIsRejected()
    {
        var result = await Extractor().ExtractAsync(Request("BillingDocument", "Valor a pagar: 10,00 Referência: 10/2026 Vencimento: 20/10/2026"), CancellationToken.None);
        Assert.Equal("Rejected", result.Assessment); Assert.Equal("UnsupportedDocumentType", result.Result.FailureCode);
    }

    [Theory]
    [InlineData("{\"documentType\":\"WaterBill\",\"issuer\":null,\"competence\":\"2026-10\",\"dueDate\":\"invalid\",\"totalAmount\":\"10.00\",\"accountReference\":null,\"serviceReference\":null,\"confidence\":\"High\"}")]
    [InlineData("{\"documentType\":\"WaterBill\",\"issuer\":null,\"competence\":\"2026-10\",\"dueDate\":\"2026-10-20\",\"totalAmount\":\"1.000,00\",\"accountReference\":null,\"serviceReference\":null,\"confidence\":\"High\"}")]
    [InlineData("{\"documentType\":\"WaterBill\",\"issuer\":null,\"competence\":\"2026-10\",\"dueDate\":\"2026-10-20\",\"totalAmount\":\"10.00\",\"accountReference\":null,\"serviceReference\":null,\"confidence\":\"High\",\"extra\":true}")]
    public async Task InvalidAgentSchemaIsRejected(string json)
    {
        var result = await new CompositeServiceBillExtractor(new() { AgentEnabled = true }, new Agent(json)).ExtractAsync(Request("WaterBill", "texto sem campos estruturados suficientes"), CancellationToken.None);
        Assert.Equal("Rejected", result.Assessment); Assert.Equal("InvalidAgentResponse", result.Result.FailureCode);
    }

    [Fact]
    public async Task ProviderUnavailableIsRejected()
    {
        var result = await new CompositeServiceBillExtractor(new() { AgentEnabled = true }, new Agent(error: true)).ExtractAsync(Request("WaterBill", "texto insuficiente para extração local"), CancellationToken.None);
        Assert.Equal("ExtractionProviderUnavailable", result.Result.FailureCode);
    }

    [Fact]
    public async Task LowConfidenceAgentResultNeedsReview()
    {
        const string json = "{\"documentType\":\"WaterBill\",\"issuer\":null,\"competence\":\"2026-10\",\"dueDate\":\"2026-10-20\",\"totalAmount\":\"10.00\",\"accountReference\":null,\"serviceReference\":null,\"confidence\":\"Low\"}";
        var result = await new CompositeServiceBillExtractor(new() { AgentEnabled = true }, new Agent(json)).ExtractAsync(Request("WaterBill", "texto insuficiente para extração local"), CancellationToken.None);
        Assert.Equal("NeedsReview", result.Assessment); Assert.Equal("LowConfidence", result.Result.FailureCode);
    }

    private static CompositeServiceBillExtractor Extractor() => new(new(), new Agent(""));
    private static ServiceBillExtractionRequest Request(string type, string text) => new("sanitized.pdf", new([new(1, text)]), type);
    private sealed class Agent(string response = "", bool error = false) : IServiceBillExtractionAgent
    {
        public Task<string> ExtractAsync(ServiceBillExtractionRequest request, CancellationToken cancellationToken) => error ? throw new CreditCardStatementAgentUnavailableException("offline") : Task.FromResult(response);
    }
}

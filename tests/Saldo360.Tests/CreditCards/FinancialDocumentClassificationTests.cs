using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class FinancialDocumentClassificationTests
{
    [Theory]
    [InlineData("Consumo de energia elétrica 123 kWh Unidade consumidora Medição atual Tarifa de energia", "ElectricityBill")]
    [InlineData("Consumo de água 18 m³ hidrômetro água e esgoto referência mensal", "WaterBill")]
    [InlineData("Fatura do cartão de crédito Limite disponível Pagamento mínimo Lançamentos do cartão", "CreditCardStatement")]
    [InlineData("Documento de cobrança Vencimento Valor a pagar Código de barras linha digitável", "BillingDocument")]
    public async Task StrongSignalsAreClassifiedDeterministically(string text, string expected)
    {
        var agent = new FakeAgent("{}"); var classifier = Create(agent, true);
        var result = await classifier.ClassifyAsync(Request(text), CancellationToken.None);
        Assert.Equal(expected, result.DocumentType); Assert.Equal("High", result.Confidence);
        Assert.Equal("Deterministic", result.ClassificationSource); Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task GenericDueDateAndAmountAreNotPromotedToCardStatement()
    {
        var classifier = Create(new FakeAgent("{}"), false);
        var result = await classifier.ClassifyAsync(Request("Relatório financeiro com vencimento e valor total para consulta."), CancellationToken.None);
        Assert.Equal("Unknown", result.DocumentType); Assert.Equal("InsufficientEvidence", result.FailureCode);
    }

    [Fact]
    public async Task InconclusiveDocumentFallsBackToStrictAgent()
    {
        var agent = new FakeAgent("""{"documentType":"WaterBill","confidence":"Medium","evidence":["Referência explícita a abastecimento"]}""");
        var result = await Create(agent, true).ClassifyAsync(Request("Documento financeiro com detalhes suficientes para análise pelo agente."), CancellationToken.None);
        Assert.Equal("WaterBill", result.DocumentType); Assert.Equal("Agent", result.ClassificationSource); Assert.Equal(1, agent.Calls);
    }

    [Theory]
    [InlineData("{\"documentType\":\"WaterBill\",\"confidence\":\"Low\",\"evidence\":[\"ambíguo\"]}", "InsufficientEvidence")]
    [InlineData("{\"documentType\":\"WaterBill\",\"confidence\":\"High\",\"evidence\":[],\"extra\":true}", "InvalidAgentResponse")]
    [InlineData("not-json", "InvalidAgentResponse")]
    public async Task LowConfidenceOrInvalidSchemaFailsSafe(string json, string failure)
    {
        var result = await Create(new FakeAgent(json), true).ClassifyAsync(Request("Documento financeiro inconclusivo com texto amplo para avaliação."), CancellationToken.None);
        Assert.Equal("Unknown", result.DocumentType); Assert.Equal(failure, result.FailureCode);
    }

    [Fact]
    public async Task UnavailableAgentFailsSafe()
    {
        var result = await Create(new FakeAgent(error: true), true).ClassifyAsync(Request("Documento financeiro inconclusivo com texto amplo para avaliação."), CancellationToken.None);
        Assert.Equal("Unknown", result.DocumentType); Assert.Equal("ClassificationProviderUnavailable", result.FailureCode);
    }

    private static CompositeFinancialDocumentClassifier Create(FakeAgent agent, bool enabled) => new(new() { AgentEnabled = enabled }, agent);
    private static FinancialDocumentClassificationRequest Request(string text) => new("sanitized.pdf", new([new(1, text)]));
    private sealed class FakeAgent(string response = "", bool error = false) : IFinancialDocumentClassificationAgent
    {
        public int Calls { get; private set; }
        public Task<string> ClassifyAsync(FinancialDocumentClassificationRequest request, CancellationToken cancellationToken)
        { Calls++; return error ? throw new CreditCardStatementAgentUnavailableException("offline") : Task.FromResult(response); }
    }
}

using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class CreditCardStatementExtractionOrchestratorTests
{
    [Fact]
    public async Task DisabledUsesOnlyParser()
    {
        var parser = new FakeParser(Json("100.00", Item("CURRENT", "100.00")));
        var provider = new FakeProvider(Json("999.00", Item("AGENT", "999.00")));
        var result = await Run(CreditCardStatementAgentMode.Disabled, parser, provider);
        Assert.Equal("Parser", result.PrimarySource); Assert.False(result.ParserShadowExecuted);
        Assert.Equal(0, provider.Calls); Assert.Equal("100.00", result.Primary!.Reconciliation.ComponentsTotal);
    }

    [Fact]
    public async Task ShadowKeepsParserPrimaryAndComparesAgent()
    {
        var parser = new FakeParser(Json("100.00", Item("CURRENT", "100.00")));
        var provider = new FakeProvider(Json("101.00", Item("CURRENT", "101.00")));
        var result = await Run(CreditCardStatementAgentMode.Shadow, parser, provider);
        Assert.Equal("Parser", result.PrimarySource); Assert.True(result.ParserShadowExecuted);
        Assert.Equal("100.00", result.Primary!.Header.OfficialTotal);
        Assert.True(result.ShadowSummary!.HasFinancialDifference);
    }

    [Fact]
    public async Task AgentPrimaryUsesAgentAndParserOnlyAsShadow()
    {
        var parser = new FakeParser(Json("962.12", Item("PARSER", "962.12")));
        var provider = new FakeProvider(Json("726.94", Item("CURRENT", "726.94"),
            Item("SHEIN FUTURA", "100.00") with { Section = "FutureInstallments", IncludedInCurrentTotal = true }));
        var result = await Run(CreditCardStatementAgentMode.AgentPrimary, parser, provider);
        Assert.Equal("Agent", result.PrimarySource); Assert.Equal("726.94", result.Primary!.Reconciliation.ComponentsTotal);
        Assert.Equal(AgentExtractionAssessment.Reliable, result.Assessment);
        Assert.Equal("962.12", result.ParserShadow!.Reconciliation.ComponentsTotal);
        Assert.True(result.ShadowSummary!.HasFinancialDifference);
    }

    [Fact]
    public async Task AgentPrimaryFailureIsRejectedWithoutParserFallback()
    {
        var parser = new FakeParser(Json("100.00", Item("PARSER", "100.00")));
        var result = await Run(CreditCardStatementAgentMode.AgentPrimary, parser, new FakeProvider(error: true));
        Assert.Equal("Agent", result.PrimarySource); Assert.Null(result.Primary);
        Assert.Equal(AgentExtractionAssessment.Rejected, result.Assessment);
        Assert.NotNull(result.ParserShadow); Assert.Contains("indisponível", result.AgentError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DivergenceAndLowConfidenceProduceNeedsReview()
    {
        var parser = new FakeParser(Json("10.00", Item("CURRENT", "10.00")));
        var divergent = await Run(CreditCardStatementAgentMode.AgentPrimary, parser, new FakeProvider(Json("11.00", Item("CURRENT", "10.00"))));
        Assert.Equal(AgentExtractionAssessment.NeedsReview, divergent.Assessment);
        var low = await Run(CreditCardStatementAgentMode.AgentPrimary, parser, new FakeProvider(Json("10.00", Item("AMBIGUOUS", "10.00") with { Confidence = "Low" })));
        Assert.Equal(AgentExtractionAssessment.NeedsReview, low.Assessment);
        Assert.Equal("Unknown", Assert.Single(low.Primary!.Items).Type);
    }

    [Fact]
    public async Task InvalidAndNonStatementResponsesAreRejected()
    {
        var parser = new FakeParser(Json("10.00", Item("CURRENT", "10.00")));
        var invalid = await Run(CreditCardStatementAgentMode.AgentPrimary, parser, new FakeProvider("not-json"));
        Assert.Equal(AgentExtractionAssessment.Rejected, invalid.Assessment); Assert.Null(invalid.Primary);
        var empty = JsonSerializer.Serialize(new CreditCardStatementAgentDocument("1.0", new(null, null, null, null, null, null), []), CreditCardStatementAgentSchema.Json);
        var nonStatement = await Run(CreditCardStatementAgentMode.AgentPrimary, parser, new FakeProvider(empty));
        Assert.Equal(AgentExtractionAssessment.Rejected, nonStatement.Assessment);
    }

    private static Task<CreditCardStatementExtractionResult> Run(CreditCardStatementAgentMode mode, FakeParser parser, FakeProvider provider)
    {
        var orchestrator = new CreditCardStatementExtractionOrchestrator(new() { Enabled = true, Mode = mode }, parser, new(provider));
        return orchestrator.ExtractAsync(new("file.pdf", new([new(1, "document")]), null), CancellationToken.None);
    }
    private static string Json(string total, params CreditCardStatementAgentItem[] items) => JsonSerializer.Serialize(
        new CreditCardStatementAgentDocument("1.0", new("Itaú", "1234", "2026-10", "2026-10-05", "2026-10-12", total), items), CreditCardStatementAgentSchema.Json);
    private static CreditCardStatementAgentItem Item(string description, string amount) =>
        new(description, amount, "Debit", "Purchase", null, null, "CurrentInvoice", true, 1, description, "High");
    private sealed class FakeParser(string response) : IParserBackedCreditCardStatementExtractorAgent
    { public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken cancellationToken) => Task.FromResult(response); }
    private sealed class FakeProvider(string? response = null, bool error = false) : ICreditCardStatementAgentProvider
    {
        public int Calls { get; private set; }
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken cancellationToken)
        { Calls++; return error ? throw new CreditCardStatementAgentUnavailableException("offline") : Task.FromResult(response!); }
    }
}

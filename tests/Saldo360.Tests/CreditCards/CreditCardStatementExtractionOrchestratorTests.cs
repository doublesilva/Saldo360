using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class CreditCardStatementExtractionOrchestratorTests
{
    [Theory]
    [InlineData(null, "1234", "SHOP 10.00", "10.00", "Reliable", "Valid")]
    [InlineData("Bank", null, "SHOP 10.00", "10.00", "Reliable", "Valid")]
    [InlineData(null, null, "SHOP 10.00", "10.00", "Reliable", "Valid")]
    [InlineData(null, null, "FABRICATED 10.00", "10.00", "Rejected", "Rejected")]
    [InlineData(null, null, "SHOP 9.00", "9.00", "NeedsReview", "Valid")]
    public async Task FinancialExtractionIsIndependentOfIdentityAndParser(string? issuer, string? digits, string evidence, string amount, string expected, string validation)
    {
        var facts = new CreditCardStatementAgentDocument("2.0", new(issuer, digits, "2026-10", null, null, "10.00"),
            [Item(evidence.StartsWith("FABRICATED", StringComparison.Ordinal) ? "FABRICATED" : "SHOP", amount) with { Evidence = evidence }]);
        var json = JsonSerializer.Serialize(facts, CreditCardStatementAgentSchema.Json);
        // Parser disagreement is deliberately reliable in isolation but is not the authority for this PDF.
        var service = new CreditCardStatementExtractionOrchestrator(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary },
            new FakeParser(Json("20.00", Item("PARSER", "20.00"))), new(new FakeProvider(json)));
        var result = await service.ExtractAsync(new("synthetic.pdf", new([new(1, "SHOP 10.00\nSHOP 9.00\nTOTAL 10.00")]), null)
            { RequireAgent = true, ReconcileWithRetry = true, AllowCorrectionRetry = false }, default);
        Assert.Equal(expected, result.Assessment.ToString()); Assert.Equal(validation, result.ValidationStatus);
        Assert.NotNull(result.Primary); Assert.Contains("ParserAgentDifference", result.DocumentWarnings!);
        Assert.Contains("DocumentIdentityIncomplete", result.DocumentWarnings!);
        Assert.DoesNotContain("CardIdentificationRequired", result.AgentError ?? "");
        Assert.Equal("ParserFinancialReviewRequired", OfficialStatementAnalysisValidation.ParserReviewReason(result));
        if (expected == "Rejected") Assert.Equal("FinancialEvidenceInvalid", result.AgentError);
        if (expected == "NeedsReview") Assert.Equal("Divergent", result.Primary.Reconciliation.Status);
    }

    [Theory]
    [InlineData("resolved", AgentExtractionAssessment.Reliable)]
    [InlineData("persistent", AgentExtractionAssessment.NeedsReview)]
    [InlineData("changed-total", AgentExtractionAssessment.Rejected)]
    [InlineData("invented", AgentExtractionAssessment.Rejected)]
    public async Task InboxRetryIsLimitedAndCannotChangeTotalOrInventEvidence(string scenario, AgentExtractionAssessment expected)
    {
        var first = Json("10.00", Item("SHOP", "8.00") with { Evidence = "SHOP 8.00" });
        var second = scenario switch
        {
            "resolved" => Json("10.00", Item("SHOP", "8.00") with { Evidence = "SHOP 8.00" }, Item("TAX", "2.00") with { Evidence = "TAX 2.00", Type = "Fee", Section = "Fees" }),
            "changed-total" => Json("8.00", Item("SHOP", "8.00") with { Evidence = "SHOP 8.00" }),
            "invented" => Json("10.00", Item("ADJUSTMENT", "10.00") with { Evidence = "TOTAL 10.00" }),
            _ => first
        };
        var provider = new SequenceProvider(first, second);
        var service = new CreditCardStatementExtractionOrchestrator(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary }, new FakeParser(first), new(provider));
        var result = await service.ExtractAsync(new("f.pdf", new([new(1, "SHOP 8.00\nTAX 2.00\nTOTAL 10.00")]), null) { ReconcileWithRetry = true, Categories = new([], []) }, default);
        Assert.Equal(expected, result.Assessment); Assert.Equal(2, provider.Calls); Assert.Equal(2, result.Attempts);
    }
    private sealed class SequenceProvider(params string[] responses) : ICreditCardStatementAgentProvider
    {
        public int Calls;
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct) => Task.FromResult(responses[Math.Min(Calls++, responses.Length - 1)]);
    }

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
        Assert.Null(OfficialStatementAnalysisValidation.ParserReviewReason(result));
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
    public async Task SchemaFailureKeepsSpecificSafeCodeAndNeverPromotesParserShadow()
    {
        var parser = new FakeParser(Json("100.00", Item("PARSER", "100.00")));
        var invalid = CreditCardStatementAgentSchemaTests.Valid.Replace("\"amount\":\"10.00\"", "\"amount\":\"private-invalid-value\"");
        var result = await Run(CreditCardStatementAgentMode.AgentPrimary, parser, new FakeProvider(invalid));
        Assert.Null(result.Primary); Assert.NotNull(result.ParserShadow);
        Assert.Equal("Agent", result.PrimarySource); Assert.Equal(AgentExtractionAssessment.Rejected, result.Assessment);
        Assert.Equal("SchemaInvalidMoney:$.items[].amount", result.AgentError);
        Assert.DoesNotContain("private-invalid-value", JsonSerializer.Serialize(result));
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
        // Explicit synthetic source evidence: these tests isolate mode/comparison behavior, not grounding failures.
        return orchestrator.ExtractAsync(new("file.pdf", new([new(1, "CURRENT 100.00\nCURRENT 101.00\nPARSER 962.12\nCURRENT 726.94\nSHEIN FUTURA 100.00\nPARSER 100.00\nCURRENT 10.00\nAMBIGUOUS 10.00\nTOTAL 11.00")]), null), CancellationToken.None);
    }
    private static string Json(string total, params CreditCardStatementAgentItem[] items) => JsonSerializer.Serialize(
        new CreditCardStatementAgentDocument("1.0", new("Itaú", "1234", "2026-10", "2026-10-05", "2026-10-12", total), items), CreditCardStatementAgentSchema.Json);
    private static CreditCardStatementAgentItem Item(string description, string amount) =>
        new(description, amount, "Debit", "Purchase", null, null, "CurrentInvoice", true, 1, description + " " + amount, "High");
    private sealed class FakeParser(string response) : IParserBackedCreditCardStatementExtractorAgent
    { public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken cancellationToken) => Task.FromResult(response); }
    private sealed class FakeProvider(string? response = null, bool error = false) : ICreditCardStatementAgentProvider
    {
        public int Calls { get; private set; }
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken cancellationToken)
        { Calls++; return error ? throw new CreditCardStatementAgentUnavailableException("offline") : Task.FromResult(response!); }
    }
}

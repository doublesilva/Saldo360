using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
public sealed class StatementVisualSafetyTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("\ufffd\ufffd\ufffd\ufffdgarbled", false)]
    [InlineData("FATURA DOCUMENTO SINTETICO COMPRA MERCADO 07/10 123,45 TOTAL 123,45", true)]
    public void NativeQualityIsExplicit(string text, bool usable) => Assert.Equal(usable, LocalStatementPageReader.Usable(text));

    [Theory]
    [InlineData("VISUAL: SHOP 10.00", "NativeWithImage", "NeedsReview")]
    [InlineData("SHOP 10.00", "Unavailable", "NeedsReview")]
    [InlineData("SHOP 10.00", "OCRWithImage", "NeedsReview")]
    public async Task VisualAndIncompletePagesNeverBecomeImplicitlyReliable(string evidence, string status, string expected)
    {
        var json = CreditCardStatementAgentSchemaTests.Valid.Replace("\"evidence\":\"SHOP 10.00\"", "\"evidence\":\"" + evidence + "\"");
        var forbidden = new ForbiddenParser();
        var service = new CreditCardStatementExtractionOrchestrator(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary }, forbidden, new(new Provider(json)));
        var result = await service.ExtractAsync(new("synthetic.pdf", new([new(1, "SHOP 10.00 TOTAL 10.00")
        { ImagePng = [137, 80, 78, 71, 13, 10, 26, 10], ProcessingStatus = status, TextSource = status == "OCRWithImage" ? "OCR" : "Native" }]), null), default);
        Assert.Equal(expected, result.Assessment.ToString()); Assert.False(result.ParserShadowExecuted); Assert.Null(result.Comparison);
        Assert.Single(result.PageCoverage!);
        Assert.Equal(0, forbidden.Calls);
        Assert.DoesNotContain("ImagePng", System.Text.Json.JsonSerializer.Serialize(result));
    }
    private sealed class ForbiddenParser : IParserBackedCreditCardStatementExtractorAgent
    { public int Calls; public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken ct) { Calls++; throw new Xunit.Sdk.XunitException("Parser must not run"); } }
    private sealed class Provider(string json) : ICreditCardStatementAgentProvider
    { public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct) => Task.FromResult(json); }
}

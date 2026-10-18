using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class FinancialEmailLinkExtractorTests
{
    private readonly FinancialEmailLinkExtractor extractor = new();

    [Fact]
    public void ExtractsOnlyAllowlistedHttpsLinksWithStableOrdinals()
    {
        var profile = Profile();
        var body = new FinancialEmailBody("<a href=\"https://dmae.example/contas/a.pdf?token=secret\">Conta</a><a href=\"http://dmae.example/contas/b.pdf\">inseguro</a>", null);

        var result = extractor.Extract(profile, body);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(1, candidate.Ordinal);
        Assert.Equal(2, result.Scanned);
        Assert.Equal(1, result.Rejected);
        Assert.Equal("gmail:message:link:1", GmailLinkedDocumentIdentity.Create("message", candidate.Ordinal));
        Assert.DoesNotContain("secret", GmailLinkedDocumentIdentity.Create("message", candidate.Ordinal));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:application/pdf;base64,AA==")]
    [InlineData("file:///tmp/a.pdf")]
    [InlineData("https://localhost/contas/a.pdf")]
    [InlineData("https://127.0.0.1/contas/a.pdf")]
    [InlineData("https://user:pass@dmae.example/contas/a.pdf")]
    [InlineData("https://evil.example/contas/a.pdf")]
    [InlineData("https://dmae.example/outro/a.pdf")]
    public void RejectsUnsafeOrOutOfScopeLinks(string value)
    {
        var result = extractor.Extract(Profile(), new FinancialEmailBody($"<a href=\"{value}\">x</a>", null));
        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.Rejected);
    }

    private static FinancialEmailImportProfile Profile() => new()
    {
        Name = "Dmae", CollectionMode = FinancialEmailCollectionMode.Links,
        AllowedLinkHosts = ["dmae.example"], AllowedLinkPathPrefixes = ["/contas/"]
    };
}

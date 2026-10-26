using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
[Trait("Profile", "FAST")]
public sealed class StatementBatchExtractionTests
{
    [Fact]
    public async Task ThirteenImagePagesUseDisjointOrderedBatchesAndPreserveOriginalNumbersAndDuplicates()
    {
        var provider = new Provider((_, n) => Facts(Enumerable.Range((n - 1) * 6 + 1, n == 3 ? 1 : 6)
            .SelectMany(p => new[] { Item(p), Item(p) }).ToArray()));
        var result = await new LlmCreditCardStatementExtractorAgent(provider).ExtractBatchesAsync(Request(13), default);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(new[] { 6, 6, 1 }, provider.Requests.Select(r => r.ImagePages.Count));
        Assert.Equal(Enumerable.Range(1, 13), provider.Requests.SelectMany(r => r.ImagePages.Select(p => p.Number)));
        Assert.DoesNotContain("--- PAGE 7 ", provider.Requests[0].DocumentText);
        Assert.Equal(26, result.Document!.Items.Count);
        Assert.Equal(13, result.Coverage.PreparedPages); Assert.Equal(13, result.Coverage.ValidReturnPages);
        Assert.True(result.Coverage.TechnicalComplete); Assert.False(result.Coverage.RecognitionComplete);
        Assert.All(result.Document.Items, i => { Assert.Equal(7, i.InstallmentNumber); Assert.Equal(10, i.InstallmentCount); Assert.Equal("2026-03-26", i.TransactionDate); });
    }

    [Fact]
    public async Task ConflictingHeaderIsClearedAndNullNeverOverwritesKnownFact()
    {
        var provider = new Provider((_, n) => Facts([Item((n - 1) * 6 + 1)], n == 1 ? "10.00" : n == 2 ? "11.00" : "10.00", n == 1 ? "Bank" : null));
        var result = await new LlmCreditCardStatementExtractorAgent(provider).ExtractBatchesAsync(Request(13), default);
        Assert.Null(result.Document!.Header.OfficialTotal); Assert.Equal("Bank", result.Document.Header.Issuer);
        Assert.Contains("BatchHeaderConflict:officialTotal", result.Warnings);
    }

    [Theory]
    [InlineData("ProviderOutputTruncated")]
    [InlineData("ProviderQuotaExceeded")]
    [InlineData("ProviderAuthenticationFailed")]
    [InlineData("ProviderModelUnavailable")]
    public async Task FailureHasSafeCodeAndNeverRetriesSuccessfulBatch(string code)
    {
        var provider = new Provider((_, n) => n == 1 ? Facts([Item(1)])
            : throw new CreditCardStatementAgentUnavailableException("private provider body", failureCode: code));
        var result = await new LlmCreditCardStatementExtractorAgent(provider).ExtractBatchesAsync(Request(7), default);
        Assert.Equal(2, provider.Requests.Count); Assert.Single(result.Document!.Items);
        Assert.Equal(1, result.Coverage.FailedPages); Assert.False(result.Coverage.TechnicalComplete);
        Assert.Equal(code, result.Coverage.Batches[1].FailureCode);
        Assert.DoesNotContain("private provider body", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task PageFromAnotherBatchIsRejected()
    {
        var result = await new LlmCreditCardStatementExtractorAgent(new Provider((_, _) => Facts([Item(7)])))
            .ExtractBatchesAsync(Request(7), default);
        Assert.Equal("BatchPageMismatch", result.Coverage.Batches[0].FailureCode);
        Assert.Single(result.Document!.Items);
    }

    [Fact]
    public async Task MissingPreparationMakesNoProviderCallAndEmptyPageIsNotProvenRecognized()
    {
        var provider = new Provider((_, _) => Facts([]));
        var request = Request(1);
        var failed = await new LlmCreditCardStatementExtractorAgent(provider).ExtractBatchesAsync(request with
        { Document = new([new(1, "") { ProcessingStatus = "Unavailable" }]) }, default);
        Assert.Empty(provider.Requests); Assert.False(failed.Coverage.TechnicalComplete);
        var empty = await new LlmCreditCardStatementExtractorAgent(provider).ExtractBatchesAsync(request, default);
        Assert.Equal(1, empty.Coverage.InconclusivePages); Assert.False(empty.Coverage.RecognitionComplete);
        Assert.Contains("PageWithoutItemsUnproven", empty.Warnings);
    }

    [Fact]
    public async Task RepeatedSourceRowsNeedRepeatedReturnedOccurrences()
    {
        var provider = new Provider((_, _) => Facts([Item(1) with { Evidence = "26/03 SHOP 10.00" }]));
        var result = await new LlmCreditCardStatementExtractorAgent(provider).ExtractBatchesAsync(Request(1) with
        { Document = new([new(1, "26/03 SHOP 10.00\n26/03 SHOP 10.00")]) }, default);
        Assert.Contains("UnextractedDocumentRows", result.Warnings); Assert.Equal(1, result.Coverage.InconclusivePages);
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoFinancialResult()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var provider = new Provider((_, _) => Facts([]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LlmCreditCardStatementExtractorAgent(provider).ExtractBatchesAsync(Request(1), cancellation.Token));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ProviderFailureStopsRemainingPaidBatchesWithoutRepeatingAnything()
    {
        var provider = new Provider((_, _) => throw new CreditCardStatementAgentUnavailableException("private", failureCode: "ProviderQuotaExceeded"));
        var result = await new LlmCreditCardStatementExtractorAgent(provider).ExtractBatchesAsync(Request(13), default);
        Assert.Single(provider.Requests); Assert.Equal(13, result.Coverage.FailedPages);
        Assert.Equal(6, result.Coverage.SentPages); Assert.Null(result.Document);
        Assert.All(result.Coverage.Batches.Skip(1), b => Assert.Equal("ProviderStoppedAfterFailure", b.FailureCode));
    }

    private static CreditCardStatementAgentRequest Request(int pages) => new("untrusted.pdf",
        new(Enumerable.Range(1, pages).Select(n => new CreditCardPdfExtractedPage(n, "SHOP 10.00") { ImagePng = [1] }).ToArray()), null);
    private static CreditCardStatementAgentItem Item(int page) => new("SHOP", "10.00", "Debit", "Installment", 7, 10,
        "CurrentInvoice", true, page, "SHOP 10.00", "High") { TransactionDate = "2026-03-26" };
    private static string Facts(CreditCardStatementAgentItem[] items, string? total = "10.00", string? issuer = null) => JsonSerializer.Serialize(
        new CreditCardStatementAgentDocument("2.0", new(issuer, null, null, null, null, total), items), CreditCardStatementAgentSchema.Json);
    private sealed class Provider(Func<CreditCardStatementAgentProviderRequest, int, string> response) : ICreditCardStatementAgentProvider
    {
        public List<CreditCardStatementAgentProviderRequest> Requests { get; } = [];
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct)
        { Requests.Add(request); return Task.FromResult(response(request, Requests.Count)); }
    }
}

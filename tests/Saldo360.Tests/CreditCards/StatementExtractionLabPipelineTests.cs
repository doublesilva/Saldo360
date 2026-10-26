using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "INFRA")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
public sealed class StatementExtractionLabPipelineTests
{
    [Fact]
    public async Task GlobalCallBudgetCountsFailuresAndNeverRetriesOrExceedsCap()
    {
        var provider = new FailedProvider();
        var budget = new StatementExtractionLabPipeline.BudgetProvider(provider, 1);
        var request = new CreditCardStatementAgentProviderRequest("synthetic", "synthetic");
        await Assert.ThrowsAsync<CreditCardStatementAgentUnavailableException>(() => budget.CompleteAsync(request, default));
        var failure = await Assert.ThrowsAsync<CreditCardStatementAgentUnavailableException>(() => budget.CompleteAsync(request, default));
        Assert.Equal("LabCallBudgetExceeded", failure.FailureCode); Assert.Equal(1, provider.Calls); Assert.Equal(1, budget.Calls);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void InvalidBudgetIsRejectedBeforeAnyProcessing(int calls)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new StatementExtractionLabPipeline("synthetic", "gpt-4o-mini", "synthetic", calls));

    [Fact]
    public async Task ProtectedDocumentWithoutPasswordNeverCallsOpenAiOrAuthenticatedCpfProvider()
    {
        var source = (string)typeof(CreditCardPdfPreviewTests).GetField("ProtectedPdfBase64", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
        using var pipeline = new StatementExtractionLabPipeline("synthetic-not-a-real-key", "gpt-6.1-sol", "synthetic", 1);
        await using var pdf = new MemoryStream(Convert.FromBase64String(source));
        var failure = await Assert.ThrowsAsync<CreditCardPdfReadException>(() => pipeline.ExtractAsync(pdf, null, default));
        Assert.Equal(CreditCardPdfReadFailure.PasswordRequired, failure.Failure); Assert.Equal(0, pipeline.Calls);
    }
    private sealed class FailedProvider : ICreditCardStatementAgentProvider
    {
        public int Calls;
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct)
        { Calls++; throw new CreditCardStatementAgentUnavailableException("synthetic failure", failureCode: "ProviderQuotaExceeded"); }
    }
}

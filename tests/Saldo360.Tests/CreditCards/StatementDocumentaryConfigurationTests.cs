using Microsoft.Extensions.Logging.Abstractions;
using Saldo360.Application.CreditCards;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class StatementDocumentaryConfigurationTests
{
    [Fact]
    public void DocumentaryOptInDoesNotEnableLegacyAgentOrChangeItsModel()
    {
        var options = new CreditCardStatementAgentOptions();
        Assert.False(options.Enabled); Assert.Equal(CreditCardStatementAgentMode.Disabled, options.Mode);
        Assert.Equal("gpt-4o-mini", options.Model); Assert.False(options.DocumentaryImport.Enabled);
        Assert.Equal("gpt-6.1-sol", options.DocumentaryImport.Model); Assert.Equal("medium", options.DocumentaryImport.ReasoningEffort);
        Assert.Equal(32768, options.DocumentaryImport.MaxOutputTokens);
        options.DocumentaryImport.Enabled = true;
        Assert.False(options.Enabled); Assert.Equal("gpt-4o-mini", options.Model);
    }

    [Theory]
    [InlineData("Vencimento: 13/10/2026", true, null)]
    [InlineData("Vencimento\n13/10/2026", true, null)]
    [InlineData("Vencimento da fatura: 2026-10-13", true, null)]
    [InlineData("Vencimento: 13.10.2026", true, null)]
    [InlineData("Compra em 13/10/2026", false, "DocumentCalendarEvidenceMissing")]
    [InlineData("Próximo vencimento: 13/10/2026", false, "DocumentCalendarEvidenceMissing")]
    [InlineData("Vencimento: 14/10/2026", false, "DocumentCalendarEvidenceConflict")]
    [InlineData("Vencimento: 13/10/2026\nVencimento: 14/10/2026", false, "DocumentCalendarEvidenceConflict")]
    public void FinancialCompetenceNeedsDocumentaryYearNotJustAValidModelHeader(string text, bool verified, string? code)
    {
        var result = StatementKnownInstallmentImport.CorroborateCalendar(new([new(1, text)]), new(null, null, "2026-10", null, "2026-10-13", "60.00"));
        Assert.Equal(verified, result.Verified); Assert.Equal(code, result.FailureCode);
        Assert.DoesNotContain("13/10", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task FaithfulZeroAmountCannotBeDeclaredEligibleForThePositiveKnownObligationContract()
    {
        var item = new CreditCardStatementAgentItem("SHOP01/02", "0.00", "Debit", "Installment", 1, 2, "CurrentInvoice", true, 1, "04/07 SHOP01/02 0.00", "High");
        var header = new CreditCardStatementAgentHeader(null, null, "2026-10", null, "2026-10-13", "0.00");
        var source = new CreditCardPdfExtractedDocument([new(1, "Vencimento: 13/10/2026\nLançamentos: compras e saques\n04/07 SHOP01/02 0.00\nTotal 0.00")
            { ImagePng = [137,80,78,71,13,10,26,10], ProcessingStatus = "NativeWithImage" }]);
        var json = System.Text.Json.JsonSerializer.Serialize(new CreditCardStatementAgentDocument("2.0", header, [item]), CreditCardStatementAgentSchema.Json);
        var result = await new CreditCardStatementExtractionOrchestrator(new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary }, new NoParser(), new(new SyntheticProvider(json)))
            .ExtractAsync(new("synthetic.pdf", source, null) { RequireAgent = true, PurchasesOnly = true, ValidatePurchaseDocument = true, BatchPages = true, AllowCorrectionRetry = false }, default);
        Assert.True(result.Suitability!.FaithfulExtraction);
        result = result with { FinancialCalendarEvidence = StatementKnownInstallmentImport.CorroborateCalendar(source, header) };
        var eligibility = StatementKnownInstallmentImport.Evaluate(result, "Synthetic", "1234", 13);
        Assert.False(eligibility.Eligible); Assert.Contains("KnownInstallmentAmountInvalid", eligibility.Blockers);
    }

    private sealed class NoParser : IParserBackedCreditCardStatementExtractorAgent
    { public Task<string> ExtractAsync(CreditCardStatementAgentRequest r, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Parser forbidden"); }
    private sealed class SyntheticProvider(string json) : ICreditCardStatementAgentProvider
    { public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest r, CancellationToken ct) => Task.FromResult(json); }

    [Theory]
    [InlineData(false, true, "DocumentaryImportDisabled")]
    [InlineData(true, false, "DocumentaryContextNotAuthorized")]
    public async Task DisabledOrUnauthorizedProfileDoesNotReadPdfCredentialsOrContactProvider(bool enabled, bool owner, string code)
    {
        var options = new CreditCardStatementAgentOptions { ApiKeyFile = "nonexistent-credential-must-not-be-read", DocumentaryImport = new() { Enabled = enabled } };
        var pipeline = new StatementDocumentaryPipeline(new ForbiddenClients(), options, new LocalStatementPageReader(), new ManualOnlyPdfPasswordProvider(),
            NullLogger<OpenAiCreditCardStatementAgentProvider>.Instance, new(new Workspace(owner)));
        var error = await Assert.ThrowsAsync<CreditCardStatementAgentUnavailableException>(() => pipeline.ExtractAsync(Stream.Null,
            new(CardEmailSearchAuthorization.AuthorizedPersonalId, Guid.NewGuid(), Guid.NewGuid()), null, "synthetic.pdf", null, null, default));
        Assert.Equal(code, error.FailureCode);
    }

    private sealed class ForbiddenClients : IHttpClientFactory
    { public HttpClient CreateClient(string name) => throw new Xunit.Sdk.XunitException("No real provider calls allowed"); }
    private sealed class Workspace(bool owner) : IFinancialWorkspaceRepository
    {
        public Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid w, Guid u, CancellationToken ct) => Task.FromResult<FinancialWorkspaceRole?>(owner ? FinancialWorkspaceRole.Owner : null);
        public Task<FinancialWorkspace> EnsurePersonalAsync(Guid user, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException();
    }
}

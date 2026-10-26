using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
public sealed class CreditCardStatementAgentPreviewTests
{
    [Theory]
    [InlineData("Itaú Credicard", "726.94")]
    public async Task KnownStatementProfilesReturnSchemaValidReconciledDocuments(string issuer, string total)
    {
        var current = decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture);
        var document = Document(issuer, total,
            Item("COMPRA CORRENTE", current, "Debit", "Purchase", "CurrentInvoice", true, 1, "High"),
            Item("PARCELA FUTURA 03/06", 99.90m, "Debit", "Installment", "FutureInstallments", false, 2, "High", 3, 6),
            Item("PAGAMENTO RECEBIDO", current, "Credit", "Payment", "Payments", false, 1, "High"));
        var service = Create(JsonSerializer.Serialize(document, CreditCardStatementAgentSchema.Json));

        var result = await PreviewAsync(service);

        Assert.Equal(CreditCardPdfPreviewAccess.Allowed, result.Access);
        Assert.Equal("Reconciled", result.Preview!.Primary!.Reconciliation.Status);
        Assert.Equal(total, result.Preview.Primary.Reconciliation.OfficialTotal);
        Assert.Equal(total, result.Preview.Primary.Reconciliation.ComponentsTotal);
        Assert.Equal("0.00", result.Preview.Primary.Reconciliation.Difference);
        Assert.DoesNotContain(result.Preview.Primary.Items, item => item.Section == "FutureInstallments" && item.IncludedInCurrentTotal);
        Assert.DoesNotContain(result.Preview.Primary.Items, item => item.Type == "Payment" && item.IncludedInCurrentTotal);
    }

    [Fact]
    public async Task DeterministicValidationAppliesCreditSignAndDoesNotTurnIntermediateInstallmentIntoFullPurchase()
    {
        var document = Document("Itaú Credicard", "726.94",
            Item("COMPRA", 800m, "Debit", "Purchase", "CurrentInvoice", true, 1, "High"),
            Item("ESTORNO", 73.06m, "Credit", "Refund", "CurrentInvoice", true, 1, "High"),
            Item("PARCELA 02/06", 150m, "Debit", "Installment", "CurrentInvoice", false, 1, "High", 2, 6));

        var result = await PreviewAsync(Create(JsonSerializer.Serialize(document, CreditCardStatementAgentSchema.Json)));

        Assert.Equal("Reconciled", result.Preview!.Primary!.Reconciliation.Status);
        Assert.Equal("726.94", result.Preview.Primary.Reconciliation.ComponentsTotal);
        var installment = Assert.Single(result.Preview.Primary.Items, item => item.Type == "Installment");
        Assert.Equal("150.00", installment.Amount);
        Assert.Equal(2, installment.InstallmentNumber);
        Assert.Equal(6, installment.InstallmentCount);
    }

    [Fact]
    public async Task LowConfidenceBecomesUnknownAndPreventsSilentReconciliation()
    {
        var document = Document("Banco", "10.00", Item("LINHA AMBÍGUA", 10m, "Debit", "Purchase", "CurrentInvoice", true, 1, "Low"));

        var result = await PreviewAsync(Create(JsonSerializer.Serialize(document, CreditCardStatementAgentSchema.Json)));

        Assert.Equal("Unknown", Assert.Single(result.Preview!.Primary!.Items).Type);
        Assert.Equal("NeedsReview", result.Preview.Primary.Reconciliation.Status);
        Assert.True(result.Preview.Primary.Reconciliation.HasUnknownCurrentItems);
    }

    [Theory]
    [InlineData("not-json")]
    public async Task InvalidAgentResponsesFailClosed(string response)
    {
        var result = await PreviewAsync(Create(response));

        Assert.Equal(CreditCardPdfPreviewAccess.Allowed, result.Access);
        Assert.Null(result.Preview!.Primary);
        Assert.Equal(AgentExtractionAssessment.Rejected, result.Preview.Assessment);
        Assert.Contains("schema", result.Preview.AgentError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentPreviewNeverWritesFinancialData()
    {
        var orchestrator = new RecordingOrchestrator(JsonSerializer.Serialize(Document("Banco", "0.00"), CreditCardStatementAgentSchema.Json));
        var service = new CreditCardStatementAgentPreviewService(new FakeContext(), new FakePdfExtractor(), new FakePasswords(), orchestrator);

        var result = await PreviewAsync(service);

        Assert.Equal(CreditCardPdfPreviewAccess.Allowed, result.Access);
        Assert.Equal(1, orchestrator.Calls);
    }

    private static CreditCardStatementAgentPreviewService Create(string response) =>
        new(new FakeContext(), new FakePdfExtractor(), new FakePasswords(), new RecordingOrchestrator(response));

    private static Task<CreditCardStatementAgentPreviewResult> PreviewAsync(CreditCardStatementAgentPreviewService service)
    {
        var bytes = new byte[] { 1, 2, 3 };
        return service.PreviewAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new MemoryStream(bytes), bytes.Length,
            "fatura.pdf", null, null, CancellationToken.None);
    }

    private static CreditCardStatementAgentDocument Document(string issuer, string total, params CreditCardStatementAgentItem[] items) =>
        new(CreditCardStatementAgentSchema.Version, new(issuer, "1234", "2026-10", "2026-10-05", "2026-10-12", total), items);

    private static CreditCardStatementAgentItem Item(string description, decimal amount, string nature, string type, string section,
        bool included, int page, string confidence, int? installment = null, int? count = null) =>
        new(description, amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), nature, type,
            installment, count, section, included, page, description, confidence);

    private sealed class RecordingOrchestrator(string response) : ICreditCardStatementExtractionOrchestrator
    {
        public int Calls { get; private set; }
        public Task<CreditCardStatementExtractionResult> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (!CreditCardStatementAgentEvaluator.TryEvaluate(response, request.FileName, request.Document.Pages.Count, out var preview, out var error))
                return Task.FromResult(new CreditCardStatementExtractionResult("AgentPrimary", "Agent", null, AgentExtractionAssessment.Rejected, false, null, null, null, error));
            return Task.FromResult(new CreditCardStatementExtractionResult("AgentPrimary", "Agent", preview, CreditCardStatementAgentEvaluator.Assess(preview), false, null, null, null, null));
        }
    }
    private sealed class FakeContext : ICreditCardPdfContextRepository
    {
        public Task<CreditCardPdfContext?> GetAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<CreditCardPdfContext?>(new("Banco", null, "1234"));
    }
    private sealed class FakePasswords : ICreditCardPdfPasswordProvider
    {
        public Task<CreditCardPdfPasswordResolution> ResolveAsync(Guid holderUserId, string issuer, CancellationToken cancellationToken) =>
            Task.FromResult(CreditCardPdfPasswordResolution.Unavailable);
    }
    private sealed class FakePdfExtractor : ICreditCardPdfTextExtractor
    {
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken) =>
            Task.FromResult(new CreditCardPdfExtractedDocument([new(1, "page one"), new(2, "page two")]));
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat) => throw new NotSupportedException();
    }
}

using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class ServiceBillPreviewServiceTests
{
    private static readonly Guid FamilyId = Guid.NewGuid(); private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task PreviewIsReadOnlyAndReturnsExtraction()
    {
        var item = Item("WaterBill"); var repository = new Repository(item);
        var service = new ServiceBillPreviewService(repository, new Storage(), new Extractor(), new BillExtractor(), new() { Enabled = true });
        var result = await service.PreviewAsync(FamilyId, item.Id, UserId, null, CancellationToken.None);
        Assert.Equal(FinancialDocumentClassificationAccess.Allowed, result.Access); Assert.Equal("Reliable", result.Preview!.Assessment);
        Assert.Equal(0, repository.SaveCalls); Assert.Equal(FinancialDocumentInboxStatus.Received, item.Status);
    }

    [Fact]
    public async Task ProtectedPdfReturnsRejectedWithoutMutation()
    {
        var item = Item("ElectricityBill"); var repository = new Repository(item);
        var service = new ServiceBillPreviewService(repository, new Storage(), new Extractor(CreditCardPdfReadFailure.PasswordRequired), new BillExtractor(), new() { Enabled = true });
        var result = await service.PreviewAsync(FamilyId, item.Id, UserId, null, CancellationToken.None);
        Assert.Equal("Rejected", result.Preview!.Assessment); Assert.Equal("PasswordRequired", result.Preview.Result.FailureCode);
        Assert.Equal(0, repository.SaveCalls); Assert.Equal(FinancialDocumentInboxStatus.Received, item.Status);
    }

    private static FinancialDocumentInboxItem Item(string type) { var item = FinancialDocumentInboxItem.Create(Guid.NewGuid(), FamilyId, FinancialDocumentSource.Email, null, "bill.pdf", "hash", "application/pdf", "object", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); item.ApplyClassification(type, "High", "Deterministic", null, DateTimeOffset.UtcNow); return item; }
    private sealed class BillExtractor : IServiceBillExtractor { public Task<ServiceBillExtractionOutput> ExtractAsync(ServiceBillExtractionRequest request, CancellationToken cancellationToken) => Task.FromResult(new ServiceBillExtractionOutput(new(request.DocumentType, null, "2026-10", "2026-10-20", "10.00", null, null, "High", "Deterministic", null), "Reliable")); }
    private sealed class Extractor(CreditCardPdfReadFailure? failure = null) : ICreditCardPdfTextExtractor
    {
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken) => failure is { } value ? throw new CreditCardPdfReadException(value) : Task.FromResult(new CreditCardPdfExtractedDocument([new(1, "texto suficiente")]));
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat) => throw new NotSupportedException();
    }
    private sealed class Storage : IFinancialDocumentStorage { public Task<Stream?> OpenReadAsync(string reference, CancellationToken cancellationToken) => Task.FromResult<Stream?>(new MemoryStream([1])); public Task<string> SaveAsync(Guid familyId, Guid documentId, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException(); public Task DeleteAsync(string reference, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class Repository(FinancialDocumentInboxItem item) : IFinancialDocumentInboxRepository
    {
        public int SaveCalls { get; private set; }
        public Task<FinancialDocumentInboxItem?> GetAsync(Guid familyId, Guid id, Guid userId, CancellationToken cancellationToken) => Task.FromResult<FinancialDocumentInboxItem?>(familyId == FamilyId && id == item.Id && userId == UserId ? item : null);
        public Task SaveAsync(CancellationToken cancellationToken) { SaveCalls++; return Task.CompletedTask; }
        public Task<bool> CanAccessFamilyAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) => Task.FromResult(false); public Task<bool> FamilyExistsAsync(Guid familyId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid familyId, string hash, CancellationToken cancellationToken) => Task.FromResult<FinancialDocumentInboxItem?>(null); public Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid familyId, FinancialDocumentSource source, string externalId, CancellationToken cancellationToken) => Task.FromResult<FinancialDocumentInboxItem?>(null);
        public Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialDocumentInboxItem>>([]); public Task<bool> TryAddAsync(FinancialDocumentInboxItem value, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}

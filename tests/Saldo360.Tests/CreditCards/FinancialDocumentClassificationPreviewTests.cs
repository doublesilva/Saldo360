using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class FinancialDocumentClassificationPreviewTests
{
    private static readonly Guid FamilyId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task PreviewDoesNotPersistOrChangeInboxStatus()
    {
        var item = FinancialDocumentInboxItem.Create(Guid.NewGuid(), FamilyId, FinancialDocumentSource.Email, "gmail:m:part:1", "document.pdf", "hash", "application/pdf", "object", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var repository = new Repository(item); var classifier = new Classifier();
        var service = new FinancialDocumentClassificationPreviewService(repository, new Storage(), new Extractor("Consumo 10 kWh unidade consumidora medição tarifa de energia"), classifier);

        var result = await service.PreviewAsync(FamilyId, item.Id, UserId, null, CancellationToken.None);

        Assert.Equal(FinancialDocumentClassificationAccess.Allowed, result.Access);
        Assert.Equal("ElectricityBill", result.Classification!.DocumentType);
        Assert.Equal(FinancialDocumentInboxStatus.Received, item.Status);
        Assert.Equal(0, repository.SaveCalls);
    }

    [Fact]
    public async Task ProtectedDocumentReturnsUnknownWithoutMutation()
    {
        var item = FinancialDocumentInboxItem.Create(Guid.NewGuid(), FamilyId, FinancialDocumentSource.Email, null, "locked.pdf", "hash", "application/pdf", "object", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var repository = new Repository(item);
        var service = new FinancialDocumentClassificationPreviewService(repository, new Storage(), new Extractor(null, CreditCardPdfReadFailure.PasswordRequired), new Classifier());
        var result = await service.PreviewAsync(FamilyId, item.Id, UserId, null, CancellationToken.None);
        Assert.Equal("Unknown", result.Classification!.DocumentType); Assert.Equal("PasswordRequired", result.Classification.FailureCode);
        Assert.Equal(FinancialDocumentInboxStatus.Received, item.Status); Assert.Equal(0, repository.SaveCalls);
    }

    private sealed class Classifier : IFinancialDocumentClassifier
    {
        private readonly CompositeFinancialDocumentClassifier inner = new(new(), new Agent());
        public Task<FinancialDocumentClassificationResult> ClassifyAsync(FinancialDocumentClassificationRequest request, CancellationToken cancellationToken) => inner.ClassifyAsync(request, cancellationToken);
    }
    private sealed class Agent : IFinancialDocumentClassificationAgent { public Task<string> ClassifyAsync(FinancialDocumentClassificationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class Extractor(string? text, CreditCardPdfReadFailure? failure = null) : ICreditCardPdfTextExtractor
    {
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken) => failure is { } value ? throw new CreditCardPdfReadException(value) : Task.FromResult(new CreditCardPdfExtractedDocument([new(1, text!)]));
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat) => throw new NotSupportedException();
    }
    private sealed class Storage : IFinancialDocumentStorage
    {
        public Task<string> SaveAsync(Guid familyId, Guid documentId, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(string reference, CancellationToken cancellationToken) => Task.FromResult<Stream?>(new MemoryStream([1]));
        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Repository(FinancialDocumentInboxItem item) : IFinancialDocumentInboxRepository
    {
        public int SaveCalls { get; private set; }
        public Task<FinancialDocumentInboxItem?> GetAsync(Guid familyId, Guid id, Guid userId, CancellationToken cancellationToken) => Task.FromResult<FinancialDocumentInboxItem?>(familyId == FamilyId && id == item.Id && userId == UserId ? item : null);
        public Task SaveAsync(CancellationToken cancellationToken) { SaveCalls++; return Task.CompletedTask; }
        public Task<bool> CanAccessFamilyAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> FamilyExistsAsync(Guid familyId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid familyId, string hash, CancellationToken cancellationToken) => Task.FromResult<FinancialDocumentInboxItem?>(null);
        public Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid familyId, FinancialDocumentSource source, string externalId, CancellationToken cancellationToken) => Task.FromResult<FinancialDocumentInboxItem?>(null);
        public Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialDocumentInboxItem>>([]);
        public Task<bool> TryAddAsync(FinancialDocumentInboxItem value, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}

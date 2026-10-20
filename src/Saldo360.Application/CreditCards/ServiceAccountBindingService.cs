namespace Saldo360.Application.CreditCards;

public enum ServiceAccountBindingStoreStatus { Created, AlreadyExists, Conflict, Invalid }
public sealed record ServiceAccountBindingStoreResult(ServiceAccountBindingStoreStatus Status, Guid? BindingId = null,
    string? AccountReferenceMasked = null, string? ServiceReferenceMasked = null);
public sealed record ServiceAccountBindingResult(string Status, Guid? BindingId, string DocumentType,
    string? AccountReferenceMasked, string? ServiceReferenceMasked);

public interface IServiceAccountBindingRepository
{
    Task<ServiceAccountBindingStoreResult> CreateAsync(Guid workspaceId, Guid documentId, Guid userId, string documentType,
        string issuerNormalized, string? accountHash, string? accountMasked, string? serviceHash, string? serviceMasked,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<ServiceAccountBindingStoreResult> GetAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken cancellationToken);
}

public sealed class ServiceAccountBindingService(ServiceBillPreviewService extraction, IServiceAccountBindingRepository repository,
    ServiceBillReconciliationOptions options, TimeProvider clock)
{
    public async Task<ServiceAccountBindingResult?> GetAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken cancellationToken)
    {
        var stored = await repository.GetAsync(workspaceId, documentId, userId, cancellationToken);
        return stored.Status == ServiceAccountBindingStoreStatus.AlreadyExists
            ? new(stored.Status.ToString(), stored.BindingId, string.Empty, stored.AccountReferenceMasked, stored.ServiceReferenceMasked) : null;
    }
    public async Task<(FinancialDocumentClassificationAccess Access, ServiceAccountBindingResult? Result,
        IDictionary<string, string[]>? Errors)> CreateAsync(Guid workspaceId, Guid documentId, Guid userId, string? password,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled || !options.BindingEnabled) return Invalid("feature", "O vínculo de conta de serviço está desabilitado.");
        var preview = await extraction.PreviewAsync(workspaceId, documentId, userId, password, cancellationToken);
        if (preview.Access != FinancialDocumentClassificationAccess.Allowed || preview.Preview is null)
            return (preview.Access, null, preview.Errors);
        var bill = preview.Preview;
        if (bill.Assessment != "Reliable" || bill.Result.DocumentType is not ("ElectricityBill" or "WaterBill" or "TelecomBill"))
            return Invalid("document", "A extração da conta de serviço não é confiável.");
        var accountHash = ServiceReferenceIdentity.Hash(bill.Result.AccountReference);
        var serviceHash = ServiceReferenceIdentity.Hash(bill.Result.ServiceReference);
        if (accountHash is null && serviceHash is null) return Invalid("document", "O documento não possui referência identificável.");
        var stored = await repository.CreateAsync(workspaceId, documentId, userId, bill.Result.DocumentType,
            ServiceReferenceIdentity.Normalize(bill.Result.Issuer) ?? string.Empty, accountHash,
            ServiceReferenceIdentity.Mask(bill.Result.AccountReference), serviceHash,
            ServiceReferenceIdentity.Mask(bill.Result.ServiceReference), clock.GetUtcNow(), cancellationToken);
        var result = new ServiceAccountBindingResult(stored.Status.ToString(), stored.BindingId, bill.Result.DocumentType,
            stored.AccountReferenceMasked, stored.ServiceReferenceMasked);
        return (FinancialDocumentClassificationAccess.Allowed, result, null);
    }
    private static (FinancialDocumentClassificationAccess, ServiceAccountBindingResult?, IDictionary<string, string[]>?) Invalid(string field, string message) =>
        (FinancialDocumentClassificationAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

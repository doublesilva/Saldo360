using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public sealed class ServiceAccountBinding : Entity
{
    private ServiceAccountBinding() { DocumentType = IssuerNormalized = Origin = string.Empty; }
    public Guid WorkspaceId { get; private init; }
    public Guid RecurringTemplateId { get; private init; }
    public string DocumentType { get; private init; }
    public string IssuerNormalized { get; private init; }
    public string? AccountReferenceHash { get; private init; }
    public string? AccountReferenceMasked { get; private init; }
    public string? ServiceReferenceHash { get; private init; }
    public string? ServiceReferenceMasked { get; private init; }
    public string Origin { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static ServiceAccountBinding Create(Guid workspaceId, Guid recurrenceId, string documentType, string issuerNormalized,
        string? accountHash, string? accountMasked, string? serviceHash, string? serviceMasked, DateTimeOffset now)
    {
        if (accountHash is null && serviceHash is null) throw new ArgumentException("Informe ao menos uma referência.");
        return new() { WorkspaceId = workspaceId, RecurringTemplateId = recurrenceId, DocumentType = documentType,
            IssuerNormalized = issuerNormalized, AccountReferenceHash = accountHash, AccountReferenceMasked = accountMasked,
            ServiceReferenceHash = serviceHash, ServiceReferenceMasked = serviceMasked, Origin = "ConfirmedServiceBill",
            CreatedAt = now, UpdatedAt = now };
    }
}

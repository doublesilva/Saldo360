using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public enum FinancialCreditCardImportStatus { Completed }
public enum FinancialCreditCardImportItemStatus { Imported }

public sealed class FinancialCreditCardImportBatch : Entity
{
    private readonly List<FinancialCreditCardImportItem> items = [];
    private FinancialCreditCardImportBatch() { PayloadHash = string.Empty; }
    public Guid FamilyId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public string PayloadHash { get; private init; }
    public FinancialCreditCardImportStatus Status { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }
    public IReadOnlyCollection<FinancialCreditCardImportItem> Items => items;

    public static FinancialCreditCardImportBatch Create(Guid id, Guid familyId, Guid cardId, string payloadHash, Guid userId, DateTimeOffset now) =>
        new() { Id = id, FamilyId = familyId, CreditCardId = cardId, PayloadHash = payloadHash, Status = FinancialCreditCardImportStatus.Completed, CreatedAt = now, CreatedByUserId = userId };

    public void AddItem(FinancialCreditCardImportItem item) => items.Add(item);
}

public sealed class FinancialCreditCardImportItem : Entity
{
    private FinancialCreditCardImportItem() { }
    public Guid FamilyId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public Guid ImportBatchId { get; private init; }
    public int RowNumber { get; private init; }
    public string? ExternalId { get; private init; }
    public Guid PurchaseId { get; private init; }
    public FinancialCreditCardImportItemStatus Status { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }

    public static FinancialCreditCardImportItem Create(Guid familyId, Guid cardId, Guid batchId, int rowNumber, string? externalId, Guid purchaseId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), FamilyId = familyId, CreditCardId = cardId, ImportBatchId = batchId, RowNumber = rowNumber, ExternalId = externalId, PurchaseId = purchaseId, Status = FinancialCreditCardImportItemStatus.Imported, CreatedAt = now };
}

using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public enum FinancialCreditCardImportStatus { Completed }
public enum FinancialCreditCardImportSource { Csv, Pdf }
public enum FinancialCreditCardImportItemStatus { Imported, AlreadyExisting, Informational }

public sealed class FinancialCreditCardImportBatch : Entity
{
    private readonly List<FinancialCreditCardImportItem> items = [];
    private FinancialCreditCardImportBatch() { PayloadHash = string.Empty; }
    public Guid WorkspaceId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public string PayloadHash { get; private init; }
    public FinancialCreditCardImportStatus Status { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }
    public FinancialCreditCardImportSource Source { get; private init; }
    public string? DocumentHash { get; private init; }
    public string? Parser { get; private init; }
    public string? Competence { get; private init; }
    public string? DecisionHash { get; private init; }
    public decimal? OfficialTotal { get; private init; }
    public IReadOnlyCollection<FinancialCreditCardImportItem> Items => items;

    public static FinancialCreditCardImportBatch Create(Guid id, Guid workspaceId, Guid cardId, string payloadHash, Guid userId, DateTimeOffset now) =>
        new() { Id = id, WorkspaceId = workspaceId, CreditCardId = cardId, PayloadHash = payloadHash, Source = FinancialCreditCardImportSource.Csv, Status = FinancialCreditCardImportStatus.Completed, CreatedAt = now, CreatedByUserId = userId };

    public static FinancialCreditCardImportBatch CreatePdf(Guid id, Guid workspaceId, Guid cardId, string documentHash, string decisionHash, string parser, string? competence, decimal officialTotal, Guid userId, DateTimeOffset now) =>
        new() { Id = id, WorkspaceId = workspaceId, CreditCardId = cardId, PayloadHash = decisionHash, Source = FinancialCreditCardImportSource.Pdf, DocumentHash = documentHash, DecisionHash = decisionHash, Parser = parser, Competence = competence, OfficialTotal = officialTotal, Status = FinancialCreditCardImportStatus.Completed, CreatedAt = now, CreatedByUserId = userId };

    public void AddItem(FinancialCreditCardImportItem item) => items.Add(item);
}

public sealed class FinancialCreditCardImportItem : Entity
{
    private FinancialCreditCardImportItem() { }
    public Guid WorkspaceId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public Guid ImportBatchId { get; private init; }
    public int RowNumber { get; private init; }
    public string? ExternalId { get; private init; }
    public string? ComponentId { get; private init; }
    public Guid? PurchaseId { get; private init; }
    public string? ComponentType { get; private init; }
    public Guid? EntityId { get; private init; }
    public FinancialCreditCardImportItemStatus Status { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }

    public static FinancialCreditCardImportItem Create(Guid workspaceId, Guid cardId, Guid batchId, int rowNumber, string? externalId, Guid purchaseId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreditCardId = cardId, ImportBatchId = batchId, RowNumber = rowNumber, ExternalId = externalId, PurchaseId = purchaseId, Status = FinancialCreditCardImportItemStatus.Imported, CreatedAt = now };

    public static FinancialCreditCardImportItem CreatePdf(Guid workspaceId, Guid cardId, Guid batchId, int rowNumber, string componentId, string componentType, Guid? entityId, FinancialCreditCardImportItemStatus status, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreditCardId = cardId, ImportBatchId = batchId, RowNumber = rowNumber, ComponentId = componentId, ExternalId = componentType == "Purchase" && status == FinancialCreditCardImportItemStatus.Imported ? componentId : null, ComponentType = componentType, EntityId = entityId, PurchaseId = componentType == "Purchase" ? entityId : null, Status = status, CreatedAt = now };
}

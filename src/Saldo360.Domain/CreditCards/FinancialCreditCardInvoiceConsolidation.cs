using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

/// <summary>Immutable audit of an official composition. Financial components continue using the existing aggregates.</summary>
public sealed class FinancialCreditCardInvoiceConsolidation : Entity
{
    private FinancialCreditCardInvoiceConsolidation() { }
    public Guid WorkspaceId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public Guid InvoiceId { get; private init; }
    public Guid? DocumentId { get; private init; }
    public string DocumentHash { get; private init; } = "";
    public string DecisionHash { get; private init; } = "";
    public string StateHash { get; private init; } = "";
    public string SnapshotJson { get; private init; } = "";
    public int Version { get; private init; }
    public decimal OfficialTotal { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }
    public static FinancialCreditCardInvoiceConsolidation Create(Guid workspace, Guid card, Guid invoice, Guid? document,
        string documentHash, string decisionHash, string stateHash, string snapshot, int version, decimal total, Guid user, DateTimeOffset now) =>
        new() { WorkspaceId = workspace, CreditCardId = card, InvoiceId = invoice, DocumentId = document, DocumentHash = documentHash,
            DecisionHash = decisionHash, StateHash = stateHash, SnapshotJson = snapshot, Version = version, OfficialTotal = total, CreatedByUserId = user, CreatedAt = now };
}

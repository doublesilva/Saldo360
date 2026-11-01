using Saldo360.Domain.Common;

namespace Saldo360.Domain.Recurrences;

public enum SettlementOrigin { Auto, Manual }

/// <summary>
/// Vínculo CONFIRMADO entre uma obrigação mensal (despesa fixa) e a movimentação bancária que a quitou integralmente.
/// Identidade da obrigação: (recorrência, competência) quando ela vem de uma recorrência (ocorrência virtual, materializada ou exceção)
/// ou a própria previsão avulsa. "A revisar" não é vínculo: não existe linha para isso. A movimentação nunca é alterada.
/// </summary>
public sealed class FinancialObligationSettlement : Entity
{
    private FinancialObligationSettlement() { }

    public Guid WorkspaceId { get; private init; }
    public Guid? RecurrenceId { get; private init; }
    /// <summary>Primeiro dia do mês da competência da obrigação.</summary>
    public DateOnly Competence { get; private init; }
    public Guid? PlannedTransactionId { get; private init; }
    public Guid FinancialTransactionId { get; private init; }
    public SettlementOrigin Origin { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? RemovedAt { get; private set; }

    public static FinancialObligationSettlement Create(Guid workspaceId, Guid? recurrenceId, DateOnly competence, Guid? plannedTransactionId,
        Guid transactionId, SettlementOrigin origin, DateTimeOffset now)
    {
        if (recurrenceId is null && plannedTransactionId is null) throw new ArgumentException("A obrigação precisa de recorrência ou previsão.");
        return new() { WorkspaceId = workspaceId, RecurrenceId = recurrenceId, Competence = new DateOnly(competence.Year, competence.Month, 1),
            PlannedTransactionId = plannedTransactionId, FinancialTransactionId = transactionId, Origin = origin, CreatedAt = now };
    }

    public bool Remove(DateTimeOffset now) { if (RemovedAt is not null) return false; RemovedAt = now; return true; }
}

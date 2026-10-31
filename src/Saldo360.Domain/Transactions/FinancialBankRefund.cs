using Saldo360.Domain.Common;

namespace Saldo360.Domain.Transactions;

/// <summary>
/// Vínculo auditável entre uma devolução (crédito de caixa, <see cref="FinancialTransactionOrigin.PluggyRefund"/>) e a despesa original que ela devolve.
/// Uma despesa pode ter várias devoluções (parciais e múltiplas), desde que a soma das ativas não ultrapasse o valor da despesa; cada crédito devolve uma
/// única despesa. A despesa original mantém valor, data e histórico; o vínculo só reduz o consumo na data do crédito. Cancelar o vínculo preserva o registro.
/// </summary>
public sealed class FinancialBankRefund : Entity
{
    private FinancialBankRefund() { }

    public Guid WorkspaceId { get; private init; }
    /// <summary>Lançamento de crédito (origem <c>PluggyRefund</c>).</summary>
    public Guid RefundTransactionId { get; private init; }
    /// <summary>Despesa devolvida (lançamento de despesa da mesma família).</summary>
    public Guid OriginalTransactionId { get; private init; }
    /// <summary>Valor devolvido: sempre o valor integral do crédito, positivo.</summary>
    public decimal Amount { get; private init; }
    public Guid ConfirmedByUserId { get; private init; }
    public DateTimeOffset ConfirmedAt { get; private init; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }

    public bool IsActive => CancelledAt is null;

    public static FinancialBankRefund Create(Guid workspaceId, Guid refundTransactionId, Guid originalTransactionId, decimal amount, Guid userId, DateTimeOffset now)
    {
        if (refundTransactionId == originalTransactionId) throw new ArgumentException("A devolução e a despesa devem ser lançamentos distintos.");
        if (amount <= 0m) throw new ArgumentOutOfRangeException(nameof(amount));
        return new() { WorkspaceId = workspaceId, RefundTransactionId = refundTransactionId, OriginalTransactionId = originalTransactionId, Amount = amount, ConfirmedByUserId = userId, ConfirmedAt = now };
    }

    /// <summary>Cancela preservando o registro. Idempotente.</summary>
    public bool Cancel(Guid userId, DateTimeOffset now)
    {
        if (CancelledAt is not null) return false;
        CancelledAt = now; CancelledByUserId = userId;
        return true;
    }
}

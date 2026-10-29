using Saldo360.Domain.Common;

namespace Saldo360.Domain.Pluggy;

/// <summary>Estado da conciliação entre o total bancário e o que o Saldo360 importou. Independe do fechamento e do pagamento.</summary>
public enum PluggyBillReconciliation
{
    /// <summary>Ainda não verificada (fatura aberta, sem total ou ainda não avaliada).</summary>
    NotVerified,
    /// <summary>O total bancário é igual ao que foi importado (compras, encargos e créditos), sem itens pendentes ou em revisão.</summary>
    Reconciled,
    /// <summary>Fatura fechada, cobertura completa e sem itens em aberto, mas os totais diferem. A diferença é registrada, nunca compensada.</summary>
    Divergent,
    /// <summary>Há itens pendentes ou em revisão da fatura, ou o histórico importado não a cobre inteira: não dá para concluir.</summary>
    Incomplete,
}

/// <summary>
/// Fatura do cartão como o banco a informou (T09.3). É a evidência que permite ao Saldo360 fechar a fatura oficial correspondente e
/// registrar o pagamento, e que localiza cada compra importada na fatura certa (pelo fechamento real do banco, não só pelo dia
/// configurado). Guarda apenas datas e totais: nada de itens, pagador ou números de cartão.
/// </summary>
public sealed class PluggyCardBill : Entity
{
    private PluggyCardBill() { PluggyBillId = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public Guid AccountLinkId { get; private init; }
    public string PluggyBillId { get; private init; }
    public DateOnly? ClosingDate { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public decimal? TotalAmount { get; private set; }
    public string? Currency { get; private set; }
    /// <summary>Soma dos encargos que o banco informou na fatura (IOF, juros, multa). Só informativo: os encargos entram como lançamentos do cartão.</summary>
    public decimal? FinanceChargesTotal { get; private set; }
    public decimal? MinimumPayment { get; private set; }
    public PluggyBillReconciliation Reconciliation { get; private set; }
    /// <summary>Código estável do motivo (por exemplo OpenItems, MissingHistory, TotalMismatch).</summary>
    public string? ReconciliationReason { get; private set; }
    /// <summary>Total do que o Saldo360 importou para esta fatura (compras + encargos − créditos aplicados) na última avaliação.</summary>
    public decimal? ComponentsTotal { get; private set; }
    public DateTimeOffset? ReconciledAt { get; private set; }
    public DateTimeOffset FirstObservedAt { get; private init; }
    public DateTimeOffset LastObservedAt { get; private set; }

    public static PluggyCardBill Create(Guid workspaceId, Guid accountLinkId, string pluggyBillId, DateOnly? closing, DateOnly? due, decimal? total, string? currency, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || accountLinkId == Guid.Empty || string.IsNullOrWhiteSpace(pluggyBillId) || pluggyBillId.Length > 80) throw new ArgumentException("Fatura inválida.");
        var bill = new PluggyCardBill { WorkspaceId = workspaceId, AccountLinkId = accountLinkId, PluggyBillId = pluggyBillId, FirstObservedAt = now };
        bill.Observe(closing, due, total, currency, now);
        return bill;
    }

    /// <summary>Atributos adicionais informados pelo banco (encargos e pagamento mínimo).</summary>
    public void ObserveDetails(decimal? financeChargesTotal, decimal? minimumPayment)
    {
        FinanceChargesTotal = financeChargesTotal; MinimumPayment = minimumPayment;
    }

    public void SetReconciliation(PluggyBillReconciliation state, string? reason, decimal? componentsTotal, DateTimeOffset now)
    {
        var clipped = reason is { Length: > 40 } ? reason[..40] : reason;
        // Reavaliar sem mudança não reescreve a fatura: o instante só muda quando o resultado muda.
        if (ReconciledAt is not null && Reconciliation == state && ReconciliationReason == clipped && ComponentsTotal == componentsTotal) return;
        Reconciliation = state; ReconciliationReason = clipped; ComponentsTotal = componentsTotal; ReconciledAt = now;
    }

    public void Observe(DateOnly? closing, DateOnly? due, decimal? total, string? currency, DateTimeOffset now)
    {
        ClosingDate = closing; DueDate = due; TotalAmount = total;
        Currency = string.IsNullOrWhiteSpace(currency) ? null : currency.Length > 8 ? currency[..8] : currency;
        LastObservedAt = now;
    }
}


/// <summary>
/// Pagamento que o banco informa dentro da fatura (<c>payments[]</c>): evidência estruturada de que um valor foi pago numa data, usada para
/// reconhecer a saída bancária correspondente sem depender de descrição. Não é um lançamento: só guarda valor, data e modo.
/// </summary>
public sealed class PluggyCardBillPayment : Entity
{
    private PluggyCardBillPayment() { }

    public Guid WorkspaceId { get; private init; }
    public Guid BillId { get; private init; }
    public int Ordinal { get; private init; }
    public decimal Amount { get; private init; }
    public DateOnly PaymentDate { get; private init; }
    public string? PaymentMode { get; private init; }
    public string? ValueType { get; private init; }

    public static PluggyCardBillPayment Create(Guid workspaceId, Guid billId, int ordinal, decimal amount, DateOnly paymentDate, string? mode, string? valueType) => new()
    {
        WorkspaceId = workspaceId, BillId = billId, Ordinal = ordinal, Amount = amount, PaymentDate = paymentDate,
        PaymentMode = mode is { Length: > 24 } ? mode[..24] : mode, ValueType = valueType is { Length: > 24 } ? valueType[..24] : valueType,
    };
}

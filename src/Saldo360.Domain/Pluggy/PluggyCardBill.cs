using Saldo360.Domain.Common;

namespace Saldo360.Domain.Pluggy;

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
    public DateTimeOffset FirstObservedAt { get; private init; }
    public DateTimeOffset LastObservedAt { get; private set; }

    public static PluggyCardBill Create(Guid workspaceId, Guid accountLinkId, string pluggyBillId, DateOnly? closing, DateOnly? due, decimal? total, string? currency, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || accountLinkId == Guid.Empty || string.IsNullOrWhiteSpace(pluggyBillId) || pluggyBillId.Length > 80) throw new ArgumentException("Fatura inválida.");
        var bill = new PluggyCardBill { WorkspaceId = workspaceId, AccountLinkId = accountLinkId, PluggyBillId = pluggyBillId, FirstObservedAt = now };
        bill.Observe(closing, due, total, currency, now);
        return bill;
    }

    public void Observe(DateOnly? closing, DateOnly? due, decimal? total, string? currency, DateTimeOffset now)
    {
        ClosingDate = closing; DueDate = due; TotalAmount = total;
        Currency = string.IsNullOrWhiteSpace(currency) ? null : currency.Length > 8 ? currency[..8] : currency;
        LastObservedAt = now;
    }
}

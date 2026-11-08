using Saldo360.Application.CreditCards.Projection;

namespace Saldo360.Application.Entries;

/// <summary>
/// T13.2: regra única de precedência entre movimentos que descrevem a mesma obrigação, aplicada só no modelo de leitura (nada é apagado ou gravado).
/// Ordem: 1 fato efetivo confirmado, 2 obrigação oficialmente registrada (cartão, dívida), 3 previsão ou exceção individual (inclui o que o banco já informou), 4 ocorrência calculada/estimada.
/// Um movimento de ordem mais baixa some quando o de ordem mais alta já o representa; o resto permanece.
/// </summary>
public static class FinancialEntryPrecedence
{
    public const string Effective = "Effective", InvoicePayment = "InvoicePayment", DebtPayment = "DebtPayment", Planned = "Planned", CardLaunched = "CardLaunched", CardForecast = "CardForecast", CardEstimated = "CardEstimated", Cancelled = "Cancelled";

    private static readonly HashSet<string> EstimatedKinds = ["EstimatedInstallment", "Recurring", "ManualRecurring"];

    public static int Rank(FinancialEntryDto e) => e.Source switch
    {
        "Transaction" or "Transfer" when e.Status == "Realized" => 1,
        "CardPurchase" or "CardAdjustment" or "Debt" => 2,
        "CardProjected" => EstimatedKinds.Contains(e.Origin) ? 4 : 3,
        _ => 3,
    };

    /// <summary>Estágio financeiro: o que o movimento é, sem insinuar que a fatura do cartão foi paga (compra lançada ≠ fatura paga ≠ pagamento realizado).</summary>
    public static string Stage(FinancialEntryDto e) => e.Status == "Cancelled" ? Cancelled : e.Source switch
    {
        "Transaction" => e.Origin switch { "CreditCardInvoicePayment" => InvoicePayment, "DebtInstallmentPayment" => DebtPayment, _ => Effective },
        "Transfer" => e.Status == "Realized" ? Effective : Planned,
        "CardPurchase" or "CardAdjustment" => CardLaunched,
        "CardProjected" => EstimatedKinds.Contains(e.Origin) ? CardEstimated : CardForecast,
        _ => Planned,
    };

    public static IReadOnlyList<FinancialEntryDto> Resolve(IEnumerable<FinancialEntryDto> entries)
    {
        // Mesma identidade lógica: fica a de maior precedência.
        var unique = entries.GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.OrderBy(Rank).First()).ToList();
        // Cobrança projetada de cartão que a fatura já registra (mesmo cartão, mesma competência da fatura e mesma compra) não é uma segunda obrigação.
        var registered = unique.Where(e => e.Card is { Id: not null } && e.Source is "CardPurchase" or "CardAdjustment")
            .Select(e => (e.Card!.Id, e.Competence, Key: CardInstallmentProjector.BaseKey(e.Description))).ToHashSet();
        return unique.Where(e => e.Source != "CardProjected" || e.Card is not { Id: not null } || !registered.Contains((e.Card.Id, e.Competence, CardInstallmentProjector.BaseKey(e.Description))))
            .Select(e => e with { Stage = Stage(e) }).ToList();
    }
}

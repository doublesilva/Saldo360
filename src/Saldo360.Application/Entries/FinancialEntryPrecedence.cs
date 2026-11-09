namespace Saldo360.Application.Entries;

/// <summary>
/// T13.2: regra única de precedência entre movimentos que descrevem a mesma obrigação, aplicada só no modelo de leitura (nada é apagado ou gravado).
/// Ordem: 1 fato efetivo confirmado, 2 obrigação oficialmente registrada (cartão, dívida), 3 previsão ou exceção individual (inclui o que o banco já informou), 4 ocorrência calculada/estimada.
/// Um movimento de ordem mais baixa some quando o de ordem mais alta tem a MESMA identidade; o resto permanece.
/// </summary>
public static class FinancialEntryPrecedence
{
    public const string Effective = "Effective", InvoicePayment = "InvoicePayment", DebtPayment = "DebtPayment", Planned = "Planned", CardLaunched = "CardLaunched", CardForecast = "CardForecast", CardEstimated = "CardEstimated", Cancelled = "Cancelled";

    private static readonly HashSet<string> EstimatedKinds = ["EstimatedInstallment", "Recurring"];

    /// <summary>Estimativa = tipo calculado por natureza, ou assinatura cujo valor não é fixo; assinatura manual de valor fixo é compromisso.</summary>
    private static bool IsEstimate(FinancialEntryDto e) => EstimatedKinds.Contains(e.Origin) || e.Origin == "ManualRecurring" && e.Flags.Contains("Estimated");

    public static int Rank(FinancialEntryDto e) => e.Source switch
    {
        "Transaction" or "Transfer" when e.Status == "Realized" => 1,
        "CardPurchase" or "CardAdjustment" or "Debt" => 2,
        "CardProjected" => IsEstimate(e) ? 4 : 3,
        _ => 3,
    };

    /// <summary>Estágio financeiro: o que o movimento é, sem insinuar que a fatura do cartão foi paga (compra lançada ≠ fatura paga ≠ pagamento realizado).</summary>
    public static string Stage(FinancialEntryDto e) => e.Status == "Cancelled" ? Cancelled : e.Source switch
    {
        "Transaction" => e.Origin switch { "CreditCardInvoicePayment" => InvoicePayment, "DebtInstallmentPayment" => DebtPayment, _ => Effective },
        "Transfer" => e.Status == "Realized" ? Effective : Planned,
        "CardPurchase" or "CardAdjustment" => CardLaunched,
        "CardProjected" => IsEstimate(e) ? CardEstimated : CardForecast,
        _ => Planned,
    };

    public static IReadOnlyList<FinancialEntryDto> Resolve(IEnumerable<FinancialEntryDto> entries)
    {
        // Mesma identidade lógica: fica a de maior precedência.
        var unique = entries.GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.OrderBy(Rank).First()).ToList();
        // A identidade lógica é o Id (cartão + ocorrência); a descrição nunca elimina uma cobrança: duas assinaturas legítimas com o mesmo nome permanecem.
        // A substituição por compra registrada acontece na origem (vínculo de materialização ou cobrança efetiva do banco), não por semelhança de texto.
        // Manual e detectada com a mesma descrição e valores diferentes (a mesma identidade já foi resolvida na origem): preserva as duas e sinaliza a possível duplicidade para revisão.
        var doubtful = unique.Where(e => e.Source == "CardProjected" && e.Origin is "ManualRecurring" or "Recurring").GroupBy(e => (e.Card?.Id, e.Competence, Text: e.Description.Trim().ToLowerInvariant()))
            .Where(g => g.Any(e => e.Origin == "ManualRecurring") && g.Any(e => e.Origin == "Recurring")).SelectMany(g => g).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        return unique.Select(e => e with { Stage = Stage(e), Flags = doubtful.Contains(e.Id) ? [.. e.Flags, "PossibleDuplicate"] : e.Flags }).ToList();
    }
}

using Saldo360.Application.Classification;
using Saldo360.Application.CreditCards.Projection;

namespace Saldo360.Application.Pluggy;

/// <summary>Uma parcela de cartão observada, com o que é preciso para decidir se pertence a um financiamento de fatura.</summary>
public sealed record FinancingObservation(Guid CardId, string Description, decimal Amount, int? Total, string? ExternalCategory);

/// <summary>
/// Financiamento do saldo de uma fatura: a fatura já foi contabilizada como consumo; as parcelas do parcelamento dela NÃO são novo consumo, mas continuam obrigação de pagamento.
/// A descrição NUNCA decide sozinha. Operação candidata = parcelas estruturadas (total &gt; 1) + categoria "Credit card payment" do banco + origem em cartão.
/// Confirmação = a própria série comprova o cronograma: pelo menos <see cref="MinimumInstallments"/> parcelas candidatas, do mesmo cartão, descrição-base e total, com valor idêntico. Sem
/// confirmação a operação fica como compra comum e é apenas listada para revisão (nada é reclassificado em silêncio).
/// </summary>
public static class InvoiceFinancingEvidence
{
    public const int MinimumInstallments = 2;
    public const string ExternalCategoryKey = "credit card payment";

    public static bool IsCandidate(FinancingObservation o) => o.Total is > 1 && FinancialCategoryMatching.Key(o.ExternalCategory) == ExternalCategoryKey;

    public static string SeriesKey(FinancingObservation o) => $"{o.CardId:N}|{CardInstallmentProjector.BaseKey(o.Description)}|{o.Total}|{o.Amount:0.00}";

    /// <summary>Séries (chave) com evidência suficiente de financiamento de fatura.</summary>
    public static IReadOnlySet<string> ConfirmedSeries(IEnumerable<FinancingObservation> observations) =>
        observations.Where(IsCandidate).GroupBy(SeriesKey).Where(g => g.Count() >= MinimumInstallments).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

    public static bool IsFinancing(FinancingObservation o, IReadOnlySet<string> confirmed) => IsCandidate(o) && confirmed.Contains(SeriesKey(o));

    /// <summary>Candidatas sem evidência suficiente: ficam como compra comum e vão para revisão.</summary>
    public static IReadOnlyList<FinancingObservation> UnconfirmedCandidates(IEnumerable<FinancingObservation> observations)
    {
        var list = observations.ToList(); var confirmed = ConfirmedSeries(list);
        return list.Where(o => IsCandidate(o) && !confirmed.Contains(SeriesKey(o))).ToList();
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Saldo360.Application.Pluggy;

public sealed record PluggyRecurringChargeObservation(Guid CardId, Guid CategoryId, string Description, string? MerchantName,
    string? MerchantCategory, DateOnly Date, decimal Amount, int ClosingDay, int DueDay, string SourceId);

public sealed record PluggyRecurringChargeDetection(string ExternalKey, Guid CardId, Guid CategoryId, string Description,
    decimal ProjectedAmount, bool IsEstimated, int EvidenceCount, string Confidence, DateOnly FirstDate, DateOnly LastDate,
    IReadOnlyList<string> SourceIds);

/// <summary>Detector conservador e puro. Duas competências formam candidato; só três competências mensais consecutivas viram alta confiança.</summary>
public static class PluggyRecurringChargeDetector
{
    private static readonly string[] GenericAggregators = ["apple", "google", "pagamento", "compra cartao"];
    private sealed record Normalized(PluggyRecurringChargeObservation Observation, string Identity, int Cycle);

    public static IReadOnlyList<PluggyRecurringChargeDetection> Detect(IEnumerable<PluggyRecurringChargeObservation> source)
    {
        var observations = source.Where(x => x.Amount > 0m && x.Date != default)
            .GroupBy(x => (x.CardId, x.Date, x.Amount, KeyText(x.Description), Merchant: KeyText(x.MerchantName)))
            .Select(g => g.OrderBy(x => x.SourceId, StringComparer.Ordinal).First())
            .Select(x => new Normalized(x, Identity(x) ?? string.Empty, Cycle(x)))
            .Where(x => x.Identity.Length > 0)
            .GroupBy(x => (x.Observation.CardId, x.Observation.CategoryId, x.Identity), x => x)
            .SelectMany(group => Build(group.Key.CardId, group.Key.CategoryId, group.Key.Identity!, group.ToList()))
            .OrderBy(x => x.CardId).ThenBy(x => x.ExternalKey, StringComparer.Ordinal).ToList();
        return observations;
    }

    private static IEnumerable<PluggyRecurringChargeDetection> Build(Guid cardId, Guid categoryId, string identity, List<Normalized> rows)
    {
        var byCycle = rows.GroupBy(x => x.Cycle).OrderBy(x => x.Key).ToList();
        if (byCycle.Any(x => x.Count() != 1)) yield break; // várias cobranças iguais no mesmo ciclo: assinatura ambígua/compra ocasional.
        var ordered = byCycle.Select(x => x.Single().Observation).OrderBy(x => x.Date).ToList();
        if (ordered.Count < 2) yield break;
        var cycles = ordered.Select(Cycle).ToList();
        var consecutive = cycles.Zip(cycles.Skip(1)).All(x => x.Second - x.First == 1);
        var values = ordered.Select(x => x.Amount).ToList();
        var variable = values.Distinct().Count() > 1;
        var generic = GenericAggregators.Any(x => identity.Contains(x, StringComparison.Ordinal));
        if (!consecutive || generic && variable) yield break;
        var confidence = ordered.Count >= 3 ? "High" : "Candidate";
        var latest = ordered[^1];
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{cardId:N}|{categoryId:N}|{identity}")));
        yield return new(key, cardId, categoryId, latest.Description, latest.Amount, variable, ordered.Count, confidence,
            ordered[0].Date, latest.Date, ordered.Select(x => x.SourceId).ToList());
    }

    private static string? Identity(PluggyRecurringChargeObservation x)
    {
        var description = KeyText(x.Description);
        var merchant = KeyText(x.MerchantName);
        if (description.Length < 4 && merchant.Length < 4) return null;
        var basis = merchant.Length >= 4 ? merchant + "|" + description : description;
        if (GenericAggregators.Any(g => basis.Contains(g, StringComparison.Ordinal))) basis += "|" + x.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        return basis;
    }

    private static int Cycle(PluggyRecurringChargeObservation x)
    {
        var competence = Saldo360.Domain.CreditCards.CreditCardInvoiceCalendarCalculator.ForInstallment(x.Date, 1, x.ClosingDay, x.DueDay).Competence;
        return int.Parse(competence[..4], System.Globalization.CultureInfo.InvariantCulture) * 12 + int.Parse(competence[5..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string KeyText(string? value) => PluggyFinancialMapper.Normalize(value);
}

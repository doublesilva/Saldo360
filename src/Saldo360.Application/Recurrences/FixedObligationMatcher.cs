using System.Globalization;
using System.Text;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Recurrences;

/// <summary>Movimentação bancária candidata a quitar uma obrigação mensal (apenas leitura: nunca é alterada).</summary>
public sealed record SettlementCandidate(Guid Id, Guid AccountId, CategoryType Type, string Description, decimal Amount, DateOnly Date);

/// <summary>
/// Regras de vínculo automático obrigação x movimentação. Valor igual NUNCA confirma sozinho: exige mesma conta, mesmo tipo, descrição compatível,
/// data dentro da janela da competência e valor esperado (exato; ou aproximado, se a obrigação é estimada). Só pares ÚNICOS em ambos os sentidos
/// são vinculados; múltiplas ou insuficientes ficam "a revisar" para decisão manual. Pagamento parcial (valor fixo diferente) nunca quita.
/// </summary>
public static class FixedObligationMatcher
{
    public const decimal EstimateTolerance = 0.5m;
    public const int DaysBefore = 10;
    public const int DaysAfter = 20;
    private static readonly HashSet<string> Stop = ["PIX", "TED", "DOC", "PAGTO", "PAGAMENTO", "DEBITO", "COMPRA", "DE", "DA", "DO", "DOS", "DAS", "EM", "NO", "NA", "PARA", "COM", "REF"];

    public static bool Matches(FixedObligation o, SettlementCandidate t)
    {
        if (t.AccountId != o.AccountId || t.Type != o.Type || t.Amount <= 0m) return false;
        if (t.Date < o.DueDate.AddDays(-DaysBefore) || t.Date > o.DueDate.AddDays(DaysAfter)) return false;
        if (!DescriptionCompatible(o.Description, t.Description)) return false;
        return o.IsEstimated
            ? o.PlannedAmount > 0m && Math.Abs(t.Amount - o.PlannedAmount) <= o.PlannedAmount * EstimateTolerance
            : Math.Abs(t.Amount - o.PlannedAmount) < 0.005m;
    }

    public static bool DescriptionCompatible(string obligation, string transaction)
    {
        var wanted = Tokens(StripCompetenceSuffix(obligation));
        if (wanted.Count == 0) return false;
        var haystack = Normalize(transaction);
        var hits = wanted.Count(w => haystack.Contains(w, StringComparison.Ordinal));
        return hits >= 1 && hits / (decimal)wanted.Count >= 0.6m;
    }

    /// <summary>Pares com correspondência única nos dois sentidos (uma movimentação quita no máximo uma obrigação; uma obrigação, no máximo um pagamento).</summary>
    public static IReadOnlyList<(FixedObligation Obligation, SettlementCandidate Transaction)> UniquePairs(IReadOnlyList<FixedObligation> obligations, IReadOnlyList<SettlementCandidate> transactions)
    {
        var edges = obligations.SelectMany(o => transactions.Where(t => Matches(o, t)).Select(t => (o, t))).ToList();
        return edges.Where(e => edges.Count(x => ReferenceEquals(x.o, e.o)) == 1 && edges.Count(x => x.t.Id == e.t.Id) == 1).ToList();
    }

    /// <summary>Remove o sufixo administrativo de competência ("DMAE — outubro/2026", "CEEE - 10/2026"), que nunca consta na descrição bancária.</summary>
    private static string StripCompetenceSuffix(string text)
    {
        var plain = new string(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return CompetenceSuffix.Replace(plain, "");
    }

    private static readonly System.Text.RegularExpressions.Regex CompetenceSuffix = new(
        @"[\s\-–—:|]*\b(?:jan(?:eiro)?|fev(?:ereiro)?|mar(?:co)?|abr(?:il)?|mai(?:o)?|jun(?:ho)?|jul(?:ho)?|ago(?:sto)?|set(?:embro)?|out(?:ubro)?|nov(?:embro)?|dez(?:embro)?|\d{1,2})\s*[/\-]\s*\d{2,4}\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static List<string> Tokens(string text) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && !Stop.Contains(w) && !w.All(char.IsDigit)).Distinct().ToList();

    private static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

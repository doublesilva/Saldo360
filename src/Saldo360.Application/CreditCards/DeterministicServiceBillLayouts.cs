using System.Globalization;
using System.Text.RegularExpressions;
using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards;

internal static class DeterministicServiceBillLayouts
{
    private const string Month = @"(?<![\d/])\d{2}/\d{4}(?![\d/])";
    private const string Date = @"(?<!\d)\d{2}/\d{2}/\d{4}(?!\d)";
    private const string Amount = @"(?<![\d.,-])(?:\d{1,3}(?:\.\d{3})+|\d+),\d{2}(?!\d)";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static ServiceBillExtractionResult? TryExtract(ServiceBillExtractionRequest request)
    {
        var pages = request.Document.Pages;
        var text = string.Join("\n", pages.Select(p => p.Text));
        var rows = pages.SelectMany(Rows).ToList();
        var dmae = request.DocumentType == "WaterBill" &&
            Regex.IsMatch(text, @"DMAE\s*-?\s*Departamento\s*Municipal\s*de\s*[ÁA]gua\s*(?:e\s*)?Esgotos", Options);
        var electricity = request.DocumentType == "ElectricityBill" && rows.Any(IsElectricityHeader);
        if (!dmae && !electricity) return null;

        var months = new List<string>(); var dates = new List<string>(); var amounts = new List<string>();
        string? issuer = dmae ? "DMAE" : Regex.IsMatch(text, @"\b(?:CEEE(?:-D)?|Equatorial)\b", Options) ? "Equatorial" : null;
        foreach (var page in pages)
        {
            var lines = Rows(page).ToList();
            if (electricity)
            {
                for (var i = 0; i + 1 < lines.Count; i++)
                {
                    if (!IsElectricityHeader(lines[i])) continue;
                    // Observed remittance table: all three values belong to the next row, not tax/consumption rows.
                    months.AddRange(Values(lines[i + 1], Month));
                    dates.AddRange(Values(lines[i + 1], Date));
                    amounts.AddRange(Values(lines[i + 1], Amount));
                }
            }
            else
            {
                if (page.Words is { Count: > 0 } words)
                {
                    months.AddRange(Below(words, @"^FATURA$", Month));
                    dates.AddRange(AdjacentDate(words));
                    foreach (var anchor in words.Where(w => Regex.IsMatch(w.Text, @"^(TOTAL|VALOR)$", Options)))
                    {
                        var line = string.Join(" ", words.Where(w => Math.Abs(w.Top - anchor.Top) <= 3 && w.X >= anchor.X).OrderBy(w => w.X).Select(w => w.Text));
                        if (Regex.IsMatch(line, @"^(TOTAL|VALOR)\s+R\$\s+", Options)) amounts.AddRange(Values(line, Amount));
                    }
                }
                else
                {
                    months.AddRange(Captures(string.Join("\n", lines), @"\bFATURA\s*[:\-]?\s*(" + Month + ")"));
                    dates.AddRange(Captures(string.Join("\n", lines), @"\bVENCIMENTO\s*[:\-]?\s*(" + Date + ")"));
                    amounts.AddRange(Captures(string.Join("\n", lines), @"\b(?:TOTAL|VALOR)\s+R\$\s*(" + Amount + ")"));
                }
            }
        }

        string? failure = null;
        string? Resolve(IEnumerable<string> values, Func<string, string?> normalize, string field)
        {
            var original = values.ToList();
            var normalized = original.Select(normalize).ToList();
            var distinct = normalized.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count > 1) { failure ??= "Ambiguous" + field; return null; }
            if (normalized.Any(v => v is null)) { failure ??= "Invalid" + field; return null; }
            if (distinct.Count == 0) { failure ??= field + "NotFound"; return null; }
            return distinct[0];
        }
        var competence = Resolve(months, s => DateOnly.TryParseExact("01/" + s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM", CultureInfo.InvariantCulture) : null, "Competence");
        var due = Resolve(dates, s => DateOnly.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null, "DueDate");
        var amount = Resolve(amounts, s => decimal.TryParse(s.Replace(".", "").Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value >= 0 && value < Money.Limit ? Money.Format(value) : null, "TotalAmount");
        if (issuer is null) failure ??= "IssuerNotFound";
        return new(request.DocumentType, issuer, competence, due, amount, null, null,
            failure is null ? "High" : "Medium", "Deterministic", failure);
    }

    private static bool IsElectricityHeader(string line) => Regex.IsMatch(line,
        @"Compet[eê]ncia:\s+Vencimento:\s+Valor\s+cobrado\s*\(R\$\):", Options);

    private static IEnumerable<string> Rows(CreditCardPdfExtractedPage page) => page.Words is { Count: > 0 }
        ? page.Words.GroupBy(w => Math.Round(w.Top / 3)).OrderBy(g => g.Key).Select(g => string.Join(" ", g.OrderBy(w => w.X).Select(w => w.Text)))
        : page.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());

    private static IEnumerable<string> Values(string text, string pattern) => Regex.Matches(text, pattern, Options, TimeSpan.FromSeconds(1)).Select(m => m.Value);
    private static IEnumerable<string> Captures(string text, string pattern) => Regex.Matches(text, pattern, Options, TimeSpan.FromSeconds(1)).Select(m => m.Groups[1].Value);
    private static IEnumerable<string> Below(IReadOnlyList<CreditCardPdfExtractedWord> words, string label, string pattern) =>
        words.Where(w => Regex.IsMatch(w.Text, label, Options)).SelectMany(anchor => words.Where(w =>
            w.Top > anchor.Top + 1 && w.Top <= anchor.Top + 18 && Math.Abs(w.X - anchor.X) <= 5)
            .SelectMany(w => Values(w.Text, pattern)));

    private static IEnumerable<string> AdjacentDate(IReadOnlyList<CreditCardPdfExtractedWord> words) =>
        words.Where(w => Regex.IsMatch(w.Text, @"^VENCIMENTO:?$", Options)).SelectMany(anchor => words.Where(w =>
            (Math.Abs(w.Top - anchor.Top) <= 3 && w.X >= anchor.Right && w.X <= anchor.Right + 100) ||
            (w.Top > anchor.Top + 1 && w.Top <= anchor.Top + 18 && Math.Abs(w.X - anchor.X) <= 5))
            .SelectMany(w => Values(w.Text, Date)));
}

using System.Text.RegularExpressions;

namespace Saldo360.Application.CreditCards;

public sealed record StatementVisualRegionCoverage(int Page, int Region, double Left, double Top, double Right, double Bottom,
    string Status, string Classification, bool Complete);

// Geometry and scope accounting only. Never extracts monetary facts, dates, purchases or installments.
public static partial class StatementVisualRegionText
{
    public static IReadOnlyList<IReadOnlyList<CreditCardPdfExtractedWord>> Lines(IReadOnlyList<CreditCardPdfExtractedWord> words)
    {
        var lines = new List<List<CreditCardPdfExtractedWord>>();
        foreach (var word in words.OrderBy(w => w.Top).ThenBy(w => w.X))
        {
            if (lines.Count == 0 || Math.Abs(lines[^1][0].Top - word.Top) > 3) lines.Add([]);
            lines[^1].Add(word);
        }
        return lines.Select(l => (IReadOnlyList<CreditCardPdfExtractedWord>)l.OrderBy(w => w.X).ToArray()).ToArray();
    }

    // Compare each physical OCR line with its native representation, not a document-wide text hash.
    // An equal line at another position remains a separate occurrence. Conflicts never replace native text.
    public static CreditCardPdfExtractedPage Merge(CreditCardPdfExtractedPage page)
    {
        var native = page.Words ?? [];
        var combined = native.ToList(); var appended = new List<string>(); var regions = new List<StatementVisualRegion>();
        foreach (var region in page.VisualRegions)
        {
            if (region.Status != "Readable" || region.Words.Count == 0) { regions.Add(region with { Status = region.Status == "Readable" ? "Unreadable" : region.Status }); continue; }
            var additions = new List<CreditCardPdfExtractedWord>(); var text = new List<string>(); var conflict = false;
            foreach (var line in Lines(region.Words))
            {
                var top = line.Min(w => w.Top); var bottom = line.Max(w => w.Bottom);
                var left = line.Min(w => w.X); var right = line.Max(w => w.Right);
                var overlapping = combined.Where(w => w.Top < bottom && w.Bottom > top && w.X < right && w.Right > left).OrderBy(w => w.X).ToArray();
                var value = string.Join(' ', line.Select(w => w.Text));
                if (overlapping.Length > 0)
                {
                    if (Comparable(value) != Comparable(string.Join(' ', overlapping.Select(w => w.Text)))) conflict = true;
                }
                else { additions.AddRange(line); text.Add(value); }
            }
            if (conflict) regions.Add(region with { Status = "NativeConflict" });
            else
            {
                regions.Add(region with { Status = additions.Count == 0 ? "NativeOverlay" : "Readable" });
                combined.AddRange(additions); appended.AddRange(text);
            }
        }
        return page with { Words = combined, NativeWords = native, Text = string.Join('\n', new[] { page.Text }.Concat(appended)), VisualRegions = regions,
            HasUntranscribedImageRegion = regions.Any(r => r.Status is not ("Readable" or "NativeOverlay")) };
    }

    private static string Comparable(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    public static StatementVisualRegionCoverage Classify(StatementVisualRegion region, IReadOnlyList<CreditCardPdfExtractedWord>? nativeWords = null)
    {
        var lines = Lines(region.Words).Select(l => string.Join(' ', l.Select(w => w.Text))).ToArray();
        var text = string.Join('\n', lines);
        var firstHeader = nativeWords is null ? double.NegativeInfinity : Lines(nativeWords)
            .Where(line => HeaderAnchor().IsMatch(string.Join(' ', line.Select(w => w.Text))))
            .Select(line => line.Min(w => w.Top)).DefaultIfEmpty(double.NegativeInfinity).Min();
        var nativeRegionText = nativeWords is null ? string.Empty : string.Join(' ', nativeWords
            .Where(w => w.X < region.Right && w.Right > region.Left && w.Top < region.Bottom && w.Bottom > region.Top).Select(w => w.Text));
        // Scope classification cannot waive a discrepancy involving a native monetary fact/table.
        var nonFinancialConflict = region.Status != "NativeConflict" || nativeWords is not null
            && !MoneyShape().IsMatch(nativeRegionText) && !PurchaseHeading().IsMatch(nativeRegionText);
        string classification; bool complete;
        if (region.Status == "NativeOverlay") { classification = "NativeOverlay"; complete = true; }
        else if (region.Status is "Readable" or "NativeConflict" && nonFinancialConflict && !text.Any(char.IsDigit) && text.Count(char.IsLetter) >= 3
            && text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 5
            && region.Bottom < firstHeader)
        { classification = "ExcludedHeaderLabel"; complete = true; }
        else if (region.Status is "Readable" or "NativeConflict" && nonFinancialConflict && ContactAnchor().Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct().Count() >= 2
            && !MoneyShape().IsMatch(text) && !lines.Any(l => DatedMoneyRow().IsMatch(l)))
        { classification = "ExcludedContactInformation"; complete = true; }
        else if (region.Status != "Readable" || lines.Length == 0) { classification = "Inconclusive"; complete = false; }
        else if (lines.Any(l => (DatedMoneyRow().IsMatch(l) || InstallmentMoneyRow().IsMatch(l)) && !ExcludedDatedRow().IsMatch(l)) || PurchaseHeading().IsMatch(text))
        { classification = "PurchaseRows"; complete = true; } // Still requires source-row/evidence matching.
        else if (lines.All(l => Summary().IsMatch(l))) { classification = "ExcludedSummary"; complete = true; }
        else if (LegalAnchor().Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct().Count() >= 2
            && text.Count(char.IsLetter) >= 100 && lines.All(l => !MoneyShape().IsMatch(l) || l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 8 || Summary().IsMatch(l)))
        { classification = "ExcludedLegal"; complete = true; }
        else if (PaymentHeading().IsMatch(text) && lines.All(l => !MoneyShape().IsMatch(l) || ExcludedDatedRow().IsMatch(l) || Summary().IsMatch(l)))
        { classification = "ExcludedPaymentInstructions"; complete = true; }
        else if (region.Bottom - region.Top > 2 * (region.Right - region.Left) && !text.Any(char.IsDigit)
            && text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 5 && text.Count(char.IsLetter) >= 3)
        { classification = "ExcludedVerticalLabel"; complete = true; }
        else { classification = "Inconclusive"; complete = false; }
        return new(region.Page, region.Index, region.Left, region.Top, region.Right, region.Bottom, region.Status, classification, complete);
    }

    [GeneratedRegex(@"^\s*(?:\d{2}/\d{2}(?:/\d{4})?|\d{4}-\d{2}-\d{2})\s+.+\d[.,]\d{2}(?!\d)")]
    private static partial Regex DatedMoneyRow();
    [GeneratedRegex(@"(?<!\d)\d{1,2}/\d{1,2}\s+(?:\d{1,3}(?:\.\d{3})*|\d+)[.,]\d{2}\s*$")]
    private static partial Regex InstallmentMoneyRow();
    [GeneratedRegex(@"^\s*(?:\d{2}/\d{2}(?:/\d{4})?\s+)?(?:pagamento|cr[eé]dito|estorno|IOF|tarifa|juros|multa|anuidade)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExcludedDatedRow();
    [GeneratedRegex(@"^\s*(?:compras\s+(?:parceladas(?:\s*-\s*pr[oó]ximas\s+faturas)?|correntes|futuras)|lan[cç]amentos\s*:?\s*(?:de\s+)?compras(?:\s+e\s+saques)?|parcelas\s+futuras)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PurchaseHeading();
    [GeneratedRegex(@"pagamento\s+(?:da|desta|de sua)\s+fatura|cr[eé]dito\s+rotativo|encargos|juros|contrato|condi[cç][oõ]es|opera[cç][oõ]es|financiamento|tributos|IOF|inadimpl[eê]ncia", RegexOptions.IgnoreCase)]
    private static partial Regex LegalAnchor();
    [GeneratedRegex(@"instru[cç][oõ]es\s+(?:de|para)\s+pagamento|local\s+de\s+pagamento|boletos?|recibo\s+do\s+pagador", RegexOptions.IgnoreCase)]
    private static partial Regex PaymentHeading();
    [GeneratedRegex(@"\bSAC\b|ouvidoria|deficiente\s+auditivo|central\s+de\s+atendimento", RegexOptions.IgnoreCase)]
    private static partial Regex ContactAnchor();
    [GeneratedRegex(@"^(?:vencimento\b|total\s+(?:da|desta)\s+fatura\b|resumo\s+da\s+fatura\b|lan[cç]amentos\s*:?\s*(?:de\s+)?compras\b)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderAnchor();
    [GeneratedRegex(@"^\s*(?:[PLⓅⓁ]\s*)?(?:total|subtotal|pr[oó]xima fatura|demais faturas|limite|saldo)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Summary();
    [GeneratedRegex(@"(?<!\d)(?:\d{1,3}(?:\.\d{3})*|\d+)[.,]\d{2}(?!\d)")]
    private static partial Regex MoneyShape();
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Saldo360.Application.CreditCards;

public sealed record StatementPurchaseDateResolution(string? Date, string Code);

// Validates the agent's date against source tokens and explicit documentary bounds. Never patches items.
public static partial class StatementPurchaseDateEvidence
{
    public static StatementPurchaseDateResolution Resolve(string token, CreditCardPdfExtractedDocument source, bool installment)
    {
        if (string.IsNullOrWhiteSpace(token)) return new(null, "PurchaseDateAbsent");
        if (token.Length > 5)
            return DateOnly.TryParseExact(token, ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var explicitDate)
                ? new(explicitDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "PurchaseDateExplicit") : new(null, "PurchaseDateInvalid");
        if (!DateOnly.TryParseExact(token + "/2000", "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayMonth))
            return new(null, "PurchaseDateInvalid");
        var windows = new List<(DateOnly Start, DateOnly End)>();
        foreach (var page in source.Pages)
        foreach (Match period in Period().Matches(page.Text))
        {
            // A billing cycle never dates the original purchase of an intermediate installment.
            if (installment && period.Groups["billing"].Success) continue;
            if (!DateOnly.TryParseExact(period.Groups["start"].Value, ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !DateOnly.TryParseExact(period.Groups["end"].Value, ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                || start > end) continue;
            windows.Add((start, end));
        }
        if (windows.Count == 0) return new(null, "PurchaseDateYearUnproven");
        var minimum = windows.Max(w => w.Start); var maximum = windows.Min(w => w.End);
        if (minimum > maximum) return new(null, "PurchaseDatePeriodConflict");
        var candidates = new HashSet<DateOnly>();
        for (var year = minimum.Year; year <= maximum.Year; year++)
        {
            if (dayMonth.Day > DateTime.DaysInMonth(year, dayMonth.Month)) continue;
            var candidate = new DateOnly(year, dayMonth.Month, dayMonth.Day);
            if (candidate >= minimum && candidate <= maximum) candidates.Add(candidate);
        }
        return candidates.Count switch
        {
            0 => new(null, "PurchaseDateOutsideDocumentPeriod"),
            1 => new(candidates.Single().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "PurchaseDateDocumentPeriod"),
            _ => new(null, "PurchaseDateYearAmbiguous")
        };
    }
    [GeneratedRegex(@"(?:(?:per[ií]odo (?:(?:das?|de) )?compras|compras realizadas|datas das compras)\s*(?::|de|entre)?|(?<billing>per[ií]odo (?:da )?fatura)\s*(?::|de|entre)?)\s*(?<start>\d{2}/\d{2}/\d{4}|\d{4}-\d{2}-\d{2})\s*(?:a|à|at[eé]|e|[-–])\s*(?<end>\d{2}/\d{2}/\d{4}|\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex Period();
}

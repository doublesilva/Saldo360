using System.Text.RegularExpressions;

namespace Saldo360.Application.CreditCards;

public static partial class StatementFinancialClassifier
{
    // An explicit, conservative semantic rule, not value/name deduplication or a balancing algorithm.
    // The invoice total is deliberately NOT an input to association. A summary can represent several
    // legitimate equal charges: retain every detail and associate the aggregate with the whole set.
    private static IReadOnlyList<StatementFinancialAssociation> AssociateSummaries(StatementFinancialDocument document,
        CreditCardPdfExtractedDocument source, List<StatementEvidenceDiagnostic> issues)
    {
        var summaries = document.Components.Select((c, index) => (Component: c, Index: index))
            .Where(v => EligibleTax(v.Component) && InternationalIofSummary().IsMatch(v.Component.Evidence)
                && AnchoredSection(v.Component, source, SummarySection())).ToArray();
        if (summaries.Length == 0) return [];
        var details = document.Components.Select((c, index) => (Component: c, Index: index))
            .Where(v => v.Component.Scope == "CurrentInvoice" && IofLabel().IsMatch(v.Component.Evidence)
                && !summaries.Any(s => s.Index == v.Index)).ToArray();
        // Do not pick a subset whose amount happens to close the invoice/summary. Every same-resource
        // detailed IOF must be accounted for, and every physical citation must be unique.
        if (summaries.Length != 1 || details.Length == 0 || details.Any(v => !EligibleTax(v.Component)
                || !AnchoredSection(v.Component, source, TransactionSection()) || !InternationalPurchaseTax(v.Component, document, source))
            || details.Select(v => (v.Component.Page, v.Component.Evidence)).Distinct().Count() != details.Length
            || details.Sum(v => Parse(v.Component.Amount)) != Parse(summaries[0].Component.Amount))
        {
            foreach (var s in summaries) issues.Add(new("FinancialSummaryAssociationUnproven", "$.components[]", s.Component.Page, s.Index));
            return [];
        }
        return [new(summaries[0].Index, details.Select(v => v.Index).ToArray(), "InternationalIofSummaryOfDetails")];
    }

    private static bool EligibleTax(StatementFinancialComponent c) => c.Kind == "Tax" && c.Scope == "CurrentInvoice"
        && c.Unit == "BRL" && c.Nature == "Debit" && c.Confidence == "High" && c.InstallmentNumber is null
        && !OfferShape().IsMatch(c.ContextEvidence + " " + c.Evidence) && HasExactAmount(c.Evidence, c.Amount);

    private static bool AnchoredSection(StatementFinancialComponent c, CreditCardPdfExtractedDocument source, Regex heading)
    {
        var page = source.Pages.FirstOrDefault(p => p.Number == c.Page);
        if (page is null || c.ContextPage != c.Page || !heading.IsMatch(c.ContextEvidence.Trim()) || !VerifiedNativeQuote(page, c.Evidence)
            || Occurrences(page.Text, c.ContextEvidence) != 1) return false;
        var start = page.Text.IndexOf(c.ContextEvidence, StringComparison.OrdinalIgnoreCase) + c.ContextEvidence.Length;
        var at = page.Text.IndexOf(c.Evidence, StringComparison.OrdinalIgnoreCase);
        // A heading elsewhere on the page cannot authorize another table/offer/payment block.
        return start <= at && !SectionBoundary().IsMatch(page.Text[start..at]);
    }

    private static bool InternationalPurchaseTax(StatementFinancialComponent c, StatementFinancialDocument document, CreditCardPdfExtractedDocument source)
    {
        var match = DetailedIof().Match(c.Evidence);
        if (!match.Success) return false;
        var page = source.Pages.Single(p => p.Number == c.Page);
        var taxAt = page.Text.IndexOf(c.Evidence, StringComparison.OrdinalIgnoreCase);
        var target = StatementCategorization.Normalize(match.Groups["target"].Value);
        var date = match.Groups["date"].Value;
        var purchases = document.Purchases.Items.Where(p => p.Page == c.Page && p.IncludedInCurrentTotal && p.Section == "CurrentInvoice"
            && p.Type is "Purchase" or "Installment" && p.Nature == "Debit" && p.Confidence == "High"
            && StatementCategorization.Normalize(p.OriginalDescription) == target && VerifiedNativeQuote(page, p.Evidence)
            && p.Evidence.StartsWith(date + " ", StringComparison.OrdinalIgnoreCase)).ToArray();
        return purchases.Count(p =>
        {
            var end = page.Text.IndexOf(p.Evidence, StringComparison.OrdinalIgnoreCase) + p.Evidence.Length;
            if (end > taxAt || taxAt - end > 400) return false;
            var between = page.Text[end..taxAt];
            return ForeignConversion().IsMatch(between) && !DatedLine().IsMatch(between) && !SectionBoundary().IsMatch(between);
        }) == 1;
    }

    private static bool VerifiedNativeQuote(CreditCardPdfExtractedPage page, string quote)
    {
        if (page.TextSource != "Native" || page.ProcessingStatus is "Unavailable" or "Unreadable" or "ImageLimitExceeded"
            || Occurrences(page.Text, quote) != 1) return false;
        var words = page.NativeWords ?? page.Words;
        if (words is null) return page.VisualRegions.Count == 0;
        // Verify provenance as well as text; conflicting raster/native facts cannot establish a link.
        var spans = new List<(CreditCardPdfExtractedWord Word, int Start, int End)>(); var joined = "";
        foreach (var word in words)
        {
            var start = joined.Length; joined += StatementCategorization.Normalize(word.Text);
            spans.Add((word, start, joined.Length));
        }
        var needle = StatementCategorization.Normalize(quote); var at = joined.IndexOf(needle, StringComparison.Ordinal);
        if (needle.Length == 0 || at < 0) return false;
        var bounds = spans.Where(s => s.Start < at + needle.Length && s.End > at).Select(s => s.Word);
        return !page.VisualRegions.Where(r => r.Status == "NativeConflict").Any(r => bounds.Any(w =>
            w.X < r.Right && w.Right > r.Left && w.Top < r.Bottom && w.Bottom > r.Top));
    }

    private static bool HasExactAmount(string quote, string amount)
    {
        var value = Parse(amount);
        return new[] { amount, value.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")), value.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) }
            .Any(v => Regex.IsMatch(quote, $@"(?<![\d.,]){Regex.Escape(v)}(?![\d.,])"));
    }
    private static bool InformationalPosting(StatementFinancialComponent c) => InformationalRow().IsMatch(c.Evidence);

    [GeneratedRegex(@"\bIOF\b", RegexOptions.IgnoreCase)]
    private static partial Regex IofLabel();
    [GeneratedRegex(@"^\s*IOF\s+(?:de|sobre)\s+compras\s+internacionais\s+(?:R\$\s*)?(?:\d{1,3}(?:\.\d{3})*|\d+)[.,]\d{2}\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex InternationalIofSummary();
    [GeneratedRegex("^(?<date>\\d{2}(?:/\\d{2}| [A-ZÇ]{3}))\\s+IOF\\s+de\\s+[\"“](?<target>[^\"”]+)[\"”]\\s+(?:R\\$\\s*)?[\\d.,]+$", RegexOptions.IgnoreCase)]
    private static partial Regex DetailedIof();
    [GeneratedRegex(@"^resumo\s+da\s+fatura(?:\s+atual)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SummarySection();
    [GeneratedRegex(@"^(?:transa[cç][oõ]es(?:\s+de\s+.+)?|lan[cç]amentos\s*:?\s*(?:compras\s+e\s+saques|internacionais))$", RegexOptions.IgnoreCase)]
    private static partial Regex TransactionSection();
    [GeneratedRegex(@"^\s*(?:pr[oó]ximas\s+faturas|limites\b|ofertas\b|simula[çc][aã]o\b|pagamentos\b|resumo\s+da\s+fatura|transa[cç][oõ]es\b|lan[cç]amentos\s*:)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SectionBoundary();
    [GeneratedRegex(@"(?:BRL\s+[\d.,]+\s*=\s*(?:USD|EUR|GBP|JPY|CAD|AUD|CHF)\s+[\d.,]+|(?:USD|EUR|GBP|JPY|CAD|AUD|CHF)\s+[\d.,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ForeignConversion();
    [GeneratedRegex(@"^\s*\d{2}(?:/\d{2}|\s+[A-ZÇ]{3})\s+", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DatedLine();
    [GeneratedRegex(@"^\s*(?:[PLⓅⓁ]\s*)?(?:total\b|subtotal\b|saldo\b|limite\b|pagamento\s+m[ií]nimo\b|principal\b)", RegexOptions.IgnoreCase)]
    private static partial Regex InformationalRow();
}

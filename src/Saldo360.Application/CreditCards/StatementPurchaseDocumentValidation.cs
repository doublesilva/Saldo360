using System.Globalization;
using System.Text.RegularExpressions;

namespace Saldo360.Application.CreditCards;

public sealed record StatementPurchaseDocumentDiagnostic(string Code, string Path, int? Page = null, int? ItemIndex = null, bool Contradiction = false);
public sealed record StatementPurchasePageCoverage(int Page, int EligibleRows, int MatchedRows, int UnclassifiedRows, bool Complete);
public sealed record StatementPurchaseCoverageIssue(int Page, int Column, int Line, double Top, string Code);
public sealed record StatementAuthorizedCardIdentity(string Issuer, string? LastFourDigits);
public sealed record StatementDocumentIdentityValidation(string Status, bool AuthorizedIdentityConfirmed);
public sealed record StatementPurchaseDocumentValidation(bool CoverageComplete, bool DatesComplete,
    StatementDocumentIdentityValidation Identity, IReadOnlyList<StatementPurchasePageCoverage> Pages,
    IReadOnlyList<StatementPurchaseDocumentDiagnostic> Diagnostics)
{
    public bool HasContradiction => Diagnostics.Any(d => d.Contradiction);
    public IReadOnlyList<StatementPurchaseCoverageIssue> CoverageIssues { get; init; } = [];
    public IReadOnlyList<StatementVisualRegionCoverage> VisualRegions { get; init; } = [];
    public IReadOnlyList<StatementPurchaseCoverageIssue> ScopeExclusions { get; init; } = [];
    public bool UsesOcrPurchaseEvidence { get; init; }
}

// Source inventory and evidence accounting, NOT a financial parser. No amounts, purchase entities,
// schedules or corrected items are produced. Row text and native coordinates are transient only.
public static partial class StatementPurchaseDocumentValidator
{
    private sealed record Row(int Page, string Section, string Text, bool Ocr, StatementPurchaseCoverageIssue Location);
    private sealed record SourcePage(int Page, List<Row> Rows, List<StatementPurchaseCoverageIssue> Issues,
        List<StatementPurchaseCoverageIssue> Exclusions, bool Readable);
    private sealed record Line(string Text, double X = 0, double Top = 0);

    public static StatementPurchaseDocumentValidation Validate(CreditCardPdfExtractedDocument source,
        CreditCardStatementAgentPreview preview, StatementAuthorizedCardIdentity? authorizedIdentity = null)
    {
        var pages = Inventory(source);
        var diagnostics = new List<StatementPurchaseDocumentDiagnostic>();
        var visualRegions = source.Pages.SelectMany(p => p.VisualRegions.Select(r => StatementVisualRegionText.Classify(r, p.NativeWords))).ToArray();
        foreach (var region in visualRegions.Where(r => !r.Complete))
            diagnostics.Add(new("VisualRegion" + (region.Status == "NativeConflict" ? "NativeConflict" : "Inconclusive"), "$.pages[].visualRegions[]", region.Page));
        var matches = new Dictionary<int, Row>();
        var used = new HashSet<Row>(ReferenceEqualityComparer.Instance);
        foreach (var (item, index) in preview.Items.Select((item, index) => (item, index)))
        {
            var row = pages.SelectMany(p => p.Rows).FirstOrDefault(row => !used.Contains(row) && row.Page == item.Page
                && row.Section == item.Section && !item.Evidence.StartsWith("VISUAL:", StringComparison.Ordinal)
                && row.Text.Contains(item.Evidence, StringComparison.OrdinalIgnoreCase));
            if (row is null) diagnostics.Add(new("PurchaseItemOutsideEligibleRows", "$.items[].evidence", item.Page, index, true));
            else { matches[index] = row; used.Add(row); }
        }
        var coverage = pages.Select(p => new StatementPurchasePageCoverage(p.Page, p.Rows.Count, p.Rows.Count(used.Contains), p.Issues.Count,
            p.Readable && p.Issues.Count == 0 && p.Rows.All(used.Contains))).ToArray();
        foreach (var page in coverage.Where(p => !p.Complete))
            diagnostics.Add(new(page.UnclassifiedRows > 0 ? "PurchaseRowsUnclassified" : "PurchaseCoverageIncomplete", "$.pages[]", page.Page));
        var datesComplete = true;
        for (var index = 0; index < preview.Items.Count; index++)
        {
            var item = preview.Items[index];
            var token = matches.TryGetValue(index, out var row) ? RowDate().Match(row.Text).Value.Trim() : string.Empty;
            var date = StatementPurchaseDateEvidence.Resolve(token, source, item.Type == "Installment");
            datesComplete &= date.Date is not null && item.TransactionDate == date.Date;
            if (date.Date is { } expected)
            {
                if (item.TransactionDate != expected)
                    diagnostics.Add(new(item.TransactionDate is null ? "PurchaseDateMissing" : "PurchaseDateMismatch", "$.items[].transactionDate", item.Page, index, true));
            }
            else
            {
                diagnostics.Add(new(item.TransactionDate is not null ? "PurchaseDateUnsupportedYear" : date.Code, "$.items[].transactionDate", item.Page, index,
                    item.TransactionDate is not null || date.Code is "PurchaseDateInvalid" or "PurchaseDateOutsideDocumentPeriod" or "PurchaseDatePeriodConflict"));
            }
        }
        var identity = Identity(preview.Header, source, authorizedIdentity);
        if (identity.Status == "Contradictory") diagnostics.Add(new("DocumentIdentityContradiction", "$.header", Contradiction: true));
        return new(coverage.All(p => p.Complete) && matches.Count == preview.Items.Count, datesComplete, identity, coverage, diagnostics)
        { CoverageIssues = pages.SelectMany(p => p.Issues).ToArray(), ScopeExclusions = pages.SelectMany(p => p.Exclusions).ToArray(),
            VisualRegions = visualRegions, UsesOcrPurchaseEvidence = matches.Values.Any(r => r.Ocr) };
    }

    private static List<SourcePage> Inventory(CreditCardPdfExtractedDocument source)
    {
        var pages = new List<SourcePage>();
        var productSeries = new HashSet<string>(StringComparer.Ordinal);
        // Text-only continuation needs an explicitly established section, never the model's item types.
        string? continuedSection = null;
        foreach (var page in source.Pages.OrderBy(p => p.Number))
        {
            var visual = page.VisualRegions.Select(r => StatementVisualRegionText.Classify(r, page.NativeWords)).ToArray();
            var excluded = visual.Where(r => r.Classification.StartsWith("Excluded", StringComparison.Ordinal)).ToArray();
            var inventoryPage = page with { Words = page.Words?.Where(w => page.NativeWords?.Contains(w) == true
                || !excluded.Any(r => w.X >= r.Left - 2 && w.Right <= r.Right + 2 && w.Top >= r.Top - 2 && w.Bottom <= r.Bottom + 2)).ToArray() };
            var rows = new List<Row>(); var issues = new List<StatementPurchaseCoverageIssue>();
            var exclusions = new List<StatementPurchaseCoverageIssue>();
            var lines = Lines(inventoryPage);
            var headings = lines.Select(l => (Line: l, Section: Heading(l.Text))).Where(h => h.Section is not null).ToArray();
            var columns = headings.Select(h => h.Line.X).Order().Aggregate(new List<double>(), (all, x) =>
            { if (all.Count == 0 || x - all[^1] > 35) all.Add(x); return all; });
            if (columns.Count == 0) columns.Add(0);
            for (var column = 0; column < columns.Count; column++)
            {
                var left = columns[column] - 8; var right = column + 1 < columns.Count ? columns[column + 1] - 8 : double.PositiveInfinity;
                var section = columns.Count == 1 ? continuedSection : null;
                var region = RegionLines(inventoryPage, lines, left, right);
                // A future table may continue at the top of the next physical column. Require its
                // repeated column labels before any dated row; a nearby table/unknown region is not proof.
                if (columns.Count > 1 && continuedSection == "FutureInstallments"
                    && region.TakeWhile(l => !RowDate().IsMatch(l.Text) && Heading(l.Text) is null).Any(l => TableHeader(l.Text)))
                    section = continuedSection;
                for (var lineIndex = 0; lineIndex < region.Count; lineIndex++)
                {
                    var line = region[lineIndex];
                    if (Heading(line.Text) is { } heading) { section = heading; continue; }
                    var dated = RowDate().IsMatch(line.Text);
                    var money = MoneyShape().IsMatch(line.Text);
                    if (section == "ExcludedProducts" && dated && money && SeriesKey(line.Text) is { } key) productSeries.Add(key);
                    var exclusion = section is "Excluded" or "ExcludedProducts" ? "NonPurchaseSection"
                        : NegativeAmountRow().IsMatch(line.Text) ? "CreditRow"
                        : ExcludedRow(line.Text) ? "NonPurchaseRow" : null;
                    if (exclusion is not null)
                    {
                        if (dated || money) exclusions.Add(new(page.Number, column, lineIndex, line.Top, exclusion));
                        continue;
                    }
                    if (!dated && !money) continue;
                    if (dated && money && section is "CurrentInvoice" or "FutureInstallments") rows.Add(new(page.Number, section, line.Text.Trim(),
                        page.NativeWords is not null && !page.NativeWords.Any(w => Math.Abs(w.Top - line.Top) < 3 && Math.Abs(w.X - line.X) < 3),
                        new(page.Number, column, lineIndex, line.Top, "SourceRow")));
                    else if (dated || section is "CurrentInvoice" or "FutureInstallments" && money)
                        issues.Add(new(page.Number, column, lineIndex, line.Top,
                            section is not ("CurrentInvoice" or "FutureInstallments") ? "PurchaseRowSectionUnknown" : dated ? "PurchaseRowAmountMissing" : "PurchaseRowDateMissing"));
                }
                // Do not carry an excluded parallel column into a later, unlabelled purchase region.
                var tail = region.LastOrDefault(l => RowDate().IsMatch(l.Text) || SummaryRow().IsMatch(l.Text));
                continuedSection = columns.Count == 1 || section == "FutureInstallments" && tail is not null && RowDate().IsMatch(tail.Text) ? section : null;
            }
            // Neither an image alone nor blank/failed OCR proves the absence of purchases.
            var readable = (!page.HasUntranscribedImageRegion || visual.Length > 0 && visual.All(r => r.Complete)) && visual.All(r => r.Complete)
                && page.ImagePng is { Length: > 0 } && !string.IsNullOrWhiteSpace(page.Text)
                && page.ProcessingStatus is not ("Unavailable" or "Unreadable" or "ImageLimitExceeded");
            pages.Add(new(page.Number, rows, issues, exclusions, readable));
        }
        // A mixed future table can repeat a product explicitly located in the non-purchase products
        // section. Match only documentary date + exact marker-independent label, never merchant names
        // or a PDF allowlist. Conflicting current purchase/product occurrences stay inconclusive.
        var purchaseSeries = pages.SelectMany(p => p.Rows).Where(r => r.Section == "CurrentInvoice")
            .Select(r => SeriesKey(r.Text)).Where(k => k is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var page in pages)
        foreach (var row in page.Rows.Where(r => r.Section == "FutureInstallments").ToArray())
        {
            if (SeriesKey(row.Text) is not { } key || !productSeries.Contains(key)) continue;
            if (purchaseSeries.Contains(key)) page.Issues.Add(row.Location with { Code = "PurchaseScopeAmbiguous" });
            else { page.Rows.Remove(row); page.Exclusions.Add(row.Location with { Code = "NonPurchaseProductContinuation" }); }
        }
        return pages;
    }

    private static IReadOnlyList<Line> Lines(CreditCardPdfExtractedPage page)
    {
        if (page.Words is not { Count: > 0 } words)
            return page.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select((t, i) => new Line(t.Trim(), 0, i)).ToArray();
        return WordLines(words, splitColumns: true);
    }

    private static IReadOnlyList<Line> RegionLines(CreditCardPdfExtractedPage page, IReadOnlyList<Line> lines, double left, double right)
        => page.Words is { Count: > 0 } words
            ? WordLines(words.Where(w => w.X >= left && w.X < right).ToArray(), splitColumns: false)
            : lines.Where(l => l.X >= left && l.X < right).ToArray();

    private static List<Line> WordLines(IReadOnlyList<CreditCardPdfExtractedWord> words, bool splitColumns)
    {
        var groups = new List<List<CreditCardPdfExtractedWord>>();
        foreach (var word in words.OrderBy(w => w.Top).ThenBy(w => w.X))
        {
            if (groups.Count == 0 || Math.Abs(groups[^1][0].Top - word.Top) > 2.5) groups.Add([]);
            groups[^1].Add(word);
        }
        var lines = new List<Line>();
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(w => w.X).ToArray(); var segment = new List<CreditCardPdfExtractedWord>();
            foreach (var word in ordered)
            {
                if (splitColumns && segment.Count > 0 && word.X - segment[^1].Right > 35)
                { lines.Add(new(string.Join(' ', segment.Select(w => w.Text)), segment[0].X, segment[0].Top)); segment.Clear(); }
                segment.Add(word);
            }
            if (segment.Count > 0) lines.Add(new(string.Join(' ', segment.Select(w => w.Text)), segment[0].X, segment[0].Top));
        }
        return lines;
    }

    private static string? Heading(string text)
    {
        var n = StatementCategorization.Normalize(text);
        if (n == "LANCAMENTOSPRODUTOSESERVICOS") return "ExcludedProducts";
        if (ExcludedHeadings.Any(h => n.StartsWith(h, StringComparison.Ordinal))) return "Excluded";
        if (MoneyShape().IsMatch(text)) return null; // A subtotal is not a new section.
        if (FutureHeadings.Any(h => n.StartsWith(h, StringComparison.Ordinal))) return "FutureInstallments";
        if (CurrentHeadings.Any(h => n.StartsWith(h, StringComparison.Ordinal))) return "CurrentInvoice";
        return null;
    }
    private static bool ExcludedRow(string text) => SummaryRow().IsMatch(text) || NonPurchaseRow().IsMatch(text) || CurrencyConversionRow().IsMatch(text) || TableHeader(text) ||
        StatementCategorization.Normalize(text) is "DATAESTABELECIMENTOVALOREMRS" or "DATADESCRICAOVALOR";
    private static bool TableHeader(string text) => TableColumns().IsMatch(text);
    private static string? SeriesKey(string text)
    {
        var match = NumberedSourceRow().Match(text);
        return match.Success && match.Groups["label"].Value.Any(char.IsLetter)
            ? match.Groups["date"].Value + "|" + string.Join(' ', match.Groups["label"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant() : null;
    }
    private static readonly string[] CurrentHeadings = ["LANCAMENTOSCOMPRASESAQUES", "LANCAMENTOSDECOMPRAS", "LANCAMENTOSINTERNACIONAIS", "COMPRASDOMES", "COMPRASNESTAFATURA", "COMPRASCORRENTES"];
    private static readonly string[] FutureHeadings = ["COMPRASPARCELADASPROXIMASFATURAS", "PARCELASFUTURAS", "COMPRASFUTURAS"];
    private static readonly string[] ExcludedHeadings = ["PAGAMENTOSEFETUADOS", "PAGAMENTOSRECEBIDOS", "PAGAMENTOS", "CREDITOSEESTORNOS", "CREDITOSEAJUSTES",
        "ENCARGOSCOBRADOS", "ENCARGOS", "JUROS", "LIMITESDECREDITO", "LIMITEDISPONIVEL", "LIMITETOTALUTILIZADO", "CREDITOROTATIVO", "SIMULACAO", "OFERTAS", "BOLETO", "RECIBODOPAGADOR", "LOCALDEPAGAMENTO", "DATADODOCUMENTO", "RESUMODAFATURA", "TARIFAS", "TAXAS"];
    [GeneratedRegex(@"^\s*(?:\d{2}/\d{2}/\d{4}|\d{4}-\d{2}-\d{2}|\d{2}/\d{2})(?![\d/])")]
    private static partial Regex RowDate();
    [GeneratedRegex(@"(?<!\d)(?:\d{1,3}(?:\.\d{3})*|\d+)[.,]\d{2}(?!\d)")]
    private static partial Regex MoneyShape();
    [GeneratedRegex(@"^\s*(?:[PLⓅⓁ]\s*)?(?:total\b|subtotal\b|pr[oó]xima fatura\b|demais faturas\b|lan[cç]amentos no cart[aã]o\b|limite\b|saldo\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryRow();
    [GeneratedRegex(@"^\s*(?:\d{2}/\d{2}(?:/\d{4})?\s+)?(?:pagamento\b|cr[eé]dito\b|estorno\b|IOF\b|repasse\s+de\s+IOF\b|d[oó]lar\s+de\s+convers[aã]o\b|encargos\b|tarifa\b|juros\b|multa\b|anuidade\b|saque\b)", RegexOptions.IgnoreCase)]
    private static partial Regex NonPurchaseRow();
    [GeneratedRegex(@"(?:^|\s)-(?:\d{1,3}(?:\.\d{3})*|\d+)[.,]\d{2}\s*$")]
    private static partial Regex NegativeAmountRow();
    [GeneratedRegex(@"^\s*[\d.,]+\s+(?:USD|EUR|GBP|BRL)\s+[\d.,]+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyConversionRow();
    [GeneratedRegex(@"^\s*DATA\s+(?:ESTABELECIMENTO|DESCRI[CÇ][AÃ]O|PRODUTOS\s*/\s*SERVI[CÇ]OS)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex TableColumns();
    [GeneratedRegex(@"^\s*(?<date>\d{2}/\d{2}(?:/\d{4})?|\d{4}-\d{2}-\d{2})\s+(?<label>.+?)\s*\d{1,2}/\d{1,2}\s+(?:\d{1,3}(?:\.\d{3})*|\d+)[.,]\d{2}\s*$")]
    private static partial Regex NumberedSourceRow();

    private static StatementDocumentIdentityValidation Identity(CreditCardStatementAgentHeader header, CreditCardPdfExtractedDocument source,
        StatementAuthorizedCardIdentity? context)
    {
        if (context is not null && CreditCardStatementResolver.ContradictsSelectedContext(header, context.Issuer, context.LastFourDigits)) return new("Contradictory", false);
        if (new[] { header.Issuer, header.CardLastDigits, header.CardProduct, header.HolderName }.All(string.IsNullOrWhiteSpace)) return new("Absent", false);
        if (header.IdentityEvidence is { } evidence && header.IdentityPage is { } page)
        {
            if (evidence.StartsWith("VISUAL:", StringComparison.Ordinal)) return new("VisualUnverified", false);
            if (source.Pages.Any(p => p.Number == page && p.Text.Contains(evidence, StringComparison.OrdinalIgnoreCase))
                && new[] { header.Issuer, header.CardLastDigits, header.CardProduct, header.HolderName }.Where(v => v is not null)
                    .All(v => StatementCategorization.Normalize(evidence).Contains(StatementCategorization.Normalize(v!), StringComparison.Ordinal)))
                return new("TextCorroborated", context is not null && header.Issuer is not null && header.CardLastDigits is not null);
        }
        return new("Unverified", false);
    }
}

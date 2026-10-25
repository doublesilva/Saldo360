using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class CreditCardPdfTextExtractor(LocalStatementPageReader? pageReader = null, bool batchPages = false, bool renderAllPages = false) : ICreditCardPdfTextExtractor
{
    private readonly CreditCardPdfParserResolver _parsers = new();
    private static readonly Regex DateRegex = new(@"(?<!\d)(\d{2}/\d{2}/\d{4}|\d{4}-\d{2}-\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex AmountRegex = new(@"(?<!\w)(?:R\$\s*)?-?\d{1,3}(?:\.\d{3})*,\d{2}|(?<!\w)-?\d+\.\d{2}(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InstallmentRegex = new(@"(?<!\d)(\d{1,2})\s*/\s*(\d{1,2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex CardRegex = new(@"(?:cart[aã]o|card)[^\d]{0,20}(?:\*{2,}|x{2,}|•{2,})?\s*(\d{4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int maxPages, IReadOnlyList<string> passwords, CancellationToken cancellationToken)
    {
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            if (pdf.CanSeek) pdf.Position = 0;
            await pdf.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        try
        {
            var options = passwords.Count == 0 ? null : new ParsingOptions { Passwords = passwords.ToList() };
            using var input = new MemoryStream(bytes, writable: false);
            using var document = options is null ? PdfDocument.Open(input) : PdfDocument.Open(input, options);
            if (document.NumberOfPages > maxPages) throw new InvalidDataException("page limit");
            var pages = new List<CreditCardPdfExtractedPage>(document.NumberOfPages);
            var images = 0;
            long imageBytes = 0;
            var preparation = System.Diagnostics.Stopwatch.StartNew();
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.GetPage(pageNumber);
                var words = page.GetWords()
                    .Select(word => new CreditCardPdfExtractedWord(
                        word.Text,
                        word.BoundingBox.Left,
                        page.Height - word.BoundingBox.Top,
                        word.BoundingBox.Right,
                        page.Height - word.BoundingBox.Bottom))
                    .ToList();
                var extracted = new CreditCardPdfExtractedPage(pageNumber, renderAllPages ? ReflowNativeWords(words, page.Text) : page.Text, words);
                var needsOcr = !LocalStatementPageReader.Usable(page.Text);
                // A large raster region plus a native footer must not prove the table was transcribed.
                // Conservative opt-in metadata only; it does not inspect pixels or interpret transactions.
                if (renderAllPages && !needsOcr && page.GetImages().Any(image => UntranscribedRasterRegion(words,
                    image.Bounds.Left, page.Height - image.Bounds.Top, image.Bounds.Right, page.Height - image.Bounds.Bottom, page.Width * page.Height)))
                    extracted = extracted with { HasUntranscribedImageRegion = true };
                if (pageReader is not null && (renderAllPages || pageNumber == 1 || needsOcr))
                {
                    if (++images > (batchPages ? maxPages : 12) || preparation.Elapsed > TimeSpan.FromSeconds(90)) extracted = extracted with { ProcessingStatus = "ImageLimitExceeded" };
                    else
                    {
                        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
                        builder.AddPage(document, pageNumber); // Already unlocked in memory; no password in process arguments.
                        extracted = await pageReader.ReadAsync(builder.Build(), extracted, needsOcr, cancellationToken);
                        imageBytes += extracted.ImagePng?.Length ?? 0;
                        if (imageBytes > (batchPages ? 64L : 24L) * 1024 * 1024) extracted = extracted with { ImagePng = null, ProcessingStatus = "ImageLimitExceeded" };
                    }
                }
                pages.Add(extracted);
            }
            return new(pages);
        }
        catch (PdfDocumentEncryptedException exception)
        {
            throw new CreditCardPdfReadException(passwords.Count == 0 ? CreditCardPdfReadFailure.PasswordRequired : CreditCardPdfReadFailure.PasswordRejected, exception);
        }
        catch (NotSupportedException exception)
        {
            throw new CreditCardPdfReadException(CreditCardPdfReadFailure.UnsupportedEncryption, exception);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new CreditCardPdfReadException(CreditCardPdfReadFailure.Corrupt, exception);
        }
    }

    // Geometric text preparation only, not a transaction parser. Keep every native word; insert row/column
    // separators so a glued PdfPig page.Text does not merge X/Y with the amount in another table column.
    // Opt-in with the lab's all-page visual profile; legacy callers keep their original native text.
    internal static string ReflowNativeWords(IReadOnlyList<CreditCardPdfExtractedWord> words, string fallback)
    {
        if (words.Count == 0) return fallback;
        var lines = new List<List<CreditCardPdfExtractedWord>>();
        foreach (var word in words.OrderBy(w => w.Top).ThenBy(w => w.X))
        {
            if (lines.Count == 0 || Math.Abs(lines[^1][0].Top - word.Top) > 2.5) lines.Add([]);
            lines[^1].Add(word);
        }
        return string.Join('\n', lines.Select(line => string.Join(' ', line.OrderBy(w => w.X).Select(w => w.Text))));
    }

    internal static bool UntranscribedRasterRegion(IReadOnlyList<CreditCardPdfExtractedWord> words,
        double left, double top, double right, double bottom, double pageArea)
    {
        var area = (right - left) * (bottom - top);
        if (area <= 0 || area < pageArea * 0.15) return false;
        var overlapping = words.Where(w => w.X < right && w.Right > left && w.Top < bottom && w.Bottom > top).ToArray();
        if (overlapping.Length == 0) return true;
        var textArea = overlapping.Sum(w => Math.Max(0, Math.Min(w.Right, right) - Math.Max(w.X, left))
            * Math.Max(0, Math.Min(w.Bottom, bottom) - Math.Max(w.Top, top)));
        var span = Math.Min(bottom, overlapping.Max(w => w.Bottom)) - Math.Max(top, overlapping.Min(w => w.Top));
        // Distinguish a native/transcribed page over a raster background from a table image with only
        // a small native footer. This is geometric coverage metadata, not a claim about OCR accuracy.
        return textArea < area * 0.05 || span < (bottom - top) * 0.5;
    }

    public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat)
    {
        if (_parsers.TryParse(document, fileName, competence) is { } specialized) return specialized;

        var allText = string.Join("\n", document.Pages.Select(page => page.Text));
        var statement = new CreditCardPdfPreviewStatement(
            FindLabel(allText, "(?:emissor|institui[cç][aã]o|issuer)"),
            CardRegex.Match(allText) is { Success: true } card ? card.Groups[1].Value : null,
            competence ?? FindCompetence(allText),
            FindDateLabel(allText, "(?:fechamento|closing)"),
            FindDateLabel(allText, "(?:vencimento|due)"),
            FindAmountLabel(allText, @"(?:total(?:\s+da\s+fatura)?|valor\s+total)"));
        var items = new List<CreditCardPdfPreviewItem>();
        var row = 0;
        foreach (var page in document.Pages)
        {
            foreach (var raw in page.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length < 4 || IsHeader(line)) continue;
                var amountMatch = AmountRegex.Matches(line).LastOrDefault();
                if (amountMatch is null) continue;
                var amount = ParseAmount(amountMatch.Value);
                var dateMatch = DateRegex.Match(line);
                var installment = InstallmentRegex.Match(line);
                var description = line[..amountMatch.Index].Trim(' ', '-', ':');
                if (dateMatch.Success) description = description.Replace(dateMatch.Value, string.Empty, StringComparison.Ordinal).Trim();
                if (description.Length == 0) description = "Linha não reconhecida";
                var type = Classify(line, out var warning);
                var errors = new List<string>();
                var warnings = warning is null ? new List<string>() : [warning];
                var status = type == "Unknown" ? "Warning" : "Valid";
                if (amount is null) { status = "Invalid"; errors.Add("Valor monetário não reconhecido."); }
                items.Add(new(++row, page.Number, line, ParseDate(dateMatch.Value, dateFormat), description, amount, type,
                    installment.Success ? int.Parse(installment.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                    installment.Success ? int.Parse(installment.Groups[2].Value, CultureInfo.InvariantCulture) : null,
                    statement.Competence, status, errors, warnings));
            }
        }
        var summary = new CreditCardPdfPreviewSummary(items.Count(item => item.Status == "Valid"), items.Count(item => item.Warnings.Count > 0), items.Count(item => item.Status == "Invalid"), items.Count(item => item.Type == "Unknown"));
        var warningsGeneral = document.Pages.Count == 0 ? new List<string> { "O PDF não possui camada textual suficiente; OCR não é suportado." } : new List<string>();
        warningsGeneral.Add("Parser genérico: a composição financeira não foi validada contra o total oficial.");
        return new(fileName, document.Pages.Count, "Generic", statement, summary, items, warningsGeneral);
    }

    private static bool IsHeader(string line) => Regex.IsMatch(line, "^(data|descri[cç][aã]o|total|subtotal|pagamento|fatura|vencimento|compras?)\\b", RegexOptions.IgnoreCase);
    private static string Classify(string line, out string? warning)
    {
        warning = null;
        if (Regex.IsMatch(line, "pagamento|payment", RegexOptions.IgnoreCase)) return "Payment";
        if (Regex.IsMatch(line, "estorno|refund", RegexOptions.IgnoreCase)) { warning = "Estorno preservado para revisão; não é uma compra."; return "Refund"; }
        if (Regex.IsMatch(line, "cr[eé]dito|credit", RegexOptions.IgnoreCase)) { warning = "Crédito preservado para revisão; não é uma compra."; return "Credit"; }
        if (Regex.IsMatch(line, "juros|multa|encargo|fee", RegexOptions.IgnoreCase)) return "Fee";
        if (InstallmentRegex.IsMatch(line)) { warning = "Parcela identificada; a compra original não foi inferida."; return "Installment"; }
        var date = DateRegex.IsMatch(line);
        return date ? "Purchase" : "Unknown";
    }
    private static string? ParseDate(string value, string? format)
    {
        if (string.IsNullOrEmpty(value)) return null;
        string[] formats = format is "yyyy-MM-dd" or "dd/MM/yyyy" ? new[] { format } : new[] { "yyyy-MM-dd", "dd/MM/yyyy" };
        return DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }
    private static string? ParseAmount(string value)
    {
        var normalized = value.Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (normalized.Contains(',')) normalized = normalized.Replace(".", string.Empty).Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ? amount.ToString("0.00", CultureInfo.InvariantCulture) : null;
    }
    private static string? FindLabel(string text, string label)
    {
        var match = Regex.Match(text, $"{label}\\s*[:\\-]?\\s*(.+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim().Split('\n')[0] : null;
    }
    private static string? FindDateLabel(string text, string label) => ParseDate(Regex.Match(text, $"{label}[^\\d]*(\\d{{2}}/\\d{{2}}/\\d{{4}}|\\d{{4}}-\\d{{2}}-\\d{{2}})", RegexOptions.IgnoreCase).Groups[1].Value, null);
    private static string? FindAmountLabel(string text, string label) => ParseAmount(Regex.Match(text, $"{label}[^\\d-]*({AmountRegex})", RegexOptions.IgnoreCase).Groups[1].Value);
    private static string? FindCompetence(string text)
    {
        var value = Regex.Match(text, @"(?:compet[eê]ncia|competence)[^\d]*(\d{4}-\d{2}|\d{2}/\d{4})", RegexOptions.IgnoreCase).Groups[1].Value;
        return value.Length == 7 ? value.Contains('/') ? value[3..] + "-" + value[..2] : value : null;
    }
}

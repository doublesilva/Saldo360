using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class CreditCardPdfTextExtractor : ICreditCardPdfTextExtractor
{
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
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pages.Add(new(pageNumber, document.GetPage(pageNumber).Text));
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

    public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? competence, string? dateFormat)
    {
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

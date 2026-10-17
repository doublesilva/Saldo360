using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Common;

namespace Saldo360.Infrastructure.CreditCards;

internal static partial class NubankCreditCardPdfParser
{
    private static readonly IReadOnlyDictionary<string, int> Months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    { ["JAN"] = 1, ["FEV"] = 2, ["MAR"] = 3, ["ABR"] = 4, ["MAI"] = 5, ["JUN"] = 6, ["JUL"] = 7, ["AGO"] = 8, ["SET"] = 9, ["OUT"] = 10, ["NOV"] = 11, ["DEZ"] = 12 };

    public static bool CanParse(CreditCardPdfExtractedDocument document)
    {
        var text = string.Join('\n', document.Pages.Select(page => page.Text));
        return text.Contains("NU PAGAMENTOS S.A", StringComparison.OrdinalIgnoreCase)
            && text.Contains("RESUMO DA FATURA ATUAL", StringComparison.OrdinalIgnoreCase)
            && text.Contains("PRÓXIMAS FATURAS", StringComparison.OrdinalIgnoreCase);
    }

    public static CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? configuredCompetence)
    {
        var lines = BuildLines(document);
        var allText = string.Join("\n", lines.Select(line => line.Text));
        var dueDate = FindNamedDate(allText, @"(?:Data de vencimento:|FATURA)\s*(\d{2})\s+([A-ZÇ]{3})\s+(\d{4})");
        var closingDate = FindNamedDate(allText, @"EMISS[ÃA]O E ENVIO\s*(\d{2})\s+([A-ZÇ]{3})\s+(\d{4})");
        var competence = configuredCompetence ?? dueDate?[..7];
        var officialTotal = FindSummaryAmount(lines, 4, "Total a pagar")
            ?? FindAmount(allText, @"fatura de\s+\w+,\s+no valor de\s+R\$\s*([\d.]+,\d{2})");
        var holder = Regex.Match(allText, @"Ol[aá],\s*(.+?)\.\s*Esta [ée] a sua fatura", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        var cardDigits = CardDigitsRegex().Matches(allText).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        var warnings = new List<string>();
        if (cardDigits.Count > 1) warnings.Add("A fatura reúne transações de mais de um final de cartão; a identificação automática exige escolha explícita.");
        var statement = new CreditCardPdfPreviewStatement("Nubank", cardDigits.Count == 1 ? cardDigits[0] : null,
            competence, closingDate, dueDate, officialTotal) { HolderName = EmptyToNull(holder) };

        var items = new List<CreditCardPdfPreviewItem>();
        AddSummary(items, lines, "Fatura anterior", "PreviousBalance", "Debit", dueDate, competence);
        AddTransactions(items, lines, dueDate, competence);
        for (var index = 0; index < items.Count; index++) items[index] = items[index] with { RowNumber = index + 1 };

        var componentTotal = items.Where(item => item.IncludedInCurrentTotal && item.Amount is not null)
            .Sum(item => decimal.Parse(item.Amount!, CultureInfo.InvariantCulture));
        decimal? parsedOfficial = decimal.TryParse(officialTotal, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var total) ? total : null;
        decimal? difference = parsedOfficial is null ? null : parsedOfficial.Value - componentTotal;
        var unknown = items.Any(item => item.IncludedInCurrentTotal && item.Type == "Unknown");
        var status = parsedOfficial is null || unknown ? "NeedsReview" : difference == 0m ? "Reconciled" : "Divergent";
        if (status == "Divergent") warnings.Add("A soma dos componentes reconhecidos diverge do total oficial da fatura.");
        if (status == "NeedsReview") warnings.Add("A composição da fatura exige revisão porque há componentes desconhecidos ou o total oficial não foi identificado.");
        var reconciliation = new CreditCardPdfReconciliation(status, officialTotal, Money.Format(componentTotal), difference is null ? null : Money.Format(difference.Value), unknown);
        var summary = new CreditCardPdfPreviewSummary(items.Count(item => item.Status == "Valid"), items.Count(item => item.Warnings.Count > 0), items.Count(item => item.Status == "Invalid"), items.Count(item => item.Type == "Unknown"));
        return new(fileName, document.Pages.Count, "Nubank", statement, summary, items, warnings) { Reconciliation = reconciliation };
    }

    private static void AddSummary(List<CreditCardPdfPreviewItem> items, IReadOnlyList<LayoutLine> lines, string label, string type, string nature, string? date, string? competence)
    {
        var index = lines.FindIndex(line => line.Page == 4 && line.Text.Contains(label, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var amountLine = lines.Skip(index).Take(3).FirstOrDefault(line => line.Page == 4 && LastAmountRegex().IsMatch(line.Text));
        if (amountLine is null || LastAmountRegex().Match(amountLine.Text) is not { Success: true } amount) return;
        items.Add(CreateItem(4, $"{label} {amountLine.Text}", date, label, amount.Groups[1].Value, type, null, null, competence, nature, false));
    }

    private static string? FindSummaryAmount(IReadOnlyList<LayoutLine> lines, int page, string label)
    {
        var index = lines.FindIndex(line => line.Page == page && line.Text.Contains(label, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        var amountLine = lines.Skip(index).Take(3).FirstOrDefault(line => line.Page == page && LastAmountRegex().IsMatch(line.Text));
        return amountLine is null ? null : ParsePtBr(LastAmountRegex().Match(amountLine.Text).Groups[1].Value) is { } amount ? Money.Format(amount) : null;
    }

    private static void AddTransactions(List<CreditCardPdfPreviewItem> items, IReadOnlyList<LayoutLine> lines, string? dueDate, string? competence)
    {
        var page = lines.Where(line => line.Page == 5).ToList();
        for (var index = 0; index < page.Count; index++)
        {
            var line = page[index];
            var match = TransactionRegex().Match(line.Text);
            if (match.Success)
            {
                var description = match.Groups[4].Value.Trim();
                var installment = InstallmentRegex().Match(description);
                if (installment.Success) description = description[..installment.Index].Trim(' ', '-');
                var international = index + 1 < page.Count && page[index + 1].Text.StartsWith("BRL ", StringComparison.OrdinalIgnoreCase);
                var amountText = match.Groups[5].Value;
                var amount = ParsePtBr(amountText);
                var type = amount < 0 ? description.Contains("Pagamento", StringComparison.OrdinalIgnoreCase) ? "Payment" : "Credit"
                    : description.StartsWith("IOF de", StringComparison.OrdinalIgnoreCase) ? "Fee"
                    : installment.Success ? "Installment" : international ? "InternationalPurchase" : "Purchase";
                var nature = amount < 0 ? "Credit" : "Debit";
                items.Add(CreateItem(5, line.Text, ResolveDate(match.Groups[1].Value, match.Groups[2].Value, dueDate), description,
                    amountText, type,
                    installment.Success ? int.Parse(installment.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                    installment.Success ? int.Parse(installment.Groups[2].Value, CultureInfo.InvariantCulture) : null,
                    competence, nature, type != "Payment"));
                continue;
            }
            if (DatePrefixRegex().IsMatch(line.Text) && line.Text.Contains("R$", StringComparison.OrdinalIgnoreCase))
                items.Add(new(0, 5, line.Text, null, line.Text, null, "Unknown", null, null, competence, "Warning", [], ["Linha financeira datada não reconhecida; revise sua participação no total."])
                { OriginalDescription = line.Text, NormalizedDescription = NormalizeDescription(line.Text), Nature = "Unknown", IncludedInCurrentTotal = true });
        }
    }

    private static CreditCardPdfPreviewItem CreateItem(int page, string source, string? date, string description, string amountText, string type,
        int? installmentNumber, int? installmentCount, string? competence, string nature, bool included)
    {
        var amount = ParsePtBr(amountText);
        var normalized = NormalizeDescription(description);
        return new(0, page, source, date, normalized, amount is null ? null : Money.Format(amount.Value), type, installmentNumber, installmentCount,
            competence, amount is null ? "Invalid" : "Valid", amount is null ? ["Valor monetário não reconhecido."] : [], [])
        { OriginalDescription = description.Trim(), NormalizedDescription = normalized, Nature = nature, IncludedInCurrentTotal = included };
    }

    private static List<LayoutLine> BuildLines(CreditCardPdfExtractedDocument document)
    {
        var result = new List<LayoutLine>();
        foreach (var page in document.Pages.Where(page => page.Words is { Count: > 0 }))
        {
            var groups = new List<List<CreditCardPdfExtractedWord>>();
            foreach (var word in page.Words!.OrderBy(word => word.Top).ThenBy(word => word.X))
            {
                var group = groups.LastOrDefault(candidate => Math.Abs(candidate.Average(item => item.Top) - word.Top) <= 2.5);
                if (group is null) { group = []; groups.Add(group); }
                group.Add(word);
            }
            result.AddRange(groups.Select(group => new LayoutLine(page.Number, group.Average(word => word.Top), string.Join(' ', group.OrderBy(word => word.X).Select(word => word.Text)).Trim())));
        }
        return result.OrderBy(line => line.Page).ThenBy(line => line.Top).ToList();
    }

    private static string? FindNamedDate(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (!match.Success || !Months.TryGetValue(RemoveDiacritics(match.Groups[2].Value), out var month)) return null;
        return new DateOnly(int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), month, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    private static string? ResolveDate(string day, string monthName, string? reference)
    {
        if (!Months.TryGetValue(RemoveDiacritics(monthName), out var month) || !DateOnly.TryParseExact(reference, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var referenceDate)) return null;
        var year = month > referenceDate.Month ? referenceDate.Year - 1 : referenceDate.Year;
        return new DateOnly(year, month, int.Parse(day, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    private static string? FindAmount(string text, string pattern) => ParsePtBr(Regex.Match(text, pattern, RegexOptions.IgnoreCase).Groups[1].Value) is { } value ? Money.Format(value) : null;
    private static decimal? ParsePtBr(string? value)
    {
        var normalized = value?.Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Replace('−', '-').Replace(" ", string.Empty).Trim();
        return decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.GetCultureInfo("pt-BR"), out var amount) ? amount : null;
    }
    private static string NormalizeDescription(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim(' ', '-', ':');
    private static string RemoveDiacritics(string value) => string.Concat(value.Normalize(NormalizationForm.FormD).Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)).ToUpperInvariant();
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"(?:•{2,}|\*{2,})\s*(\d{4})\b")]
    private static partial Regex CardDigitsRegex();
    [GeneratedRegex(@"((?:[−-]\s*)?(?:R\$\s*)?[\d.]+,\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex LastAmountRegex();
    [GeneratedRegex(@"^(\d{2})\s+([A-ZÇ]{3})\s+(?:(?:•{2,}|\*{2,})\s*(\d{4})\s+)?(.+?)\s+((?:[−-]\s*)?(?:R\$\s*)?[\d.]+,\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex TransactionRegex();
    [GeneratedRegex(@"Parcela\s+(\d{1,2})/(\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex InstallmentRegex();
    [GeneratedRegex(@"^\d{2}\s+[A-ZÇ]{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex DatePrefixRegex();

    private sealed record LayoutLine(int Page, double Top, string Text);
}

internal static class LayoutLineExtensions
{
    public static int FindIndex<T>(this IReadOnlyList<T> source, Func<T, bool> predicate)
    {
        for (var index = 0; index < source.Count; index++) if (predicate(source[index])) return index;
        return -1;
    }
}

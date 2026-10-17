using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Common;

namespace Saldo360.Infrastructure.CreditCards;

internal static partial class ItauCreditCardPdfParser
{
    private const double ColumnBoundary = 345;

    public static bool CanParse(CreditCardPdfExtractedDocument document)
    {
        var text = string.Join('\n', document.Pages.Select(page => page.Text));
        return text.Contains("Resumo da fatura em R$", StringComparison.OrdinalIgnoreCase)
            && text.Contains("Total dos lançamentos atuais", StringComparison.OrdinalIgnoreCase)
            && text.Contains("Itaú Cartões", StringComparison.OrdinalIgnoreCase);
    }

    public static CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string fileName, string? configuredCompetence)
    {
        var lines = BuildLines(document);
        var page1 = document.Pages.First().Text;
        var dueDate = FindDate(page1, @"Vencimento:\s*(\d{2}/\d{2}/\d{4})");
        var closingDate = FindDate(page1, @"Emiss[aã]o:\s*(\d{2}/\d{2}/\d{4})");
        var competence = configuredCompetence ?? dueDate?[..7];
        var total = FindAmount(page1, @"Total desta fatura\s+([\d.]+,\d{2})")
            ?? FindAmount(page1, @"O total da sua fatura [ée]:\s*R\$\s*([\d.]+,\d{2})");
        var holder = Regex.Match(page1, @"(?m)^(.+?)\s*\r?\nTitular\s*$", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        var cardDigits = Regex.Match(page1, @"\d{4}\.X{4}\.X{4}\.(\d{4})", RegexOptions.IgnoreCase).Groups[1].Value;
        var statement = new CreditCardPdfPreviewStatement(
            "Itaú",
            EmptyToNull(cardDigits),
            competence,
            closingDate,
            dueDate,
            total)
        { HolderName = EmptyToNull(holder) };

        var items = new List<CreditCardPdfPreviewItem>();
        AddSummaryItem(items, lines, "Total da fatura anterior", "PreviousBalance", "Debit", true, dueDate, competence);
        AddSummaryItem(items, lines, "Saldo financiado", "FinancedBalance", "Debit", true, dueDate, competence);
        AddPayment(items, lines, dueDate, competence);
        AddCurrentCardPurchases(items, lines, dueDate, competence);
        AddInternational(items, lines, dueDate, competence);
        AddProductsAndServices(items, lines, dueDate, competence);
        AddFees(items, lines, dueDate, competence);
        AddFutureInstallments(items, lines, dueDate, competence);

        for (var index = 0; index < items.Count; index++) items[index] = items[index] with { RowNumber = index + 1 };

        var componentTotal = items.Where(item => item.IncludedInCurrentTotal && item.Amount is not null)
            .Sum(item => decimal.Parse(item.Amount!, CultureInfo.InvariantCulture));
        decimal? officialTotal = decimal.TryParse(total, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var parsedOfficialTotal) ? parsedOfficialTotal : null;
        decimal? difference = officialTotal is null ? null : officialTotal.Value - componentTotal;
        var hasUnknownCurrent = items.Any(item => item.IncludedInCurrentTotal && item.Type == "Unknown");
        var reconciliationStatus = officialTotal is null || hasUnknownCurrent
            ? "NeedsReview"
            : difference == 0m ? "Reconciled" : "Divergent";

        var reconciliation = new CreditCardPdfReconciliation(
            reconciliationStatus,
            total,
            Money.Format(componentTotal),
            difference is null ? null : Money.Format(difference.Value),
            hasUnknownCurrent);
        var summary = new CreditCardPdfPreviewSummary(
            items.Count(item => item.Status == "Valid"),
            items.Count(item => item.Warnings.Count > 0),
            items.Count(item => item.Status == "Invalid"),
            items.Count(item => item.Type == "Unknown"));
        var warnings = reconciliationStatus switch
        {
            "Divergent" => new[] { "A soma dos componentes reconhecidos diverge do total oficial da fatura." },
            "NeedsReview" => new[] { "A composição da fatura exige revisão porque há componentes desconhecidos ou o total oficial não foi identificado." },
            _ => Array.Empty<string>(),
        };
        return new(fileName, document.Pages.Count, "ItauMastercard", statement, summary, items, warnings)
        {
            Reconciliation = reconciliation,
        };
    }

    private static void AddSummaryItem(List<CreditCardPdfPreviewItem> items, IReadOnlyList<LayoutLine> lines, string label, string type, string nature, bool included, string? date, string? competence)
    {
        var line = lines.FirstOrDefault(candidate => candidate.Page == 1 && candidate.Text.Contains(label, StringComparison.OrdinalIgnoreCase));
        if (line is null) return;
        var match = Regex.Match(line.Text, $@"{Regex.Escape(label)}\s+(-?[\d.]+,\d{{2}})", RegexOptions.IgnoreCase);
        if (!match.Success) return;
        items.Add(CreateItem(1, line.Text, date, label, match.Groups[1].Value, type, null, null, competence, nature, included));
    }

    private static void AddPayment(List<CreditCardPdfPreviewItem> items, IReadOnlyList<LayoutLine> lines, string? dueDate, string? competence)
    {
        var payment = lines.FirstOrDefault(line => line.Page == 2 && line.Column == 0 && PaymentRegex().IsMatch(line.Text));
        if (payment is null) return;
        var match = PaymentRegex().Match(payment.Text);
        items.Add(CreateItem(payment.Page, payment.Text, ResolveDate(match.Groups[1].Value, dueDate), match.Groups[2].Value,
            match.Groups[3].Value, "Payment", null, null, competence, "Credit", true));
    }

    private static void AddCurrentCardPurchases(List<CreditCardPdfPreviewItem> items, IReadOnlyList<LayoutLine> lines, string? dueDate, string? competence)
    {
        var leftStart = lines.Where(line => line.Page == 2 && line.Column == 0 && line.Text.Contains("Lançamentos: compras e saques", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Top).DefaultIfEmpty(double.MaxValue).Max();
        foreach (var line in lines.Where(line =>
                     (line.Page == 2 && (line.Column == 1 || line.Column == 0 && line.Top > leftStart))
                     || line.Page == 3 && line.Column == 0 && line.Top < FindTop(lines, 3, 0, "Lançamentos no cartão")))
        {
            if (TryParseTransaction(line, dueDate, competence, included: true, future: false, out var item)) items.Add(item);
            else AddUnknownDatedLine(items, line, competence, included: true);
        }
    }

    private static void AddInternational(List<CreditCardPdfPreviewItem> items, IReadOnlyList<LayoutLine> lines, string? dueDate, string? competence)
    {
        var start = FindTop(lines, 3, 0, "Lançamentos internacionais");
        var end = FindTop(lines, 3, 0, "Lançamentos: produtos e serviços");
        foreach (var line in lines.Where(line => line.Page == 3 && line.Column == 0 && line.Top > start && line.Top < end))
        {
            if (TryParseTransaction(line, dueDate, competence, true, false, out var item))
                items.Add(item with { Type = "InternationalPurchase" });
            else if (line.Text.StartsWith("Repasse de IOF", StringComparison.OrdinalIgnoreCase) && LastAmountRegex().Match(line.Text) is { Success: true } amount)
                items.Add(CreateItem(3, line.Text, dueDate, "Repasse de IOF", amount.Groups[1].Value, "Fee", null, null, competence, "Debit", true));
            else AddUnknownDatedLine(items, line, competence, included: true);
        }
    }

    private static void AddProductsAndServices(List<CreditCardPdfPreviewItem> items, IReadOnlyList<LayoutLine> lines, string? dueDate, string? competence)
    {
        var start = FindTop(lines, 3, 0, "Lançamentos: produtos e serviços");
        var end = FindTop(lines, 3, 0, "Lançamentos produtos e serviços");
        foreach (var line in lines.Where(line => line.Page == 3 && line.Column == 0 && line.Top > start && line.Top < end))
        {
            if (TryParseTransaction(line, dueDate, competence, true, false, out var item)) items.Add(item);
            else AddUnknownDatedLine(items, line, competence, included: true);
        }
    }

    private static void AddFees(List<CreditCardPdfPreviewItem> items, IReadOnlyList<LayoutLine> lines, string? dueDate, string? competence)
    {
        var start = FindTop(lines, 3, 1, "Encargos cobrados nesta fatura");
        var end = FindTop(lines, 3, 1, "Total de encargos em R$");
        foreach (var line in lines.Where(line => line.Page == 3 && line.Column == 1 && line.Top > start && line.Top < end))
        {
            var amount = LastAmountRegex().Match(line.Text);
            if (!amount.Success || !FeeTextRegex().IsMatch(line.Text)) continue;
            items.Add(CreateItem(3, line.Text, dueDate, DescriptionBeforeAmount(line.Text, amount.Index), amount.Groups[1].Value,
                "Fee", null, null, competence, "Debit", true));
        }
    }

    private static void AddFutureInstallments(List<CreditCardPdfPreviewItem> items, IReadOnlyList<LayoutLine> lines, string? dueDate, string? competence)
    {
        var leftStart = FindTop(lines, 3, 0, "Compras parceladas - próximas faturas");
        var rightEnd = lines.Where(line => line.Page == 3 && line.Column == 1
                && (line.Text.Contains("Próxima fatura", StringComparison.OrdinalIgnoreCase)
                    || line.Text.Contains("Encargos cobrados", StringComparison.OrdinalIgnoreCase)))
            .Select(line => line.Top).DefaultIfEmpty(double.MinValue).Min();
        foreach (var line in lines.Where(line => line.Page == 3 && (line.Column == 1 && line.Top < rightEnd || line.Column == 0 && line.Top > leftStart)))
        {
            if (TryParseTransaction(line, dueDate, competence, false, true, out var item)) items.Add(item);
        }
    }

    private static bool TryParseTransaction(LayoutLine line, string? dueDate, string? competence, bool included, bool future, out CreditCardPdfPreviewItem item)
    {
        item = null!;
        var match = TransactionRegex().Match(line.Text);
        if (!match.Success) return false;
        int? installmentNumber = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : null;
        int? installmentCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : null;
        var amount = ParsePtBr(match.Groups[5].Value);
        var nature = amount < 0 ? "Credit" : future ? "Informational" : "Debit";
        var description = match.Groups[2].Value.Trim();
        var type = future ? "FutureInstallment"
            : FeeTextRegex().IsMatch(description) ? "Fee"
            : amount < 0 && RefundTextRegex().IsMatch(description) ? "Refund"
            : amount < 0 ? "Credit"
            : installmentNumber is not null ? "Installment"
            : "Purchase";
        item = CreateItem(line.Page, line.Text, ResolveDate(match.Groups[1].Value, dueDate), description,
            match.Groups[5].Value, type, installmentNumber, installmentCount, competence, nature, included);
        return true;
    }

    private static void AddUnknownDatedLine(List<CreditCardPdfPreviewItem> items, LayoutLine line, string? competence, bool included)
    {
        if (!DatePrefixRegex().IsMatch(line.Text)) return;
        items.Add(new(0, line.Page, line.Text, null, line.Text, null, "Unknown", null, null, competence,
            "Warning", Array.Empty<string>(), new[] { "Linha financeira datada não reconhecida; revise sua participação no total." })
        {
            OriginalDescription = line.Text,
            NormalizedDescription = NormalizeDescription(line.Text),
            Nature = "Unknown",
            IncludedInCurrentTotal = included,
        });
    }

    private static CreditCardPdfPreviewItem CreateItem(int page, string source, string? date, string description, string amountText,
        string type, int? installmentNumber, int? installmentCount, string? competence, string nature, bool included)
    {
        var amount = ParsePtBr(amountText);
        var normalized = NormalizeDescription(description);
        return new(0, page, source, date, normalized, amount is null ? null : Money.Format(amount.Value), type,
            installmentNumber, installmentCount, competence, amount is null ? "Invalid" : "Valid",
            amount is null ? new[] { "Valor monetário não reconhecido." } : Array.Empty<string>(), Array.Empty<string>())
        {
            OriginalDescription = description.Trim(),
            NormalizedDescription = normalized,
            Nature = nature,
            IncludedInCurrentTotal = included,
        };
    }

    private static IReadOnlyList<LayoutLine> BuildLines(CreditCardPdfExtractedDocument document)
    {
        var result = new List<LayoutLine>();
        foreach (var page in document.Pages.Where(page => page.Words is { Count: > 0 }))
        {
            foreach (var column in new[] { 0, 1 })
            {
                var words = page.Words!.Where(word => column == 0 ? word.X < ColumnBoundary : word.X >= ColumnBoundary)
                    .OrderBy(word => word.Top).ThenBy(word => word.X).ToList();
                var groups = new List<List<CreditCardPdfExtractedWord>>();
                foreach (var word in words)
                {
                    var group = groups.LastOrDefault(candidate => Math.Abs(candidate.Average(item => item.Top) - word.Top) <= 2.5);
                    if (group is null) { group = []; groups.Add(group); }
                    group.Add(word);
                }
                result.AddRange(groups.Select(group => new LayoutLine(page.Number, column, group.Average(word => word.Top),
                    string.Join(' ', group.OrderBy(word => word.X).Select(word => word.Text)).Trim())));
            }
        }
        return result.OrderBy(line => line.Page).ThenBy(line => line.Top).ThenBy(line => line.Column).ToList();
    }

    private static double FindTop(IReadOnlyList<LayoutLine> lines, int page, int column, string text) =>
        lines.FirstOrDefault(line => line.Page == page && line.Column == column && line.Text.Contains(text, StringComparison.OrdinalIgnoreCase))?.Top ?? double.MaxValue;

    private static string? FindDate(string text, string pattern)
    {
        var value = Regex.Match(text, pattern, RegexOptions.IgnoreCase).Groups[1].Value;
        return DateOnly.TryParseExact(value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    private static string? FindAmount(string text, string pattern)
    {
        var value = Regex.Match(text, pattern, RegexOptions.IgnoreCase).Groups[1].Value;
        return ParsePtBr(value) is { } amount ? Money.Format(amount) : null;
    }

    private static decimal? ParsePtBr(string? value) => decimal.TryParse(value,
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
        CultureInfo.GetCultureInfo("pt-BR"), out var amount) ? amount : null;

    private static string? ResolveDate(string dayMonth, string? reference)
    {
        if (reference is null || !DateOnly.TryParseExact(reference, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var referenceDate)) return null;
        var parts = dayMonth.Split('/');
        var month = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var year = month > referenceDate.Month ? referenceDate.Year - 1 : referenceDate.Year;
        return DateOnly.TryParseExact($"{parts[0]}/{parts[1]}/{year}", "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    private static string NormalizeDescription(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        return Regex.Replace(normalized, @"\s+", " ").Trim(' ', '-', ':');
    }

    private static string DescriptionBeforeAmount(string text, int amountIndex) => text[..amountIndex].Trim();
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"^(\d{2}/\d{2})\s+(.+?)\s+(-?[\d.]+,\d{2})$")]
    private static partial Regex PaymentRegex();
    [GeneratedRegex(@"^(\d{2}/\d{2})\s+(.+?)(?:\s+(\d{2})/(\d{2}))?\s+(-?[\d.]+,\d{2})$")]
    private static partial Regex TransactionRegex();
    [GeneratedRegex(@"(-?[\d.]+,\d{2})$")]
    private static partial Regex LastAmountRegex();
    [GeneratedRegex(@"juros|multa|iof|tarifa|encargo", RegexOptions.IgnoreCase)]
    private static partial Regex FeeTextRegex();
    [GeneratedRegex(@"estorno|reembolso|refund", RegexOptions.IgnoreCase)]
    private static partial Regex RefundTextRegex();
    [GeneratedRegex(@"^\d{2}/\d{2}\b")]
    private static partial Regex DatePrefixRegex();

    private sealed record LayoutLine(int Page, int Column, double Top, string Text);
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Saldo360.Application.CreditCards;

// Checks explicit numbering attached to an existing item. Never creates/corrects purchases or schedules.
internal static partial class StatementInstallmentEvidence
{
    public static bool TryValidate(CreditCardStatementAgentItem item, out string error)
    {
        error = string.Empty;
        var description = Numbering(item.OriginalDescription);
        var evidence = Numbering(item.Evidence);
        if (description is not null && evidence is not null && description != evidence)
            error = "PurchaseInstallmentEvidenceConflict:$.items[]";
        else if ((description ?? evidence) is { } explicitNumber)
        {
            if (item.InstallmentNumber is null || item.InstallmentCount is null)
                error = "PurchaseInstallmentMetadataMissing:$.items[].installmentNumber";
            else if (item.InstallmentNumber != explicitNumber.Number || item.InstallmentCount != explicitNumber.Count)
                error = "PurchaseInstallmentMetadataMismatch:$.items[]";
            else if (item.Type != "Installment") error = "PurchaseInstallmentTypeMismatch:$.items[].type";
        }
        else if (item.Type == "Installment" && (item.InstallmentNumber is null || item.InstallmentCount is null))
            error = "PurchaseInstallmentMetadataMissing:$.items[].installmentNumber";
        else if (item.Type == "Purchase" && (item.InstallmentNumber is not null || item.Section == "FutureInstallments"))
            error = "PurchaseInstallmentTypeMismatch:$.items[].type";
        return error.Length == 0;
    }

    private static (int Number, int Count)? Numbering(string text)
    {
        if (text.StartsWith("VISUAL:", StringComparison.Ordinal)) text = text["VISUAL:".Length..];
        // Only a terminal marker after a description, optionally followed by a separated monetary value.
        // A leading dd/MM, a full date, decimal fragment or 3+-digit commercial code is not a marker.
        var match = TerminalNumbering().Match(text);
        if (!match.Success || match.Index == 0) return null;
        var prefix = text[..match.Index].TrimEnd();
        if (!prefix.Any(char.IsLetter) || NonInstallmentLabel().IsMatch(prefix)) return null;
        var number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        var count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
        return number > 0 && number <= count ? (number, count) : null;
    }

    [GeneratedRegex(@"(?<![\d/.,])(?<number>\d{1,2})\s*/\s*(?<count>\d{1,2})(?![\d/.,])(?:\s+(?:R\$\s*)?(?:\d{1,3}(?:\.\d{3})+|\d+)[.,]\d{2})?\s*$")]
    private static partial Regex TerminalNumbering();
    [GeneratedRegex(@"(?:\b(?:c[oó]digo|cod|ref|refer[eê]ncia|pedido|protocolo|data|em))\s*[:#.-]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NonInstallmentLabel();
}

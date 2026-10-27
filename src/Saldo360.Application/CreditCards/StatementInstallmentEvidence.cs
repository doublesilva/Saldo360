using System.Globalization;
using System.Text.RegularExpressions;

namespace Saldo360.Application.CreditCards;

// Allowlisted metadata only. Never contains descriptions, amounts, quotes or provider payloads.
// ItemIndex is zero-based within the provider response; Batch is one-based when batching is used.
public sealed record StatementInstallmentDiagnostic(int ItemIndex, int Page, string Code, string Path,
    string Field, string? Marker, int? InstallmentNumber, int? InstallmentCount, int? Batch = null);

// Checks explicit numbering attached to an existing item. Never creates/corrects purchases or schedules.
internal static partial class StatementInstallmentEvidence
{
    public static bool TryValidate(CreditCardStatementAgentItem item, out string error, int itemIndex = 0,
        ICollection<StatementInstallmentDiagnostic>? diagnostics = null)
    {
        error = string.Empty;
        var description = Numbering(item.OriginalDescription);
        var evidence = Numbering(item.Evidence);
        var explicitNumber = description ?? evidence;
        void Add(string code, string field, (int Number, int Count)? marker) => diagnostics?.Add(new(itemIndex, item.Page, code,
            "$.items[]." + field, field, marker is { } value ? FormattableString.Invariant($"{value.Number:00}/{value.Count:00}") : null,
            item.InstallmentNumber, item.InstallmentCount));
        if (description is not null && evidence is not null && description != evidence)
        {
            error = "PurchaseInstallmentEvidenceConflict:$.items[]";
            // Neither conflicting candidate is reported as an unequivocal document marker.
            Add("PurchaseInstallmentEvidenceConflict", "evidence", null);
        }
        else if (explicitNumber is { } numbering)
        {
            if (item.InstallmentNumber is null || item.InstallmentCount is null)
            {
                error = "PurchaseInstallmentMetadataMissing:$.items[].installmentNumber";
                if (item.InstallmentNumber is null) Add("PurchaseInstallmentMetadataMissing", "installmentNumber", numbering);
                if (item.InstallmentCount is null) Add("PurchaseInstallmentMetadataMissing", "installmentCount", numbering);
            }
            else if (item.InstallmentNumber != numbering.Number || item.InstallmentCount != numbering.Count)
            {
                error = "PurchaseInstallmentMetadataMismatch:$.items[]";
                if (item.InstallmentNumber != numbering.Number) Add("PurchaseInstallmentMetadataMismatch", "installmentNumber", numbering);
                if (item.InstallmentCount != numbering.Count) Add("PurchaseInstallmentMetadataMismatch", "installmentCount", numbering);
            }
            else if (item.Type != "Installment")
            { error = "PurchaseInstallmentTypeMismatch:$.items[].type"; Add("PurchaseInstallmentTypeMismatch", "type", numbering); }
        }
        else if (item.Type == "Installment" && (item.InstallmentNumber is null || item.InstallmentCount is null))
        {
            error = "PurchaseInstallmentMetadataMissing:$.items[].installmentNumber";
            if (item.InstallmentNumber is null) Add("PurchaseInstallmentMetadataMissing", "installmentNumber", null);
            if (item.InstallmentCount is null) Add("PurchaseInstallmentMetadataMissing", "installmentCount", null);
        }
        else if (item.Type == "Purchase" && (item.InstallmentNumber is not null || item.Section == "FutureInstallments"))
        {
            error = "PurchaseInstallmentTypeMismatch:$.items[].type";
            Add("PurchaseInstallmentTypeMismatch", "type", null);
        }
        return error.Length == 0;
    }

    internal static (int Number, int Count)? Numbering(string text)
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

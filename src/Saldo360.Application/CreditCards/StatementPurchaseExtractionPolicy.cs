namespace Saldo360.Application.CreditCards;

// Scope guard, not a parser: it never creates, corrects, removes or reconciles transactions.
public static class StatementPurchaseExtractionPolicy
{
    public static bool TryValidateItem(CreditCardStatementAgentItem item, out string error, int itemIndex = 0,
        ICollection<StatementInstallmentDiagnostic>? diagnostics = null)
    {
        error = string.Empty;
        if (item.Type is not ("Purchase" or "Installment")) error = "PurchaseScopeInvalidType:$.items[].type";
        else if (item.Nature != "Debit") error = "PurchaseScopeInvalidNature:$.items[].nature";
        else if (item.Section is not ("CurrentInvoice" or "FutureInstallments")) error = "PurchaseScopeInvalidSection:$.items[].section";
        else if (item.IncludedInCurrentTotal != (item.Section == "CurrentInvoice")) error = "PurchaseScopeInvalidInclusion:$.items[].includedInCurrentTotal";
        else if (IsSummary(item.OriginalDescription) || IsSummary(item.Evidence)) error = "PurchaseSummaryNotAllowed:$.items[]";
        else if (!StatementInstallmentEvidence.TryValidate(item, out var installmentError, itemIndex, diagnostics)) error = installmentError;
        return error.Length == 0;
    }

    private static bool IsSummary(string text)
    {
        if (text.StartsWith("VISUAL:", StringComparison.Ordinal)) text = text["VISUAL:".Length..];
        var normalized = StatementCategorization.Normalize(text);
        return SummaryPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal))
            || normalized is "TOTAL" or "SUBTOTAL" or "LIMITE" or "SALDO";
    }

    private static readonly string[] SummaryPrefixes =
    [
        "LANCAMENTOSATUAIS", "TOTALDESTAFATURA", "TOTALDAFATURA", "VALORTOTALDAFATURA", "VALORDODOCUMENTO",
        "TOTALDECOMPRAS", "TOTALDASCOMPRAS", "TOTALDELANCAMENTOS", "TOTALDOSLANCAMENTOS", "SUBTOTAL",
        "PAGAMENTOMINIMO", "PAGAMENTOEFETUADO", "PAGAMENTORECEBIDO", "RESUMODAFATURA", "RESUMODOMES",
        "SALDOANTERIOR", "SALDOFINANCIADO", "LIMITEDECREDITO", "LIMITEDISPONIVEL", "TOTALDEPARCELASFUTURAS",
        "PROXIMAFATURA", "DEMAISFATURAS", "TOTALPARAPROXIMASFATURAS"
    ];
}

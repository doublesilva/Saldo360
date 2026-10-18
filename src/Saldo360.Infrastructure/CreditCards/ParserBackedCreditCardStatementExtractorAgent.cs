using System.Globalization;
using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

/// <summary>
/// Adaptador local inicial da fronteira de agente. Mantém os parsers validados como fonte
/// enquanto a implementação semântica externa não é configurada; todo resultado ainda passa
/// pela validação não confiável e pela reconciliação determinística da camada Application.
/// </summary>
internal sealed class ParserBackedCreditCardStatementExtractorAgent(ICreditCardPdfTextExtractor extractor)
    : ICreditCardStatementExtractorAgent
{
    public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preview = extractor.Parse(request.Document, request.FileName, request.ConfiguredCompetence, null);
        var header = new CreditCardStatementAgentHeader(preview.Statement.Issuer, preview.Statement.CardLastDigits,
            preview.Statement.Competence, preview.Statement.ClosingDate, preview.Statement.DueDate, preview.Statement.TotalAmount);
        var items = preview.Items.Where(item => item.Amount is not null).Select(item =>
        {
            var amount = decimal.Parse(item.Amount!, CultureInfo.InvariantCulture);
            var type = item.Type switch
            {
                "InternationalPurchase" => "Purchase",
                "FutureInstallment" => "Installment",
                "Purchase" or "Installment" or "Payment" or "Fee" or "Credit" or "Refund" or "PreviousBalance" or "FinancedBalance" or "Unknown" => item.Type,
                _ => "Unknown",
            };
            var section = item.Type switch
            {
                "FutureInstallment" => "FutureInstallments",
                "Payment" => "Payments",
                "PreviousBalance" or "FinancedBalance" => "PreviousBalance",
                "Fee" => "Fees",
                _ when item.IncludedInCurrentTotal => "CurrentInvoice",
                _ => "Other",
            };
            var nature = item.Nature is "Debit" or "Credit" ? item.Nature : amount < 0m ? "Credit" : "Debit";
            return new CreditCardStatementAgentItem(
                item.OriginalDescription ?? item.Description,
                Math.Abs(amount).ToString("0.00", CultureInfo.InvariantCulture), nature, type,
                item.InstallmentNumber, item.InstallmentCount, section, item.IncludedInCurrentTotal,
                item.Page, Truncate(item.SourceText, 300), type == "Unknown" ? "Low" : "High");
        }).ToList();
        var document = new CreditCardStatementAgentDocument(CreditCardStatementAgentSchema.Version, header, items);
        return Task.FromResult(JsonSerializer.Serialize(document, CreditCardStatementAgentSchema.Json));
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

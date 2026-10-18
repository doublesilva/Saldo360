using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards;

public sealed record CreditCardStatementAgentRequest(
    string FileName,
    CreditCardPdfExtractedDocument Document,
    string? ConfiguredCompetence);

public interface ICreditCardStatementExtractorAgent
{
    Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken cancellationToken);
}
public interface IParserBackedCreditCardStatementExtractorAgent : ICreditCardStatementExtractorAgent;

public enum CreditCardStatementAgentMode { Disabled, Shadow, AgentPrimary }
public sealed class CreditCardStatementAgentOptions
{
    public bool Enabled { get; set; }
    public CreditCardStatementAgentMode Mode { get; set; } = CreditCardStatementAgentMode.Disabled;
    public string Provider { get; set; } = "OpenAI";
    public string Model { get; set; } = "gpt-4o-mini";
    public string Endpoint { get; set; } = "https://api.openai.com/v1/";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyFile { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed record CreditCardStatementAgentProviderRequest(string SystemPrompt, string DocumentText);

public interface ICreditCardStatementAgentProvider
{
    Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken cancellationToken);
}

public sealed class CreditCardStatementAgentUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public static class CreditCardStatementAgentPrompt
{
    public const string Version = "2026-10-18.1";
    public const string System = """
        Você extrai faturas de cartão de crédito para JSON estritamente aderente ao schema fornecido.
        Extraia somente fatos evidenciados no documento. Nunca invente dados ausentes, emissor, produto comercial ou cartão cadastrado.
        Distinga fatura atual, parcelas futuras, pagamentos, créditos, estornos, taxas, saldo anterior e saldo financiado.
        Payment é informacional e deve usar includedInCurrentTotal=false e section=Payments.
        FutureInstallments sempre usa includedInCurrentTotal=false. Componentes correntes usam true somente quando compõem o total oficial.
        Uma parcela intermediária permanece apenas a parcela exibida: nunca multiplique seu valor nem reconstrua a compra completa.
        Dinheiro deve ser string decimal positiva canônica com duas casas; Nature Debit/Credit determina o sinal financeiro.
        Na dúvida, use Type=Unknown e Confidence=Low. Não tente associar o documento a dados externos.
        Para documento que não seja fatura de cartão ou não tenha evidência suficiente, retorne header nulo, items vazio e nunca fabrique uma fatura.
        A saída deve conter somente JSON compatível com o schema.
        """;
}

public sealed class LlmCreditCardStatementExtractorAgent(ICreditCardStatementAgentProvider provider) : ICreditCardStatementExtractorAgent
{
    public Task<string> ExtractAsync(CreditCardStatementAgentRequest request, CancellationToken cancellationToken)
    {
        var pages = string.Join("\n\n", request.Document.Pages.Select(page => $"--- PAGE {page.Number} ---\n{page.Text}"));
        var competence = string.IsNullOrWhiteSpace(request.ConfiguredCompetence) ? string.Empty : $"\nCompetência configurada pelo usuário: {request.ConfiguredCompetence}";
        return provider.CompleteAsync(new(CreditCardStatementAgentPrompt.System, pages + competence), cancellationToken);
    }
}

public sealed record CreditCardStatementAgentHeader(
    string? Issuer,
    string? CardLastDigits,
    string? Competence,
    string? ClosingDate,
    string? DueDate,
    string? OfficialTotal);

public sealed record CreditCardStatementAgentItem(
    string OriginalDescription,
    string Amount,
    string Nature,
    string Type,
    int? InstallmentNumber,
    int? InstallmentCount,
    string Section,
    bool IncludedInCurrentTotal,
    int Page,
    string Evidence,
    string Confidence);

public sealed record CreditCardStatementAgentDocument(
    string SchemaVersion,
    CreditCardStatementAgentHeader Header,
    IReadOnlyList<CreditCardStatementAgentItem> Items);

public sealed record CreditCardStatementAgentPreview(
    string FileName,
    int PageCount,
    CreditCardStatementAgentHeader Header,
    IReadOnlyList<CreditCardStatementAgentItem> Items,
    CreditCardPdfReconciliation Reconciliation);

public sealed record CreditCardStatementAgentPreviewResult(
    CreditCardPdfPreviewAccess Access,
    CreditCardStatementExtractionResult? Preview,
    IDictionary<string, string[]>? Errors);

public interface ICreditCardStatementAgentPreviewService
{
    Task<CreditCardStatementAgentPreviewResult> PreviewAsync(Guid familyId, Guid cardId, Guid userId, Stream pdf,
        long length, string fileName, string? competence, string? password, CancellationToken cancellationToken);
}

public static class CreditCardStatementAgentSchema
{
    public const string Version = "1.0";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly HashSet<string> Natures = ["Debit", "Credit"];
    private static readonly HashSet<string> Types = ["Purchase", "Installment", "Payment", "Fee", "Credit", "Refund", "PreviousBalance", "FinancedBalance", "Unknown"];
    private static readonly HashSet<string> Sections = ["CurrentInvoice", "FutureInstallments", "Payments", "PreviousBalance", "Fees", "Other"];
    private static readonly HashSet<string> Confidences = ["High", "Medium", "Low"];

    public static bool TryValidate(string json, int pageCount, out CreditCardStatementAgentDocument? document, out string error)
    {
        document = null;
        error = "A resposta do agente não corresponde ao schema esperado.";
        try { document = JsonSerializer.Deserialize<CreditCardStatementAgentDocument>(json, Json); }
        catch (JsonException) { return false; }
        if (document is null || document.SchemaVersion != Version || document.Header is null || document.Items is null) return false;
        if (!OptionalText(document.Header.Issuer, 120) || !OptionalDigits(document.Header.CardLastDigits, 4)
            || !OptionalCompetence(document.Header.Competence) || !OptionalDate(document.Header.ClosingDate)
            || !OptionalDate(document.Header.DueDate) || !OptionalMoney(document.Header.OfficialTotal)) return false;
        var normalized = new List<CreditCardStatementAgentItem>(document.Items.Count);
        foreach (var item in document.Items)
        {
            if (string.IsNullOrWhiteSpace(item.OriginalDescription) || item.OriginalDescription.Length > 500
                || !TryMoney(item.Amount, out var amount) || amount < 0m || !Natures.Contains(item.Nature)
                || !Types.Contains(item.Type) || !Sections.Contains(item.Section) || !Confidences.Contains(item.Confidence)
                || item.Page < 1 || item.Page > pageCount || string.IsNullOrWhiteSpace(item.Evidence) || item.Evidence.Length > 300
                || !ValidInstallment(item)) return false;
            var type = item.Confidence == "Low" ? "Unknown" : item.Type;
            var included = item.IncludedInCurrentTotal;
            if (type == "Payment" || item.Section is "FutureInstallments" or "Payments") included = false;
            normalized.Add(item with { Amount = Money.Format(amount), Type = type, IncludedInCurrentTotal = included });
        }
        document = document with { Items = normalized };
        error = string.Empty;
        return true;
    }

    private static bool ValidInstallment(CreditCardStatementAgentItem item) =>
        item.InstallmentNumber is null && item.InstallmentCount is null
        || item.InstallmentNumber is > 0 && item.InstallmentCount is > 0 && item.InstallmentNumber <= item.InstallmentCount;
    private static bool OptionalText(string? value, int max) => value is null || value.Length is > 0 && value.Length <= max;
    private static bool OptionalDigits(string? value, int length) => value is null || value.Length == length && value.All(char.IsAsciiDigit);
    private static bool OptionalCompetence(string? value) => value is null || DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static bool OptionalDate(string? value) => value is null || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static bool OptionalMoney(string? value) => value is null || TryMoney(value, out var amount) && amount >= 0m;
    private static bool TryMoney(string? value, out decimal amount) => decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
        && value is not null && value == amount.ToString("0.00", CultureInfo.InvariantCulture);
}

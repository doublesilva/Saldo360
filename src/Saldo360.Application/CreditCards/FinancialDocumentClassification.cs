using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saldo360.Application.CreditCards;

public enum FinancialDocumentType { CreditCardStatement, ElectricityBill, WaterBill, TelecomBill, BillingDocument, Unknown }
public enum FinancialDocumentConfidence { High, Medium, Low }
public enum FinancialDocumentClassificationSource { Deterministic, Agent, Unknown }

public sealed record FinancialDocumentClassificationResult(
    string DocumentType, string Confidence, IReadOnlyList<string> Evidence, string ClassificationSource, string? FailureCode = null);
public sealed record FinancialDocumentClassificationRequest(string FileName, CreditCardPdfExtractedDocument Document);

public interface IFinancialDocumentClassifier
{
    Task<FinancialDocumentClassificationResult> ClassifyAsync(FinancialDocumentClassificationRequest request, CancellationToken cancellationToken);
}
public interface IFinancialDocumentClassificationAgent
{
    Task<string> ClassifyAsync(FinancialDocumentClassificationRequest request, CancellationToken cancellationToken);
}

public sealed class FinancialDocumentClassificationOptions
{
    public bool Enabled { get; set; }
    public bool AgentEnabled { get; set; }
}

public static class FinancialDocumentClassificationPrompt
{
    public const string Version = "2026-10-18.1";
    public const string System = """
        Classifique o tipo semântico do documento financeiro somente pelo conteúdo fornecido.
        O nome do arquivo não é prova suficiente. Não assuma fornecedor pelo formato visual e não invente campos.
        Não classifique como fatura de cartão apenas porque existem vencimento e valor; diferencie conta de consumo e fatura de cartão.
        Use Unknown com confiança Low quando a evidência for insuficiente ou ambígua.
        Não associe cartão cadastrado e não tome qualquer decisão financeira.
        Retorne somente JSON estritamente compatível com o schema, com evidências curtas e sem dados pessoais.
        """;
}

public sealed class FinancialDocumentClassificationAgent(ICreditCardStatementAgentProvider provider) : IFinancialDocumentClassificationAgent
{
    public Task<string> ClassifyAsync(FinancialDocumentClassificationRequest request, CancellationToken cancellationToken)
    {
        var text = string.Join("\n\n", request.Document.Pages.Select(page => $"--- PAGE {page.Number} ---\n{page.Text}"));
        return provider.CompleteAsync(new(FinancialDocumentClassificationPrompt.System, text)
        { SchemaName = "financial_document_classification", OutputSchema = FinancialDocumentClassificationSchema.OutputSchema }, cancellationToken);
    }
}

public sealed class CompositeFinancialDocumentClassifier(
    FinancialDocumentClassificationOptions options,
    IFinancialDocumentClassificationAgent agent) : IFinancialDocumentClassifier
{
    private static readonly Meter Meter = new("Saldo360.FinancialDocumentClassification", "1.0");
    private static readonly Counter<long> Runs = Meter.CreateCounter<long>("financial_document_classification_runs");

    public async Task<FinancialDocumentClassificationResult> ClassifyAsync(FinancialDocumentClassificationRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var deterministic = Deterministic(request.Document);
        if (deterministic is not null) { Record(deterministic, request.Document.Pages.Count, started.ElapsedMilliseconds); return deterministic; }
        if (!options.AgentEnabled)
        {
            var unknown = Unknown("InsufficientEvidence"); Record(unknown, request.Document.Pages.Count, started.ElapsedMilliseconds); return unknown;
        }
        try
        {
            var json = await agent.ClassifyAsync(request, cancellationToken);
            if (!FinancialDocumentClassificationSchema.TryValidate(json, out var result))
            { var invalid = Unknown("InvalidAgentResponse"); Record(invalid, request.Document.Pages.Count, started.ElapsedMilliseconds); return invalid; }
            Record(result!, request.Document.Pages.Count, started.ElapsedMilliseconds); return result!;
        }
        catch (CreditCardStatementAgentUnavailableException)
        { var unavailable = Unknown("ClassificationProviderUnavailable"); Record(unavailable, request.Document.Pages.Count, started.ElapsedMilliseconds); return unavailable; }
    }

    public static FinancialDocumentClassificationResult? Deterministic(CreditCardPdfExtractedDocument document)
    {
        var text = Normalize(string.Join(' ', document.Pages.Select(page => page.Text)));
        if (string.IsNullOrWhiteSpace(text) || text.Length < 30) return null;
        var evidence = new List<string>();
        if (Has(text, "kwh") && (Has(text, "energia eletrica") || Has(text, "unidade consumidora")) && (Has(text, "medicao") || Has(text, "tarifa")))
        { evidence.Add("Consumo em kWh"); evidence.Add("Medição ou tarifa de energia"); return Result(FinancialDocumentType.ElectricityBill, evidence); }
        if ((Has(text, "m³") || Has(text, "m3")) && (Has(text, "hidrometro") || Has(text, "agua e esgoto") || Has(text, "consumo de agua")))
        { evidence.Add("Consumo de água em m³"); evidence.Add("Hidrômetro ou água/esgoto"); return Result(FinancialDocumentType.WaterBill, evidence); }
        if ((Has(text, "telefone") || Has(text, "internet") || Has(text, "servicos moveis") || Has(text, "servicos fixos"))
            && (Has(text, "plano") || Has(text, "linha") || Has(text, "franquia")) && Has(text, "vencimento"))
        { evidence.Add("Serviço de telefonia ou internet"); evidence.Add("Plano ou linha e vencimento"); return Result(FinancialDocumentType.TelecomBill, evidence); }
        if (Has(text, "fatura") && (Has(text, "limite") || Has(text, "cartao de credito")) && (Has(text, "pagamento minimo") || Has(text, "melhor dia de compra") || Has(text, "lancamentos")))
        { evidence.Add("Fatura e limite do cartão"); evidence.Add("Pagamento mínimo ou lançamentos"); return Result(FinancialDocumentType.CreditCardStatement, evidence); }
        if (Has(text, "vencimento") && (Has(text, "valor a pagar") || Has(text, "total a pagar")) && (Has(text, "codigo de barras") || Has(text, "linha digitavel") || Has(text, "cobranca")))
        { evidence.Add("Vencimento e valor a pagar"); evidence.Add("Código de barras ou cobrança"); return Result(FinancialDocumentType.BillingDocument, evidence); }
        return null;
    }

    private static FinancialDocumentClassificationResult Result(FinancialDocumentType type, IReadOnlyList<string> evidence) =>
        new(type.ToString(), FinancialDocumentConfidence.High.ToString(), evidence, FinancialDocumentClassificationSource.Deterministic.ToString());
    private static FinancialDocumentClassificationResult Unknown(string failure) => new(FinancialDocumentType.Unknown.ToString(), FinancialDocumentConfidence.Low.ToString(), [], FinancialDocumentClassificationSource.Unknown.ToString(), failure);
    private static bool Has(string text, string value) => text.Contains(value, StringComparison.Ordinal);
    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD); var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed) if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) result.Append(character);
        return result.ToString().Normalize(NormalizationForm.FormC);
    }
    private static void Record(FinancialDocumentClassificationResult result, int pages, long duration) => Runs.Add(1,
        new("document.type", result.DocumentType), new("confidence", result.Confidence), new("source", result.ClassificationSource), new("pages", pages), new("duration.ms", duration), new("success", result.FailureCode is null));
}

public static class FinancialDocumentClassificationSchema
{
    private sealed record AgentResult(string DocumentType, string Confidence, IReadOnlyList<string> Evidence);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static readonly JsonElement OutputSchema = JsonDocument.Parse("""
      {"type":"object","additionalProperties":false,"required":["documentType","confidence","evidence"],"properties":{
       "documentType":{"type":"string","enum":["CreditCardStatement","ElectricityBill","WaterBill","TelecomBill","BillingDocument","Unknown"]},
       "confidence":{"type":"string","enum":["High","Medium","Low"]},
       "evidence":{"type":"array","maxItems":5,"items":{"type":"string","maxLength":120}}}}
      """).RootElement.Clone();

    public static bool TryValidate(string json, out FinancialDocumentClassificationResult? result)
    {
        result = null; AgentResult? value;
        try { value = JsonSerializer.Deserialize<AgentResult>(json, Json); } catch (JsonException) { return false; }
        if (value is null || !Enum.TryParse<FinancialDocumentType>(value.DocumentType, out var type)
            || !Enum.TryParse<FinancialDocumentConfidence>(value.Confidence, out var confidence) || value.Evidence is null
            || value.Evidence.Count > 5 || value.Evidence.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 120)) return false;
        if (confidence == FinancialDocumentConfidence.Low) type = FinancialDocumentType.Unknown;
        result = new(type.ToString(), confidence.ToString(), value.Evidence.Select(Sanitize).ToList(),
            type == FinancialDocumentType.Unknown ? FinancialDocumentClassificationSource.Unknown.ToString() : FinancialDocumentClassificationSource.Agent.ToString(),
            type == FinancialDocumentType.Unknown ? "InsufficientEvidence" : null);
        return true;
    }
    private static string Sanitize(string value) => new(value.Where(character => !char.IsControl(character)).Take(120).ToArray());
}

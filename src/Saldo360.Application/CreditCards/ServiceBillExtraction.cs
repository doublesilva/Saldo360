using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Saldo360.Domain.Common;

namespace Saldo360.Application.CreditCards;

public enum ServiceBillExtractionSource { Deterministic, Agent, Unknown }
public enum ServiceBillExtractionAssessment { Reliable, NeedsReview, Rejected }
public sealed record ServiceBillExtractionResult(string DocumentType, string? Issuer, string? Competence, string? DueDate,
    string? TotalAmount, string? AccountReference, string? ServiceReference, string Confidence, string ExtractionSource, string? FailureCode);
public sealed record ServiceBillExtractionOutput(ServiceBillExtractionResult Result, string Assessment);
public sealed record ServiceBillExtractionRequest(string FileName, CreditCardPdfExtractedDocument Document, string DocumentType);
public interface IServiceBillExtractor { Task<ServiceBillExtractionOutput> ExtractAsync(ServiceBillExtractionRequest request, CancellationToken cancellationToken); }
public interface IServiceBillExtractionAgent { Task<string> ExtractAsync(ServiceBillExtractionRequest request, CancellationToken cancellationToken); }
public sealed class ServiceBillExtractionOptions { public bool Enabled { get; set; } public bool AgentEnabled { get; set; } }

public static class ServiceBillExtractionPrompt
{
    public const string Version = "2026-10-18.1";
    public const string System = """
      Extraia somente dados explicitamente presentes na conta de serviço. Nunca invente competência ou vencimento, não use valor médio,
      não associe conta, categoria ou recorrência do Saldo360 e não marque a conta como paga. Retorne null para dado ausente.
      Dinheiro deve ser decimal canônico não negativo com duas casas, competência yyyy-MM e vencimento yyyy-MM-dd.
      Retorne exclusivamente JSON aderente ao schema, sem campos adicionais.
      """;
}

public sealed class ServiceBillExtractionAgent(ICreditCardStatementAgentProvider provider) : IServiceBillExtractionAgent
{
    public Task<string> ExtractAsync(ServiceBillExtractionRequest request, CancellationToken cancellationToken)
    {
        var pages = string.Join("\n\n", request.Document.Pages.Select(page => $"--- PAGE {page.Number} ---\n{page.Text}"));
        return provider.CompleteAsync(new(ServiceBillExtractionPrompt.System, $"Tipo já classificado: {request.DocumentType}\n{pages}")
        { SchemaName = "service_bill_extraction", OutputSchema = ServiceBillExtractionSchema.OutputSchema }, cancellationToken);
    }
}

public sealed class CompositeServiceBillExtractor(ServiceBillExtractionOptions options, IServiceBillExtractionAgent agent) : IServiceBillExtractor
{
    private static readonly HashSet<string> Supported = ["ElectricityBill", "WaterBill", "TelecomBill"];
    public async Task<ServiceBillExtractionOutput> ExtractAsync(ServiceBillExtractionRequest request, CancellationToken cancellationToken)
    {
        if (!Supported.Contains(request.DocumentType)) return Rejected(request.DocumentType, "UnsupportedDocumentType");
        var deterministic = ExtractDeterministic(request);
        if (Assess(deterministic) == ServiceBillExtractionAssessment.Reliable || !options.AgentEnabled)
            return new(deterministic, Assess(deterministic).ToString());
        try
        {
            var json = await agent.ExtractAsync(request, cancellationToken);
            if (!ServiceBillExtractionSchema.TryValidate(json, request.DocumentType, out var result)) return Rejected(request.DocumentType, "InvalidAgentResponse");
            return new(result!, Assess(result!).ToString());
        }
        catch (CreditCardStatementAgentUnavailableException) { return Rejected(request.DocumentType, "ExtractionProviderUnavailable"); }
    }

    private static ServiceBillExtractionResult ExtractDeterministic(ServiceBillExtractionRequest request)
    {
        if (DeterministicServiceBillLayouts.TryExtract(request) is { } layout) return layout;
        var text = string.Join("\n", request.Document.Pages.Select(page => page.Text));
        var amount = Match(text, @"(?im)(?:valor\s+(?:total\s+)?a\s+pagar|total\s+a\s+pagar)\s*[:\-]?\s*R?\$?\s*([0-9.]+,[0-9]{2}|[0-9]+\.[0-9]{2})");
        var competence = Match(text, @"(?im)(?:m[eê]s\s+de\s+refer[eê]ncia|refer[eê]ncia|compet[eê]ncia)\s*[:\-]?\s*(\d{2}/\d{4}|\d{4}-\d{2})");
        var due = Match(text, @"(?im)vencimento\s*[:\-]?\s*(\d{2}/\d{2}/\d{4}|\d{4}-\d{2}-\d{2})");
        var account = Match(text, @"(?im)(?:unidade\s+consumidora|matr[ií]cula|n[uú]mero\s+da\s+conta|contrato)\s*[:\-]?\s*([A-Z0-9.\-/]{3,40})");
        var service = Match(text, @"(?im)(?:hidr[oô]metro|linha|instala[cç][aã]o)\s*[:\-]?\s*([A-Z0-9.\-/]{3,40})");
        return new(request.DocumentType, null, NormalizeCompetence(competence), NormalizeDate(due), NormalizeMoney(amount), account, service,
            "High", "Deterministic", null);
    }
    public static ServiceBillExtractionAssessment Assess(ServiceBillExtractionResult result)
    {
        if (result.FailureCode == "LowConfidence") return ServiceBillExtractionAssessment.NeedsReview;
        if (result.ExtractionSource == "Deterministic" && result.FailureCode is { } code &&
            (code.EndsWith("NotFound", StringComparison.Ordinal) || code.StartsWith("Ambiguous", StringComparison.Ordinal) || code.StartsWith("Invalid", StringComparison.Ordinal)))
            return ServiceBillExtractionAssessment.NeedsReview;
        if (!Supported.Contains(result.DocumentType) || result.FailureCode is not null) return ServiceBillExtractionAssessment.Rejected;
        if (result.Confidence == "Low" || result.TotalAmount is null || result.Competence is null || result.DueDate is null) return ServiceBillExtractionAssessment.NeedsReview;
        return ServiceBillExtractionAssessment.Reliable;
    }
    private static string? Match(string text, string pattern) { var match = Regex.Match(text, pattern); return match.Success ? match.Groups[1].Value.Trim() : null; }
    private static string? NormalizeMoney(string? value)
    {
        if (value is null) return null; var normalized = value.Contains(',') ? value.Replace(".", "").Replace(',', '.') : value;
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) && amount >= 0 ? Money.Format(amount) : null;
    }
    private static string? NormalizeCompetence(string? value)
    {
        if (value is null) return null; if (DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return value;
        return DateOnly.TryParseExact("01/" + value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM") : null;
    }
    private static string? NormalizeDate(string? value) => value is not null && (DateOnly.TryParseExact(value, ["yyyy-MM-dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) ? date.ToString("yyyy-MM-dd") : null;
    private static ServiceBillExtractionOutput Rejected(string type, string code) => new(new(type, null, null, null, null, null, null, "Low", "Unknown", code), "Rejected");
}

public static class ServiceBillExtractionSchema
{
    private sealed record AgentResult(string DocumentType, string? Issuer, string? Competence, string? DueDate, string? TotalAmount, string? AccountReference, string? ServiceReference, string Confidence);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static readonly JsonElement OutputSchema = JsonDocument.Parse("""
      {"type":"object","additionalProperties":false,"required":["documentType","issuer","competence","dueDate","totalAmount","accountReference","serviceReference","confidence"],"properties":{
      "documentType":{"type":"string","enum":["ElectricityBill","WaterBill","TelecomBill"]},"issuer":{"type":["string","null"]},
      "competence":{"type":["string","null"]},"dueDate":{"type":["string","null"]},"totalAmount":{"type":["string","null"]},
      "accountReference":{"type":["string","null"]},"serviceReference":{"type":["string","null"]},"confidence":{"type":"string","enum":["High","Medium","Low"]}}}
      """).RootElement.Clone();
    public static bool TryValidate(string json, string expectedType, out ServiceBillExtractionResult? result)
    {
        result = null; AgentResult? value; try { value = JsonSerializer.Deserialize<AgentResult>(json, Json); } catch (JsonException) { return false; }
        if (value is null || value.DocumentType != expectedType || value.Confidence is not ("High" or "Medium" or "Low")
            || !ValidMonth(value.Competence) || !ValidDate(value.DueDate) || !ValidMoney(value.TotalAmount)
            || !Short(value.Issuer, 120) || !Short(value.AccountReference, 80) || !Short(value.ServiceReference, 80)) return false;
        result = new(value.DocumentType, value.Issuer, value.Competence, value.DueDate, value.TotalAmount, value.AccountReference, value.ServiceReference,
            value.Confidence, value.Confidence == "Low" ? "Unknown" : "Agent", value.Confidence == "Low" ? "LowConfidence" : null); return true;
    }
    private static bool ValidMoney(string? value) => value is null || decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) && amount >= 0 && value == amount.ToString("0.00", CultureInfo.InvariantCulture);
    private static bool ValidMonth(string? value) => value is null || DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static bool ValidDate(string? value) => value is null || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static bool Short(string? value, int max) => value is null || value.Length is > 0 && value.Length <= max;
}

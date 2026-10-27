using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

// Explicit lab-only transport tuning. The API's existing constructor/defaults are unchanged.
internal sealed record OpenAiStatementRequestTuning(string ReasoningEffort, int MaxOutputTokens, string ImageDetail)
{
    public void Validate(string model)
    {
        if (model is not ("gpt-6.1-sol" or "gpt-6-astra") || ReasoningEffort is not ("medium" or "high")
            || MaxOutputTokens is < 25000 or > 65536 || ImageDetail is not ("high" or "original")
            || ImageDetail == "original" && model != "gpt-6-astra")
            throw new ArgumentException("LabModelConfigurationInvalid");
    }
}

internal sealed class OpenAiCreditCardStatementAgentProvider(
    IHttpClientFactory clients,
    CreditCardStatementAgentOptions options,
    ILogger<OpenAiCreditCardStatementAgentProvider> logger,
    OpenAiStatementRequestTuning? tuning = null, string clientName = "CreditCardStatementAgent") : ICreditCardStatementAgentProvider
{
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
    {
      "type":"object","additionalProperties":false,
      "required":["schemaVersion","header","items"],
      "properties":{
        "schemaVersion":{"type":"string","enum":["2.0"]},
        "header":{"type":"object","additionalProperties":false,
          "required":["issuer","cardLastDigits","competence","closingDate","dueDate","officialTotal","cardProduct","holderName","identityEvidence","identityPage"],
          "properties":{
            "issuer":{"type":["string","null"],"minLength":1,"maxLength":120},"cardLastDigits":{"type":["string","null"],"pattern":"^[0-9]{4}$"},
            "competence":{"type":["string","null"],"pattern":"^[0-9]{4}-(0[1-9]|1[0-2])$"},"closingDate":{"type":["string","null"],"format":"date"},
            "dueDate":{"type":["string","null"],"format":"date"},"officialTotal":{"type":["string","null"],"pattern":"^(0|[1-9][0-9]{0,12})\\.[0-9]{2}$"},
            "cardProduct":{"type":["string","null"],"minLength":1,"maxLength":120},"holderName":{"type":["string","null"],"minLength":1,"maxLength":200},
            "identityEvidence":{"type":["string","null"],"minLength":1,"maxLength":1000},"identityPage":{"type":["integer","null"],"minimum":1}
          }},
        "items":{"type":"array","maxItems":2000,"items":{"type":"object","additionalProperties":false,
          "required":["originalDescription","amount","nature","type","installmentNumber","installmentCount","section","includedInCurrentTotal","page","evidence","confidence","transactionDate","normalizedDescription","suggestedCategory","categoryConfidence"],
          "properties":{
            "originalDescription":{"type":"string","minLength":1,"maxLength":500},"amount":{"type":"string","pattern":"^(0|[1-9][0-9]{0,12})\\.[0-9]{2}$"},
            "transactionDate":{"type":["string","null"],"format":"date"},"normalizedDescription":{"type":["string","null"],"minLength":1,"maxLength":500},
            "suggestedCategory":{"type":["string","null"],"minLength":1,"maxLength":100},"categoryConfidence":{"type":["string","null"],"enum":["High","Medium","Low",null]},
            "nature":{"type":"string","enum":["Debit","Credit"]},
            "type":{"type":"string","enum":["Purchase","Installment","Payment","Fee","Credit","Refund","PreviousBalance","FinancedBalance","Unknown"]},
            "installmentNumber":{"type":["integer","null"],"minimum":1},"installmentCount":{"type":["integer","null"],"minimum":1},
            "section":{"type":"string","enum":["CurrentInvoice","FutureInstallments","Payments","PreviousBalance","Fees","Other"]},
            "includedInCurrentTotal":{"type":"boolean"},"page":{"type":"integer","minimum":1},
            "evidence":{"type":"string","minLength":1,"maxLength":300},"confidence":{"type":"string","enum":["High","Medium","Low"]}
          }}}
      }
    }
    """).RootElement.Clone();

    internal static readonly JsonElement PurchasesOnlySchema = CreatePurchasesOnlySchema();
    private static JsonElement CreatePurchasesOnlySchema()
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse(OutputSchema.GetRawText())!;
        var properties = schema["properties"]!["items"]!["items"]!["properties"]!;
        properties["nature"]!["enum"] = new System.Text.Json.Nodes.JsonArray("Debit");
        properties["type"]!["enum"] = new System.Text.Json.Nodes.JsonArray("Purchase", "Installment");
        properties["section"]!["enum"] = new System.Text.Json.Nodes.JsonArray("CurrentInvoice", "FutureInstallments");
        properties["type"]!["description"] = "Use Installment whenever an explicit X/Y is associated with the purchase description, including MERCHANT06/06 without a space; Purchase only when no installment is documented.";
        properties["installmentNumber"]!["description"] = "X in the documented X/Y marker; 06/06 means 6. Null only when no installment is documented. Not the day of a date.";
        properties["installmentCount"]!["description"] = "Y in the documented X/Y marker; 03/12 means 12. Do not infer from money, dates or commercial codes.";
        properties["amount"]!["description"] = "Exact individual amount of the displayed purchase/installment, not the original purchase total, a subtotal or an amount multiplied by Y.";
        properties["transactionDate"]!["description"] = "Date of the individual transaction/purchase, yyyy-MM-dd or null. Never the invoice due date, closing date or a date inferred from X/Y.";
        properties["section"]!["description"] = "Individual rows under Compras parceladas - próximas faturas belong to FutureInstallments with includedInCurrentTotal=false. Do not include section totals.";
        return JsonSerializer.SerializeToElement(schema);
    }

    public async Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken cancellationToken)
    {
        if (!options.Enabled || options.Mode == CreditCardStatementAgentMode.Disabled || !string.Equals(options.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            throw new CreditCardStatementAgentUnavailableException("Provider desabilitado.", failureCode: "AgentUnavailable");
        tuning?.Validate(options.Model);
        var key = ReadSecret(options.ApiKeyFile, options.ApiKey);
        var client = clients.CreateClient(clientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, "responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var content = new List<object> { new { type = "input_text", text = request.DocumentText } };
        if (request.ImagePages.Count > 12 || request.ImagePages.Select(p => p.Number).Distinct().Count() != request.ImagePages.Count
            || request.ImagePages.Sum(p => (long)(p.ImagePng?.Length ?? 0)) > 24 * 1024 * 1024)
            throw new CreditCardStatementAgentUnavailableException("Limite multimodal excedido.", failureCode: "VisualInputLimitExceeded");
        foreach (var page in request.ImagePages.OrderBy(p => p.Number))
        {
            if (page.ImagePng is not { Length: > 0 and <= 4 * 1024 * 1024 } png || png.Length < 8
                || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new CreditCardStatementAgentUnavailableException("Imagem indisponível.", failureCode: "InvalidVisualInput");
            content.Add(new { type = "input_text", text = $"--- IMAGE PAGE {page.Number} ---" });
            content.Add(new { type = "input_image", image_url = "data:image/png;base64," + Convert.ToBase64String(png), detail = tuning?.ImageDetail ?? "high" });
        }
        var payload = new Dictionary<string, object>
        {
            ["model"] = options.Model,
            ["store"] = false,
            ["max_output_tokens"] = tuning?.MaxOutputTokens ?? 12000,
            ["input"] = new object[]
            {
                new { role = "system", content = new[] { new { type = "input_text", text = request.SystemPrompt } } },
                new { role = "user", content = content.ToArray() },
            },
            ["text"] = new { format = new { type = "json_schema", name = request.SchemaName, strict = true, schema = request.PurchasesOnly ? PurchasesOnlySchema : request.OutputSchema ?? OutputSchema } },
        };
        if (tuning is not null) payload["reasoning"] = new { effort = tuning.ReasoningEffort };
        message.Content = JsonContent.Create(payload);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new CreditCardStatementAgentUnavailableException("Provider recusou a solicitação.", failureCode: await FailureCodeAsync(response, cancellationToken));
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bounded = new MemoryStream();
            var chunk = new byte[8192];
            int length;
            while ((length = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (bounded.Length + length > 4 * 1024 * 1024)
                    throw new CreditCardStatementAgentUnavailableException("Limite de resposta excedido.", failureCode: "ProviderResponseLimitExceeded");
                bounded.Write(chunk, 0, length);
            }
            using var json = JsonDocument.Parse(bounded.GetBuffer().AsMemory(0, (int)bounded.Length));
            if (json.RootElement.TryGetProperty("status", out var responseStatus) && responseStatus.GetString() is { } status && status != "completed")
            {
                var truncated = json.RootElement.TryGetProperty("incomplete_details", out var detail) && detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("reason", out var reason) && reason.GetString() == "max_output_tokens";
                throw new CreditCardStatementAgentUnavailableException("Resposta incompleta.", failureCode: truncated ? "ProviderOutputTruncated" : "ProviderIncompleteResponse");
            }
            var outputText = json.RootElement.GetProperty("output").EnumerateArray()
                .SelectMany(item => item.TryGetProperty("content", out var content) ? content.EnumerateArray().ToArray() : [])
                .FirstOrDefault(item => item.TryGetProperty("type", out var type) && type.GetString() == "output_text");
            if (outputText.ValueKind == JsonValueKind.Undefined || !outputText.TryGetProperty("text", out var text) || string.IsNullOrWhiteSpace(text.GetString()))
                throw new CreditCardStatementAgentUnavailableException("Provider não retornou saída estruturada.", failureCode: "ProviderNoStructuredOutput");
            logger.LogInformation("Credit card statement agent completed provider={Provider} model={Model} durationMs={Duration}", "OpenAI", options.Model, started.ElapsedMilliseconds);
            return text.GetString()!;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new CreditCardStatementAgentUnavailableException("Timeout do provider.", exception, "ProviderTimeout"); }
        catch (HttpRequestException exception)
        { throw new CreditCardStatementAgentUnavailableException("Falha de comunicação com o provider.", exception, "ProviderNetworkFailure"); }
        catch (JsonException) { throw new CreditCardStatementAgentUnavailableException("Resposta inválida do provider.", failureCode: "InvalidProviderResponse"); }
        catch (InvalidOperationException) { throw new CreditCardStatementAgentUnavailableException("Resposta inválida do provider.", failureCode: "InvalidProviderResponse"); }
        catch (KeyNotFoundException) { throw new CreditCardStatementAgentUnavailableException("Resposta inválida do provider.", failureCode: "InvalidProviderResponse"); }
    }

    private static async Task<string> FailureCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        // Parse only bounded error.code; never return or log the provider's free-text message/body.
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[4097]; var count = 0;
            while (count < buffer.Length)
            { var read = await body.ReadAsync(buffer.AsMemory(count), ct); if (read == 0) break; count += read; }
            if (count <= 4096)
            {
                using var error = JsonDocument.Parse(buffer.AsMemory(0, count));
                if (error.RootElement.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString();
            }
        }
        catch (JsonException) { }
        return (int)response.StatusCode switch
        {
            401 => "ProviderAuthenticationFailed", 403 => "ProviderPermissionDenied",
            404 => code == "model_not_found" ? "ProviderModelUnavailable" : "ProviderResourceNotFound",
            429 => code == "insufficient_quota" ? "ProviderQuotaExceeded" : "ProviderRateLimited",
            400 => code == "model_not_found" ? "ProviderModelUnavailable" : "ProviderInvalidRequest",
            _ => "ProviderHttpFailure"
        };
    }

    private static string ReadSecret(string file, string configured)
    {
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file)) throw new CreditCardStatementAgentUnavailableException("Arquivo de segredo do provider não encontrado.", failureCode: "ProviderCredentialMissing");
            string value;
            try { value = File.ReadAllText(file).TrimEnd('\r', '\n'); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new CreditCardStatementAgentUnavailableException("Arquivo de segredo indisponível.", failureCode: "ProviderCredentialUnreadable"); }
            if (string.IsNullOrWhiteSpace(value)) throw new CreditCardStatementAgentUnavailableException("Arquivo de segredo do provider está vazio.", failureCode: "ProviderCredentialEmpty");
            return value;
        }
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        throw new CreditCardStatementAgentUnavailableException("Credencial do provider não configurada.");
    }
}

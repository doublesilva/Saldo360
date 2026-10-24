using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class OpenAiCreditCardStatementAgentProvider(
    IHttpClientFactory clients,
    CreditCardStatementAgentOptions options,
    ILogger<OpenAiCreditCardStatementAgentProvider> logger) : ICreditCardStatementAgentProvider
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
            "issuer":{"type":["string","null"]},"cardLastDigits":{"type":["string","null"]},
            "competence":{"type":["string","null"]},"closingDate":{"type":["string","null"]},
            "dueDate":{"type":["string","null"]},"officialTotal":{"type":["string","null"]},
            "cardProduct":{"type":["string","null"]},"holderName":{"type":["string","null"]},
            "identityEvidence":{"type":["string","null"]},"identityPage":{"type":["integer","null"]}
          }},
        "items":{"type":"array","items":{"type":"object","additionalProperties":false,
          "required":["originalDescription","amount","nature","type","installmentNumber","installmentCount","section","includedInCurrentTotal","page","evidence","confidence","transactionDate","normalizedDescription","suggestedCategory","categoryConfidence"],
          "properties":{
            "originalDescription":{"type":"string"},"amount":{"type":"string"},
            "transactionDate":{"type":["string","null"]},"normalizedDescription":{"type":["string","null"]},
            "suggestedCategory":{"type":["string","null"]},"categoryConfidence":{"type":["string","null"],"enum":["High","Medium","Low",null]},
            "nature":{"type":"string","enum":["Debit","Credit"]},
            "type":{"type":"string","enum":["Purchase","Installment","Payment","Fee","Credit","Refund","PreviousBalance","FinancedBalance","Unknown"]},
            "installmentNumber":{"type":["integer","null"]},"installmentCount":{"type":["integer","null"]},
            "section":{"type":"string","enum":["CurrentInvoice","FutureInstallments","Payments","PreviousBalance","Fees","Other"]},
            "includedInCurrentTotal":{"type":"boolean"},"page":{"type":"integer"},
            "evidence":{"type":"string"},"confidence":{"type":"string","enum":["High","Medium","Low"]}
          }}}
      }
    }
    """).RootElement.Clone();

    public async Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken cancellationToken)
    {
        if (!options.Enabled || options.Mode == CreditCardStatementAgentMode.Disabled || !string.Equals(options.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            throw new CreditCardStatementAgentUnavailableException("Provider desabilitado.", failureCode: "AgentUnavailable");
        var key = ReadSecret(options.ApiKeyFile, options.ApiKey);
        var client = clients.CreateClient("CreditCardStatementAgent");
        using var message = new HttpRequestMessage(HttpMethod.Post, "responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = JsonContent.Create(new
        {
            model = options.Model,
            store = false,
            input = new object[]
            {
                new { role = "system", content = new[] { new { type = "input_text", text = request.SystemPrompt } } },
                new { role = "user", content = new[] { new { type = "input_text", text = request.DocumentText } } },
            },
            text = new { format = new { type = "json_schema", name = request.SchemaName, strict = true, schema = request.OutputSchema ?? OutputSchema } },
        });
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new CreditCardStatementAgentUnavailableException("Provider recusou a solicitação.", failureCode: await FailureCodeAsync(response, cancellationToken));
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Saldo360.Application.Ai;

namespace Saldo360.Infrastructure.Ai;

/// <summary>
/// Único cliente OpenAI do Saldo360 (Responses API, <c>store:false</c>, saída estruturada estrita). Só envia texto já saneado pelo chamador.
/// Falhas viram códigos curtos de uma lista fechada; a mensagem, o corpo e a credencial do provider nunca são devolvidos nem registrados.
/// </summary>
internal sealed class OpenAiCompletionProvider(
    IHttpClientFactory clients,
    AiAgentOptions options,
    ILogger<OpenAiCompletionProvider> logger,
    string clientName = OpenAiCompletionProvider.ClientName) : IAiCompletionProvider
{
    public const string ClientName = "AiProvider";
    private const int MaxOutputTokens = 12000;
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    public async Task<string> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
    {
        if (!options.Enabled || options.Mode == AiAgentMode.Disabled || !string.Equals(options.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            throw new AiProviderUnavailableException("Provider desabilitado.", failureCode: "AgentUnavailable");
        var key = ReadSecret(options.ApiKeyFile, options.ApiKey);
        var client = clients.CreateClient(clientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, "responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var payload = new Dictionary<string, object>
        {
            ["model"] = request.Model ?? options.Model,
            ["store"] = false,
            ["max_output_tokens"] = MaxOutputTokens,
            ["input"] = new object[]
            {
                new { role = "system", content = new[] { new { type = "input_text", text = request.SystemPrompt } } },
                new { role = "user", content = new[] { new { type = "input_text", text = request.Input } } },
            },
            ["text"] = new { format = new { type = "json_schema", name = request.SchemaName, strict = true, schema = request.OutputSchema } },
        };
        message.Content = JsonContent.Create(payload);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new AiProviderUnavailableException("Provider recusou a solicitação.", failureCode: await FailureCodeAsync(response, cancellationToken));
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bounded = new MemoryStream();
            var chunk = new byte[8192];
            int length;
            while ((length = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (bounded.Length + length > MaxResponseBytes)
                    throw new AiProviderUnavailableException("Limite de resposta excedido.", failureCode: "ProviderResponseLimitExceeded");
                bounded.Write(chunk, 0, length);
            }
            using var json = JsonDocument.Parse(bounded.GetBuffer().AsMemory(0, (int)bounded.Length));
            if (json.RootElement.TryGetProperty("status", out var responseStatus) && responseStatus.GetString() is { } status && status != "completed")
            {
                var truncated = json.RootElement.TryGetProperty("incomplete_details", out var detail) && detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("reason", out var reason) && reason.GetString() == "max_output_tokens";
                throw new AiProviderUnavailableException("Resposta incompleta.", failureCode: truncated ? "ProviderOutputTruncated" : "ProviderIncompleteResponse");
            }
            var outputText = json.RootElement.GetProperty("output").EnumerateArray()
                .SelectMany(item => item.TryGetProperty("content", out var content) ? content.EnumerateArray().ToArray() : [])
                .FirstOrDefault(item => item.TryGetProperty("type", out var type) && type.GetString() == "output_text");
            if (outputText.ValueKind == JsonValueKind.Undefined || !outputText.TryGetProperty("text", out var text) || string.IsNullOrWhiteSpace(text.GetString()))
                throw new AiProviderUnavailableException("Provider não retornou saída estruturada.", failureCode: "ProviderNoStructuredOutput");
            if (request.OnUsage is not null && json.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                request.OnUsage(usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var inputTokens) ? inputTokens : 0,
                    usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var outputTokens) ? outputTokens : 0);
            logger.LogInformation("AI completion finished provider={Provider} model={Model} durationMs={Duration}", "OpenAI", request.Model ?? options.Model, started.ElapsedMilliseconds);
            return text.GetString()!;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new AiProviderUnavailableException("Timeout do provider.", exception, "ProviderTimeout"); }
        catch (HttpRequestException exception)
        { throw new AiProviderUnavailableException("Falha de comunicação com o provider.", exception, "ProviderNetworkFailure"); }
        catch (JsonException) { throw new AiProviderUnavailableException("Resposta inválida do provider.", failureCode: "InvalidProviderResponse"); }
        catch (InvalidOperationException) { throw new AiProviderUnavailableException("Resposta inválida do provider.", failureCode: "InvalidProviderResponse"); }
        catch (KeyNotFoundException) { throw new AiProviderUnavailableException("Resposta inválida do provider.", failureCode: "InvalidProviderResponse"); }
    }

    private static async Task<string> FailureCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        // Lê apenas error.code (limitado); nunca devolve nem registra a mensagem livre do provider.
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
            if (!File.Exists(file)) throw new AiProviderUnavailableException("Arquivo de segredo do provider não encontrado.", failureCode: "ProviderCredentialMissing");
            string value;
            try { value = File.ReadAllText(file).TrimEnd('\r', '\n'); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new AiProviderUnavailableException("Arquivo de segredo indisponível.", failureCode: "ProviderCredentialUnreadable"); }
            if (string.IsNullOrWhiteSpace(value)) throw new AiProviderUnavailableException("Arquivo de segredo do provider está vazio.", failureCode: "ProviderCredentialEmpty");
            return value;
        }
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        throw new AiProviderUnavailableException("Credencial do provider não configurada.");
    }
}

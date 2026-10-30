using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saldo360.Application.Ai;

/// <summary>
/// Contrato único do agente de IA do Saldo360. Hoje serve à classificação de lançamentos (<c>Classification</c>); não conhece documentos,
/// faturas nem e-mail. Há um só cliente (<c>OpenAiCompletionProvider</c>), com a mesma credencial e os mesmos limites operacionais.
/// </summary>
/// <remarks>
/// Os valores de <see cref="AiAgentMode"/> diferentes de <see cref="AiAgentMode.Disabled"/> habilitam o agente; as variantes são
/// mantidas apenas para que a configuração já existente (<c>CreditCardStatementAgent:*</c>) continue válida até a central de automação (R04).
/// </remarks>
public enum AiAgentMode { Disabled, Shadow, AgentPrimary }

/// <summary>
/// Configuração do agente. A seção continua sendo <c>CreditCardStatementAgent</c> (nome histórico, preservado para não invalidar variáveis de
/// ambiente e segredos já provisionados); a renomeação fica para a R04.
/// </summary>
public sealed class AiAgentOptions
{
    public const string SectionName = "CreditCardStatementAgent";

    public bool Enabled { get; set; }
    public AiAgentMode Mode { get; set; } = AiAgentMode.Disabled;
    public string Provider { get; set; } = "OpenAI";
    public string Model { get; set; } = "gpt-4o-mini";
    public string Endpoint { get; set; } = "https://api.openai.com/v1/";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyFile { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Chamada estruturada ao modelo: prompt de sistema, entrada já saneada pelo chamador e esquema JSON estrito da resposta.</summary>
public sealed record AiCompletionRequest(string SystemPrompt, string Input, string SchemaName, JsonElement OutputSchema)
{
    /// <summary>Modelo específico desta chamada. Sem valor, usa o modelo configurado.</summary>
    [JsonIgnore] public string? Model { get; init; }

    /// <summary>Recebe (tokens de entrada, tokens de saída) informados pelo provider; nunca recebe conteúdo.</summary>
    [JsonIgnore] public Action<int, int>? OnUsage { get; init; }
}

public interface IAiCompletionProvider
{
    Task<string> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>Falha do provider com código curto e seguro (nunca a mensagem, o corpo ou a credencial do provider).</summary>
public sealed class AiProviderUnavailableException(string message, Exception? inner = null, string? failureCode = null) : Exception(message, inner)
{
    public string? FailureCode { get; } = failureCode;
}

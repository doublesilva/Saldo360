using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saldo360.Application.Ai;

/// <summary>
/// Contrato único do agente de IA do Saldo360. Hoje serve à classificação de lançamentos (<c>Classification</c>); não conhece documentos,
/// faturas nem e-mail. Há um só cliente (<c>OpenAiCompletionProvider</c>), com a mesma credencial e os mesmos limites operacionais.
/// </summary>
/// <remarks>
/// O agente só é habilitado por <see cref="AiAgentOptions.Enabled"/>; não há modos.
/// </remarks>
public sealed class AiAgentOptions
{
    /// <summary>
    /// Seção de configuração do agente (R04). Substitui a antiga <c>CreditCardStatementAgent</c>, que nomeava um fluxo documental retirado na R00: variáveis
    /// de ambiente e arquivos de configuração provisionados com o nome antigo precisam ser renomeados (<c>AiAgent__*</c>) antes de uma implantação; não há
    /// leitura do nome antigo.
    /// </summary>
    public const string SectionName = "AiAgent";

    public bool Enabled { get; set; }
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

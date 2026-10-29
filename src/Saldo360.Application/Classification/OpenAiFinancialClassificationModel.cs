using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Application.Classification;

/// <summary>
/// Fallback de IA para classificação de movimentações. Não é um agente novo nem um novo cliente OpenAI: reaproveita o
/// <see cref="ICreditCardStatementAgentProvider"/> já usado pelas demais rotinas (mesma credencial, mesmo cliente HTTP, Responses API com
/// <c>store:false</c> e saída estruturada estrita), com prompt, schema e modelo próprios. O texto enviado já vem saneado pelo chamador.
/// </summary>
public sealed class OpenAiFinancialClassificationModel(ICreditCardStatementAgentProvider provider, FinancialClassificationOptions options, CreditCardStatementAgentOptions agent) : IFinancialClassificationModel
{
    public const string SchemaName = "financial_movement_classification";

    public const string SystemPrompt = """
        Você é um consultor financeiro. Está recebendo uma lista de categorias que já existem no sistema (categories) e uma lista de lançamentos
        ainda não categorizados (items). Para cada lançamento, verifique se ele se encaixa em alguma das categorias atuais: se sim, relacione o
        lançamento à categoria usando o categoryCode (somente um código da lista cujo type seja igual ao kind do item, Income ou Expense).
        Se nenhuma categoria atual servir, sugira uma NOVA categoria em suggestedCategoryName: um nome curto e genérico, reutilizável por outros
        lançamentos (1 a 3 palavras, em português, sem números, sem nome de empresa ou pessoa), e deixe categoryCode null. Nunca invente códigos.
        Se faltar evidência até para sugerir uma categoria, use categoryCode null, suggestedCategoryName null e needsReview true; é melhor pedir revisão do que errar.
        O texto foi saneado (documentos, números e dados pessoais foram removidos): não tente reconstruí-los. pluggyCategory é só uma dica do banco e pode estar errada.
        Não decida se algo é transferência, pagamento de fatura ou estorno: isso já foi decidido antes e esses itens não são enviados.
        reason: justificativa de até 12 palavras, sem dados pessoais. confidence: número de 0 a 1 que indica sua segurança, não uma probabilidade calibrada.
        Responda uma entrada em results para cada item, com o mesmo índice i.
        """;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["results"],
         "properties":{"results":{"type":"array","items":{"type":"object","additionalProperties":false,
           "required":["i","categoryCode","suggestedCategoryName","confidence","needsReview","reason"],
           "properties":{"i":{"type":"integer"},"categoryCode":{"type":["integer","null"]},"suggestedCategoryName":{"type":["string","null"]},"confidence":{"type":"number"},
                         "needsReview":{"type":"boolean"},"reason":{"type":"string"}}}}}}
        """).RootElement.Clone();

    public bool IsAvailable => options.AiEnabled && agent.Enabled && agent.Mode != CreditCardStatementAgentMode.Disabled
        && string.Equals(agent.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase)
        && (!string.IsNullOrWhiteSpace(agent.ApiKey) || !string.IsNullOrWhiteSpace(agent.ApiKeyFile));

    public string ModelName => options.Model;

    public async Task<FinancialModelResponse> ClassifyAsync(FinancialModelRequest request, CancellationToken cancellationToken)
    {
        // Somente códigos temporários, o tipo financeiro, a operação, a categoria externa e a descrição saneada: nada de ids, valores, datas ou contas.
        var payload = JsonSerializer.Serialize(new
        {
            categories = request.Categories.Select(c => new { code = c.Code, name = c.Name, type = c.Type.ToString() }),
            items = request.Items.Select(i => new { i = i.Index, kind = i.Kind.ToString(), operation = i.OperationType, pluggyCategory = i.ExternalCategory, text = i.Text }),
        });
        int input = 0, output = 0;
        string text;
        try
        {
            text = await provider.CompleteAsync(new(SystemPrompt, payload)
            {
                SchemaName = SchemaName, OutputSchema = Schema, Model = options.Model, OnUsage = (i, o) => { input = i; output = o; },
            }, cancellationToken);
        }
        catch (CreditCardStatementAgentUnavailableException exception)
        {
            var code = exception.FailureCode ?? "AgentUnavailable";
            throw new FinancialClassificationModelException(code, Retryable(code));
        }
        return new(Parse(text), input, output);
    }

    private static bool Retryable(string code) => code is "ProviderRateLimited" or "ProviderTimeout" or "ProviderNetworkFailure" or "ProviderHttpFailure" or "ProviderIncompleteResponse";

    private static IReadOnlyList<FinancialModelDecision> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new FinancialClassificationModelException("InvalidProviderResponse", false);
            var decisions = new List<FinancialModelDecision>();
            foreach (var item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("i", out var index) || !index.TryGetInt32(out var i)) continue;
                int? code = item.TryGetProperty("categoryCode", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var parsed) ? parsed : null;
                var confidence = item.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number && conf.TryGetDouble(out var d) ? d : 0d;
                var review = !item.TryGetProperty("needsReview", out var nr) || nr.ValueKind != JsonValueKind.False;
                var reason = item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                var suggested = item.TryGetProperty("suggestedCategoryName", out var sg) && sg.ValueKind == JsonValueKind.String ? sg.GetString() : null;
                decisions.Add(new(i, code, confidence, review, reason, suggested));
            }
            return decisions;
        }
        catch (JsonException) { throw new FinancialClassificationModelException("InvalidProviderResponse", false); }
    }
}

using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Classification;

/// <summary>Natureza do movimento a classificar. Só receita e despesa têm categoria: transferências e pagamentos de fatura não são classificados.</summary>
public enum FinancialMovementKind { Income, Expense }

/// <summary>De onde veio a categoria. Persistida no rastro de cada origem (hoje <c>PluggyPosting</c>).</summary>
public enum FinancialClassificationSource { Manual, Rule, Pluggy, AI }

/// <summary>
/// Movimento financeiro normalizado, independente da origem (Pluggy, OFX/CSV, fatura por OCR, comprovante...). <paramref name="Key"/> só serve para
/// o chamador casar a resposta com a entrada. <paramref name="ManualCategoryId"/> é a escolha do usuário: quando existe, nada é recalculado.
/// </summary>
public sealed record FinancialClassificationInput(string Key, string Description, decimal Amount, DateOnly Date, FinancialMovementKind Kind,
    string? ExternalCategory, string? OperationType, string? Source, Guid? ManualCategoryId = null, bool IsCardPurchase = false);

/// <summary>
/// Resultado para uma entrada. <c>CategoryId</c> preenchido = categoria existente escolhida. <c>ProposedName</c> preenchido = o catálogo aponta uma
/// categoria que o orçamento ainda não tem (quem persiste cria, uma única vez). Sem nenhum dos dois, não há categoria segura; <c>NeedsReview</c>
/// diz se o usuário precisa decidir (falso quando a regra determina que o movimento não é consumo e não deve ter categoria).
/// </summary>
public sealed record FinancialClassificationOutcome(string Key, Guid? CategoryId, FinancialClassificationSource? Source, string? ProposedName,
    CategoryType? ProposedType, bool NeedsReview, string? Reason, string Method)
{
    public bool HasCategory => CategoryId is not null;
    public bool NeedsAi => CategoryId is null && ProposedName is null && Source is null;
}

/// <summary>Consumo da IA numa execução: o que o chamador mostra ao usuário e usa para limitar o custo.</summary>
public sealed record FinancialClassificationUsage(int Calls, int Items, int InputTokens, int OutputTokens, decimal EstimatedCostUsd,
    string? StoppedBecause, string? FailureCode, string? Model);

/// <summary>Resposta cru-validada do modelo para um item (código do menu enviado, nunca identificador interno).</summary>
public sealed record FinancialModelDecision(int Index, int? CategoryCode, double Confidence, bool NeedsReview, string? Reason, string? SuggestedCategory = null);

public sealed record FinancialModelItem(int Index, FinancialMovementKind Kind, string Text, string? OperationType, string? ExternalCategory);

public sealed record FinancialModelCategory(int Code, string Name, CategoryType Type);

public sealed record FinancialModelRequest(IReadOnlyList<FinancialModelItem> Items, IReadOnlyList<FinancialModelCategory> Categories);

public sealed record FinancialModelResponse(IReadOnlyList<FinancialModelDecision> Decisions, int InputTokens, int OutputTokens);

/// <summary>Falha controlada do modelo (indisponível, timeout, limite, resposta inválida). O código é estável e não contém dados.</summary>
public sealed class FinancialClassificationModelException(string code, bool retryable) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>Transporte do fallback de IA. A implementação padrão reaproveita o provider OpenAI já existente; testes usam um substituto sem rede.</summary>
public interface IFinancialClassificationModel
{
    /// <summary>Verdadeiro quando há configuração suficiente para tentar a IA (habilitada e com credencial). Não faz chamada externa.</summary>
    bool IsAvailable { get; }
    string ModelName { get; }
    Task<FinancialModelResponse> ClassifyAsync(FinancialModelRequest request, CancellationToken cancellationToken);
}

public sealed class FinancialClassificationOptions
{
    public const string SectionName = "FinancialClassification";
    /// <summary>A IA só roda com esta chave ligada, a credencial do provider presente e a confirmação explícita do usuário na chamada.</summary>
    public bool AiEnabled { get; set; }
    public string Model { get; set; } = "gpt-6.1-sol";
    public int BatchSize { get; set; } = 20;
    public int MaxItemsPerRun { get; set; } = 200;
    public int MaxEstimatedInputTokensPerRun { get; set; } = 60000;
    public decimal MaxEstimatedCostUsdPerRun { get; set; } = 0.50m;
    public double MinConfidence { get; set; } = 0.80;
    /// <summary>
    /// A IA pode SUGERIR uma categoria nova quando nenhuma existente serve, mas a sugestão nunca cadastra nada: com nome válido ela só é registrada
    /// como motivo da revisão ("Sugestão de categoria nova, não cadastrada: X") e o lançamento continua sem categoria até o usuário decidir.
    /// </summary>
    public bool AllowCategorySuggestions { get; set; } = true;
    public int MaxRetries { get; set; } = 2;
    public int RetryDelayMilliseconds { get; set; } = 1500;
    /// <summary>
    /// Preços (USD por 1 milhão de tokens) usados só para estimar o custo mostrado ao usuário. Os valores padrão são placeholders do modelo
    /// anterior: ajuste-os à tabela vigente do modelo configurado, senão a estimativa não reflete a cobrança real.
    /// </summary>
    public decimal InputUsdPerMillionTokens { get; set; } = 0.15m;
    public decimal OutputUsdPerMillionTokens { get; set; } = 0.60m;
}

/// <summary>Categoria existente do orçamento, como o classificador enxerga (reaproveita o tipo já usado pelo mapeamento da Pluggy).</summary>
public static class FinancialCategoryMatching
{
    /// <summary>Texto ASCII em minúsculas, sem acento, pontuação nem espaços repetidos: "Alimentação " e "alimentacao" são a mesma categoria.</summary>
    public static string Key(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = PluggyFinancialMapper.Normalize(text);
        var builder = new System.Text.StringBuilder(normalized.Length);
        foreach (var c in normalized) if (char.IsLetterOrDigit(c)) builder.Append(c); else if (c == ' ') builder.Append(' ');
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Categoria disponível (ela e a principal ativas) com esse nome e tipo. A principal vence a subcategoria homônima; entre subcategorias
    /// homônimas de principais diferentes a escolha seria um palpite, então não há correspondência segura (null).
    /// </summary>
    public static MappableCategory? Find(IReadOnlyCollection<MappableCategory> categories, string name, CategoryType type)
    {
        var key = Key(name);
        var matches = categories.Where(c => c.IsAvailable && c.Type == type && Key(c.Name) == key).ToList();
        return matches.FirstOrDefault(c => !c.IsSubcategory) ?? (matches.Count == 1 ? matches[0] : null);
    }

    /// <summary>Existe (ativa ou não) uma categoria equivalente? Criar outra violaria a unicidade por nome e tipo.</summary>
    public static bool ExistsInAnyState(IReadOnlyCollection<MappableCategory> categories, string name, CategoryType type) =>
        categories.Any(c => c.Type == type && Key(c.Name) == Key(name));
}

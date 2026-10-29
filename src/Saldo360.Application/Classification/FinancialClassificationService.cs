using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Classification;

public sealed record FinancialAiClassification(IReadOnlyList<FinancialClassificationOutcome> Outcomes, FinancialClassificationUsage Usage);

/// <summary>
/// Classificação financeira única e reutilizável (Pluggy hoje; OFX/CSV, OCR de fatura, comprovantes e outros conectores depois). A ordem é
/// sempre: escolha manual → regras do domínio → categoria externa (catálogo) → IA como último recurso. O serviço é puro quanto a persistência:
/// quem chama grava o resultado, cria as categorias propostas e preserva o que o usuário já decidiu.
/// </summary>
public interface IFinancialClassificationService
{
    /// <summary>Etapas locais (manual, regras e catálogo). Não faz nenhuma chamada externa.</summary>
    IReadOnlyList<FinancialClassificationOutcome> ClassifyLocal(IReadOnlyList<FinancialClassificationInput> inputs, IReadOnlyCollection<MappableCategory> categories);

    /// <summary>
    /// Fallback de IA somente para as entradas que as etapas locais deixaram sem categoria. Aplica sanitização, limites por execução, tentativas
    /// limitadas e validação estrita da resposta: nunca devolve uma categoria que não seja existente, ativa e do tipo certo.
    /// </summary>
    Task<FinancialAiClassification> ClassifyWithAiAsync(IReadOnlyList<FinancialClassificationInput> inputs, IReadOnlyCollection<MappableCategory> categories, CancellationToken cancellationToken);
}

public sealed class FinancialClassificationService(IFinancialClassificationModel model, FinancialClassificationOptions options) : IFinancialClassificationService
{
    public const string SkippedMethod = "skipped";
    private const int MaxReasonLength = 120;

    public IReadOnlyList<FinancialClassificationOutcome> ClassifyLocal(IReadOnlyList<FinancialClassificationInput> inputs, IReadOnlyCollection<MappableCategory> categories)
    {
        var result = new List<FinancialClassificationOutcome>(inputs.Count);
        foreach (var input in inputs)
        {
            var type = input.Kind == FinancialMovementKind.Income ? CategoryType.Income : CategoryType.Expense;
            if (input.ManualCategoryId is { } manual)
            {
                result.Add(new(input.Key, manual, FinancialClassificationSource.Manual, null, null, false, "Categoria escolhida pelo usuário", "manual"));
                continue;
            }
            if (FinancialClassificationRules.Evaluate(input) is { } rule)
            {
                result.Add(new(input.Key, null, FinancialClassificationSource.Rule, null, null, rule.NeedsReview, rule.Reason, FinancialClassificationVersions.Rules));
                continue;
            }
            if (FinancialExternalCategoryCatalog.ResolveExisting(input.ExternalCategory, type, categories) is { } existing)
            {
                result.Add(new(input.Key, existing, FinancialClassificationSource.Pluggy, null, null, false, "Categoria informada pelo banco", FinancialClassificationVersions.Catalog));
                continue;
            }
            if (FinancialExternalCategoryCatalog.ProposeNewName(input.ExternalCategory, type, categories) is { } proposal)
            {
                result.Add(new(input.Key, null, FinancialClassificationSource.Pluggy, proposal, type, false, "Categoria informada pelo banco", FinancialClassificationVersions.Catalog));
                continue;
            }
            result.Add(new(input.Key, null, null, null, null, true, "Sem categoria segura", FinancialClassificationVersions.Rules));
        }
        return result;
    }

    public async Task<FinancialAiClassification> ClassifyWithAiAsync(IReadOnlyList<FinancialClassificationInput> inputs, IReadOnlyCollection<MappableCategory> categories, CancellationToken cancellationToken)
    {
        var outcomes = new List<FinancialClassificationOutcome>(inputs.Count);
        var candidates = new List<(FinancialClassificationInput Input, string Text)>();
        foreach (var input in inputs)
        {
            if (!FinancialDescriptionSanitizer.TrySanitize(input.Description, input.OperationType, input.ExternalCategory, input.IsCardPurchase, out var text))
            {
                outcomes.Add(new(input.Key, null, null, null, null, true, "Descrição não pode ser anonimizada: revisão manual", FinancialClassificationVersions.Rules));
                continue;
            }
            var type = input.Kind == FinancialMovementKind.Income ? CategoryType.Income : CategoryType.Expense;
            if (!categories.Any(c => c.IsActive && c.Type == type))
            {
                outcomes.Add(new(input.Key, null, null, null, null, true, "Não há categoria elegível desse tipo", FinancialClassificationVersions.Rules));
                continue;
            }
            candidates.Add((input, text));
        }

        var model_ = model.ModelName;
        var method = FinancialClassificationVersions.Ai(model_);
        int calls = 0, sent = 0, inputTokens = 0, outputTokens = 0;
        string? stopped = null, failure = null;
        if (!model.IsAvailable || !options.AiEnabled)
        {
            outcomes.AddRange(candidates.Select(c => Skipped(c.Input.Key, "IA indisponível: classificação local apenas")));
            return new(outcomes, new(0, 0, 0, 0, 0m, "AiUnavailable", null, null));
        }

        // "Outros" é o marcador de "sem categoria" das compras de cartão: oferecê-lo à IA só estimularia a resposta preguiçosa.
        var eligible = categories.Where(c => c.IsActive && FinancialCategoryMatching.Key(c.Name) != "outros").OrderBy(c => c.Type).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var codes = eligible.Select((c, i) => new FinancialModelCategory(i + 1, c.Name, c.Type)).ToList();
        var codeToCategory = codes.ToDictionary(c => c.Code, c => eligible[c.Code - 1]);

        var remaining = candidates.Take(Math.Max(0, options.MaxItemsPerRun)).ToList();
        foreach (var skipped in candidates.Skip(remaining.Count)) outcomes.Add(Skipped(skipped.Input.Key, "Limite de itens da execução"));
        if (candidates.Count > remaining.Count) stopped = "MaxItemsPerRun";

        var newNames = new HashSet<string>(StringComparer.Ordinal);
        var batchSize = Math.Clamp(options.BatchSize, 1, 50);
        for (var offset = 0; offset < remaining.Count; offset += batchSize)
        {
            var batch = remaining.Skip(offset).Take(batchSize).ToList();
            if (stopped is not null && stopped != "MaxItemsPerRun") { outcomes.AddRange(batch.Select(b => Skipped(b.Input.Key, "Execução interrompida"))); continue; }

            var items = batch.Select((b, i) => new FinancialModelItem(i, b.Input.Kind, b.Text, b.Input.OperationType, b.Input.ExternalCategory)).ToList();
            var request = new FinancialModelRequest(items, codes);
            var estimated = EstimateInputTokens(request);
            if (inputTokens + estimated > options.MaxEstimatedInputTokensPerRun)
            { stopped = "MaxTokensPerRun"; outcomes.AddRange(batch.Select(b => Skipped(b.Input.Key, "Limite de tokens da execução"))); continue; }
            if (EstimateCost(inputTokens + estimated, outputTokens) > options.MaxEstimatedCostUsdPerRun)
            { stopped = "MaxCostPerRun"; outcomes.AddRange(batch.Select(b => Skipped(b.Input.Key, "Limite de custo da execução"))); continue; }

            FinancialModelResponse? response = null;
            for (var attempt = 0; attempt <= Math.Max(0, options.MaxRetries); attempt++)
            {
                try { calls++; response = await model.ClassifyAsync(request, cancellationToken); break; }
                catch (FinancialClassificationModelException exception)
                {
                    failure = exception.Code;
                    if (!exception.Retryable || attempt >= options.MaxRetries) break;
                    await Task.Delay(options.RetryDelayMilliseconds * (attempt + 1), cancellationToken);
                }
            }
            if (response is null)
            {
                stopped = "ProviderFailure";
                outcomes.AddRange(batch.Select(b => Skipped(b.Input.Key, "IA indisponível no momento")));
                continue;
            }

            sent += batch.Count; inputTokens += response.InputTokens; outputTokens += response.OutputTokens;
            var byIndex = new Dictionary<int, FinancialModelDecision>();
            foreach (var decision in response.Decisions) if (decision.Index >= 0 && decision.Index < batch.Count) byIndex.TryAdd(decision.Index, decision);
            for (var i = 0; i < batch.Count; i++)
            {
                var input = batch[i].Input;
                outcomes.Add(byIndex.TryGetValue(i, out var d) ? Validate(input, d, codeToCategory, categories, newNames, method) : Review(input.Key, "Sem resposta válida da IA", method));
            }
        }

        return new(outcomes, new(calls, sent, inputTokens, outputTokens, EstimateCost(inputTokens, outputTokens), stopped, failure, model_));
    }

    private FinancialClassificationOutcome Validate(FinancialClassificationInput input, FinancialModelDecision decision, Dictionary<int, MappableCategory> codeToCategory,
        IReadOnlyCollection<MappableCategory> categories, HashSet<string> newNames, string method)
    {
        var expected = input.Kind == FinancialMovementKind.Income ? CategoryType.Income : CategoryType.Expense;
        var reason = SafeReason(decision.Reason);
        if (decision.NeedsReview) return Review(input.Key, reason ?? "IA sem evidência suficiente", method);
        if (double.IsNaN(decision.Confidence) || decision.Confidence < options.MinConfidence) return Review(input.Key, reason ?? "Confiança insuficiente", method);
        if (decision.CategoryCode is { } code)
        {
            // Categoria inexistente, inativa ou de outro tipo: rejeitada. A IA nunca escapa do menu enviado.
            if (!codeToCategory.TryGetValue(code, out var category) || !category.IsActive || category.Type != expected)
                return Review(input.Key, "Categoria inválida ou incompatível com o tipo", method);
            return new(input.Key, category.Id, FinancialClassificationSource.AI, null, null, false, reason ?? "Classificado pela IA", method);
        }
        return options.AllowCategorySuggestions && SuggestedName(decision.SuggestedCategory) is { } suggestion
            ? Suggest(input, suggestion, expected, categories, newNames, reason, method)
            : Review(input.Key, reason ?? "IA sem evidência suficiente", method);
    }

    /// <summary>
    /// Sugestão de categoria nova da IA. Se já existe equivalente ativa, usa-a; se existe equivalente inativa (o usuário a desativou), não recria.
    /// Só vira proposta (criada pelo backend) com nome válido e dentro do limite de novas categorias da execução.
    /// </summary>
    private FinancialClassificationOutcome Suggest(FinancialClassificationInput input, string name, CategoryType type, IReadOnlyCollection<MappableCategory> categories,
        HashSet<string> newNames, string? reason, string method)
    {
        if (FinancialCategoryMatching.Find(categories, name, type) is { } existing)
            return new(input.Key, existing.Id, FinancialClassificationSource.AI, null, null, false, reason ?? "Sugestão equivalente a categoria existente", method);
        if (FinancialCategoryMatching.ExistsInAnyState(categories, name, type)) return Review(input.Key, "Categoria equivalente está desativada", method);
        var key = type + ":" + FinancialCategoryMatching.Key(name);
        if (!newNames.Contains(key) && newNames.Count >= Math.Max(0, options.MaxNewCategoriesPerRun)) return Review(input.Key, "Limite de novas categorias da execução", method);
        newNames.Add(key);
        return new(input.Key, null, FinancialClassificationSource.AI, name, type, false, reason ?? "Nova categoria sugerida pela IA", method);
    }

    private static readonly HashSet<string> GenericNames = new(StringComparer.Ordinal)
        { "outros", "outras", "diversos", "diversas", "geral", "categoria", "sem categoria", "nao categorizado", "indefinido", "desconhecido", "variados", "misc" };

    /// <summary>Nome seguro para uma categoria nova: curto, só letras, sem dígitos/e-mail/símbolos, não genérico. Devolve null se recusado.</summary>
    internal static string? SuggestedName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var name = string.Join(' ', raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!FinancialCategory.TryNormalizeName(name, out var normalized, out _) || normalized.Length is < 3 or > 40) return null;
        if (normalized.Split(' ').Length > 4 || normalized.Any(c => !(char.IsLetter(c) || c is ' ' or '-' or '/' or '&'))) return null;
        if (GenericNames.Contains(FinancialCategoryMatching.Key(normalized))) return null;
        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }

    private static FinancialClassificationOutcome Review(string key, string reason, string method) =>
        new(key, null, null, null, null, true, reason, method);

    private static FinancialClassificationOutcome Skipped(string key, string reason) =>
        new(key, null, null, null, null, false, reason, SkippedMethod);

    private static string? SafeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var cleaned = new string(reason.Where(c => !char.IsControl(c) && !char.IsDigit(c)).ToArray()).Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length > MaxReasonLength ? cleaned[..MaxReasonLength] : cleaned;
    }

    private static int EstimateInputTokens(FinancialModelRequest request) =>
        400 + request.Categories.Sum(c => c.Name.Length / 3 + 8) + request.Items.Sum(i => (i.Text.Length + (i.OperationType?.Length ?? 0) + (i.ExternalCategory?.Length ?? 0)) / 3 + 24);

    private decimal EstimateCost(int inputTokens, int outputTokens) =>
        Math.Round(inputTokens / 1_000_000m * options.InputUsdPerMillionTokens + outputTokens / 1_000_000m * options.OutputUsdPerMillionTokens, 6);
}

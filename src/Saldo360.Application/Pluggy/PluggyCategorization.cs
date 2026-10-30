using Saldo360.Application.Classification;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

/// <summary>Lançamento (receita/despesa bancária ou compra de cartão) gerado pela Pluggy e o que se sabe sobre sua categoria hoje.</summary>
public sealed class PluggyCategorizationCandidate
{
    public Guid PostingId { get; init; }
    public PluggyPostingKind Kind { get; init; }
    public bool IsIncome { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public DateOnly Date { get; init; }
    public string? ExternalCategory { get; init; }
    public string? OperationType { get; init; }
    /// <summary>Categoria atual no núcleo financeiro (lançamento ou compra). Se diferir do rastro, alguém a alterou pelas telas.</summary>
    public Guid? CurrentCategoryId { get; init; }
    /// <summary>A categoria atual é o marcador "Outros" que a compra de cartão recebe por não poder ficar sem categoria.</summary>
    public bool CurrentIsFallback { get; init; }
    public Guid? PostingCategoryId { get; init; }
    public PluggyCategorySource Source { get; init; }
    public bool NeedsReview { get; init; }
    public DateTimeOffset? ClassifiedAt { get; init; }
    public string? MethodVersion { get; init; }
    public string? Reason { get; init; }

    public bool IsCardPurchase => Kind == PluggyPostingKind.CardPurchase;
    public bool AiAttempted => MethodVersion?.StartsWith("ai:", StringComparison.Ordinal) == true;

    /// <summary>
    /// Categoria definida pelo usuário: escolha registrada no rastro ou alteração direta no lançamento/compra (a categoria atual não é a que a
    /// integração aplicou). É preservada sempre, inclusive em novas sincronizações e execuções.
    /// </summary>
    public bool IsManual => Source == PluggyCategorySource.User
        || (CurrentCategoryId is { } current && current != PostingCategoryId && !(CurrentIsFallback && PostingCategoryId is null));

    /// <summary>Categoria que a integração aplicou (ou o próprio rastro dela).</summary>
    public bool HasAutomaticCategory => !IsManual && PostingCategoryId is not null && Source != PluggyCategorySource.None;
}

/// <summary>Decisão a persistir: categoria existente (<see cref="CategoryId"/>), categoria canônica a criar (<see cref="ProposedName"/>) ou nenhuma (apenas registra a avaliação).</summary>
public sealed record PluggyCategoryDecision(Guid PostingId, Guid? CategoryId, string? ProposedName, CategoryType? ProposedType, PluggyCategorySource Source,
    string Method, string? Reason, bool NeedsReview, Guid? ExpectedCurrentCategoryId);

public sealed record PluggyCategorizationApplied(int Applied, int Recorded, int SkippedChanged, int CategoriesCreated);

/// <summary>Lançamentos Pluggy por origem da categoria. <c>Rule</c> = regra determinou que não é consumo (sem categoria, sem pendência); <c>NeedsReview</c> = sem categoria segura; <c>NotEvaluated</c> = ainda não avaliados.</summary>
public sealed record PluggyCategorizationCounts(int Total, int Manual, int Pluggy, int Rule, int Ai, int NeedsReview, int NotEvaluated);

public interface IPluggyCategorizationRepository
{
    Task<IReadOnlyList<PluggyCategorizationCandidate>> ListCandidatesAsync(Guid workspaceId, CancellationToken ct);
    Task<IReadOnlyList<MappableCategory>> ListCategoriesAsync(Guid workspaceId, CancellationToken ct);
    /// <summary>Cria, numa transação serializada por orçamento, as categorias canônicas que ainda não existem (em nenhum estado). Devolve quantas criou.</summary>
    Task<int> EnsureCategoriesAsync(Guid workspaceId, Guid userId, IReadOnlyCollection<(string Name, CategoryType Type)> categories, DateTimeOffset now, CancellationToken ct);
    /// <summary>
    /// Persiste as decisões numa transação serializada por orçamento, revalidando cada uma: categoria do usuário ou alterada depois da leitura é
    /// preservada; categoria inexistente, de outro orçamento, inativa ou de tipo incompatível é recusada. Nunca altera valores, datas, contas ou saldos.
    /// </summary>
    Task<PluggyCategorizationApplied> ApplyAsync(Guid workspaceId, Guid userId, IReadOnlyList<PluggyCategoryDecision> decisions, DateTimeOffset now, CancellationToken ct);
    Task<PluggyCategorizationCounts> CountAsync(Guid workspaceId, CancellationToken ct);
}

public sealed record PluggyCategorizationRequest(bool UseAi, bool ConfirmExternal, bool Force);

public sealed record PluggyCategorizationAiStatus(bool Available, string? Model, string? Reason);

public sealed record PluggyCategorizationOverview(PluggyCategorizationCounts Counts, PluggyCategorizationAiStatus Ai);

public sealed record PluggyCategorizationResult(int Considered, int Manual, int Pluggy, int Rule, int Ai, int Unresolved, int CategoriesCreated, int SkippedChanged,
    int AiItemsSent, int AiCalls, int InputTokens, int OutputTokens, decimal EstimatedCostUsd, string? AiStoppedBecause, string? AiFailure, string? Model,
    PluggyCategorizationCounts Counts);

public enum PluggyCategorizationOutcome { Ok, ConfirmationRequired, AiUnavailable }

public sealed record PluggyCategorizationRun(PluggyCategorizationOutcome Outcome, PluggyCategorizationResult? Result);

public interface IPluggyCategorizationService
{
    Task<PluggyCategorizationOverview> GetOverviewAsync(Guid workspaceId, CancellationToken ct);
    Task<PluggyCategorizationRun> RunAsync(Guid workspaceId, Guid userId, PluggyCategorizationRequest request, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// Categorização dos lançamentos que a Pluggy gerou (retroativa e a qualquer momento, sempre sob demanda). Ordem fixa: preserva escolha manual →
/// regras do domínio → categoria da Pluggy (criando a categoria canônica só quando o catálogo é inequívoco) → IA somente para o que restou, e só
/// com confirmação explícita. Falha da IA nunca desfaz o que foi classificado localmente nem afeta a sincronização.
/// </summary>
public sealed class PluggyCategorizationService(IPluggyCategorizationRepository repository, IFinancialClassificationService classifier, IFinancialClassificationModel model) : IPluggyCategorizationService
{
    public async Task<PluggyCategorizationOverview> GetOverviewAsync(Guid workspaceId, CancellationToken ct) =>
        new(await repository.CountAsync(workspaceId, ct), new(model.IsAvailable, model.IsAvailable ? model.ModelName : null, model.IsAvailable ? null : "AiNotConfigured"));

    public async Task<PluggyCategorizationRun> RunAsync(Guid workspaceId, Guid userId, PluggyCategorizationRequest request, DateTimeOffset now, CancellationToken ct)
    {
        if (request.UseAi && !request.ConfirmExternal) return new(PluggyCategorizationOutcome.ConfirmationRequired, null);

        var candidates = await repository.ListCandidatesAsync(workspaceId, ct);
        var manual = candidates.Count(c => c.IsManual);
        // Reavalia o que ainda não tem categoria da integração; com "force", também o que a integração já havia classificado (nunca o manual).
        var targets = candidates.Where(c => !c.IsManual && (request.Force || !c.HasAutomaticCategory)).ToList();

        var categories = await repository.ListCategoriesAsync(workspaceId, ct);
        var created = 0;
        var local = Classify(targets, categories);
        var proposals = local.Where(o => o.ProposedName is not null).Select(o => (o.ProposedName!, o.ProposedType!.Value)).Distinct().ToList();
        if (proposals.Count > 0)
        {
            created = await repository.EnsureCategoriesAsync(workspaceId, userId, proposals, now, ct);
            categories = await repository.ListCategoriesAsync(workspaceId, ct);
            local = Classify(targets, categories);
        }

        var byKey = targets.ToDictionary(t => t.PostingId.ToString("N"));
        var final = local.ToDictionary(o => o.Key);
        FinancialAiClassification? ai = null;
        var usage = new FinancialClassificationUsage(0, 0, 0, 0, 0m, null, null, null);
        if (request.UseAi)
        {
            // Só vai à IA o que continua sem categoria e sem regra, e que ainda não foi tentado (salvo "force").
            var pending = local.Where(o => o.NeedsAi || (o.Source is null && o.NeedsReview)).Where(o => request.Force || !byKey[o.Key].AiAttempted).ToList();
            if (!model.IsAvailable)
                usage = new(0, 0, 0, 0, 0m, "AiUnavailable", null, null);
            else if (pending.Count > 0)
            {
                ai = await classifier.ClassifyWithAiAsync(pending.Select(o => ToInput(byKey[o.Key])).ToList(), categories, ct);
                usage = ai.Usage;
                foreach (var outcome in ai.Outcomes) final[outcome.Key] = outcome;
            }
        }

        // Sugestão de categoria nova da IA não é cadastro autorizado: o classificador já a devolve como revisão e nada é criado aqui.

        var decisions = new List<PluggyCategoryDecision>();
        int byPluggy = 0, byRule = 0, byAi = 0, unresolved = 0;
        foreach (var (key, outcome) in final)
        {
            if (outcome.Method == FinancialClassificationService.SkippedMethod) continue; // não tentado: continua elegível numa próxima execução
            var candidate = byKey[key];
            // Reavaliar nunca remove uma categoria que a integração já aplicou: sem resultado novo, o que existe permanece.
            if (outcome.CategoryId is null && candidate.HasAutomaticCategory) continue;
            var source = outcome.Source switch
            {
                FinancialClassificationSource.Pluggy => PluggyCategorySource.Pluggy,
                FinancialClassificationSource.Rule => PluggyCategorySource.Rule,
                FinancialClassificationSource.AI => PluggyCategorySource.AI,
                _ => PluggyCategorySource.Rule,
            };
            var needsReview = outcome.CategoryId is null && outcome.NeedsReview;
            if (Unchanged(candidate, outcome, source, needsReview)) continue;
            switch (outcome)
            {
                case { CategoryId: not null, Source: FinancialClassificationSource.Pluggy }: byPluggy++; break;
                case { CategoryId: not null, Source: FinancialClassificationSource.AI }: byAi++; break;
                case { Source: FinancialClassificationSource.Rule }: byRule++; break;
                default: unresolved++; break;
            }
            decisions.Add(new(candidate.PostingId, outcome.CategoryId, null, null, source, outcome.Method, outcome.Reason, needsReview, candidate.CurrentCategoryId));
        }
        var applied = decisions.Count == 0 ? new PluggyCategorizationApplied(0, 0, 0, 0) : await repository.ApplyAsync(workspaceId, userId, decisions, now, ct);

        var counts = await repository.CountAsync(workspaceId, ct);
        return new(PluggyCategorizationOutcome.Ok, new(targets.Count, manual, byPluggy, byRule, byAi, unresolved,
            created, applied.SkippedChanged, usage.Items, usage.Calls, usage.InputTokens, usage.OutputTokens, usage.EstimatedCostUsd, usage.StoppedBecause, usage.FailureCode, usage.Model, counts));
    }

    private IReadOnlyList<FinancialClassificationOutcome> Classify(IReadOnlyList<PluggyCategorizationCandidate> targets, IReadOnlyCollection<MappableCategory> categories) =>
        classifier.ClassifyLocal(targets.Select(ToInput).ToList(), categories);

    private static FinancialClassificationInput ToInput(PluggyCategorizationCandidate c) =>
        new(c.PostingId.ToString("N"), c.Description, c.Amount, c.Date, c.IsIncome ? FinancialMovementKind.Income : FinancialMovementKind.Expense, c.ExternalCategory, c.OperationType, "Pluggy",
            IsCardPurchase: c.IsCardPurchase);

    /// <summary>Evita reescrever (e mudar a data de avaliação) quando o resultado é igual ao que já está gravado.</summary>
    private static bool Unchanged(PluggyCategorizationCandidate c, FinancialClassificationOutcome outcome, PluggyCategorySource source, bool needsReview) =>
        outcome.CategoryId == c.PostingCategoryId && c.ClassifiedAt is not null && c.MethodVersion == outcome.Method && c.NeedsReview == needsReview
        && c.Reason == outcome.Reason && (outcome.CategoryId is null ? c.Source == PluggyCategorySource.None : c.Source == source);
}

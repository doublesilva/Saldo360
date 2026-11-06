using Saldo360.Application.Workspaces;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Budgets;

// T12.1: leitura e alteração do orçamento mensal recorrente. Ler nunca grava (nenhum orçamento mensal é criado por consulta). Alterar acrescenta ao log de regras
// (FinancialBudgetRuleChange), que é append-only: "somente este mês" cria uma exceção; "este mês e os próximos" cria uma nova versão do padrão a partir da competência.

public enum EffectiveAccess { Allowed, NotFound, Invalid, Conflict }

public sealed record EffectiveResult<T>(EffectiveAccess Access, T? Value, IDictionary<string, string[]>? Errors, string? Message = null) where T : class
{
    public static EffectiveResult<T> NotFound { get; } = new(EffectiveAccess.NotFound, null, null);
    public static EffectiveResult<T> Invalid(IDictionary<string, string[]> errors) => new(EffectiveAccess.Invalid, null, errors);
    public static EffectiveResult<T> Conflict(string message) => new(EffectiveAccess.Conflict, null, null, message);
    public static EffectiveResult<T> Ok(T value) => new(EffectiveAccess.Allowed, value, null);
}

/// <summary><c>Origin</c>: Inherited, Customized, Historical ou Undefined. <c>Removed</c> = retirada de propósito (não reaparece por herança). <c>StandardFrom</c> = início da vigência do padrão.</summary>
public sealed record EffectiveTargetDto(Guid Id, string Target, Guid? CategoryId, string Name, string? Amount, string Origin, bool Removed, string? StandardFrom);

/// <summary><c>OutsideCardsTarget</c> = total − cartões (calculado, nunca armazenado). Despesas fixas e categorias são visões complementares e não entram nessa conta.</summary>
public sealed record EffectiveBudgetDto(string Competence, Guid? BudgetId, bool StandardConfigured, string? OutsideCardsTarget, IReadOnlyList<EffectiveTargetDto> Targets, IReadOnlyList<string> Warnings);

public sealed record BudgetChangeRequest(string? Competence, string? Target, Guid? CategoryId, string? Mode, string? Action, decimal? Amount, string? Note);

public sealed record BudgetRuleHistoryDto(string Scope, string Target, Guid? CategoryId, string? CategoryName, string Competence, int Revision, string Action, string? Amount, string? Note, DateTimeOffset At);

public sealed record ReferenceItemDto(string Target, Guid? CategoryId, string Name, string Amount);

/// <summary>Prévia de "usar o orçamento existente como referência". <c>Warnings</c>: FixedExpensesNotOutsideCards, CategoriesMayBeInsideCards, TotalExpensesRequiredForCeilings.</summary>
public sealed record ReferencePreviewDto(string Source, string From, IReadOnlyList<ReferenceItemDto> Items, IReadOnlyList<string> Warnings);

public sealed record ReferenceTargetRef(string? Target, Guid? CategoryId);
public sealed record ApplyReferenceRequest(string? Source, string? From, IReadOnlyList<ReferenceTargetRef>? Targets);

public sealed class EffectiveBudgetService(
    IFinancialWorkspaceRepository workspaces,
    IBudgetLayerReader layerReader,
    IBudgetRuleRepository rules,
    IFinancialBudgetRepository budgets,
    TimeProvider timeProvider)
{
    /// <summary>Quantos meses à frente as alterações são conferidas (o padrão vale indefinidamente; as mudanças de vigência e exceções cadastradas é que importam).</summary>
    public const int ValidationHorizonMonths = 60;
    public const int HistoryLimit = 100;
    public const string ConcurrentMessage = "Outra alteração simultânea ocupou esta revisão. Atualize a tela e tente novamente.";

    public static string NameOf(BudgetRuleTarget target) => target switch
    {
        BudgetRuleTarget.TotalExpenses => "Total de despesas PF",
        BudgetRuleTarget.AllCards => "Todos os cartões",
        BudgetRuleTarget.FixedExpenses => "Despesas fixas",
        _ => "Categoria",
    };

    private static string F(decimal value) => Money.Format(value);

    // ---- Leitura (nunca grava) --------------------------------------------------------------------------------------------------------------------------

    public async Task<EffectiveResult<EffectiveBudgetDto>> GetAsync(Guid workspaceId, Guid userId, string? competence, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)) return EffectiveResult<EffectiveBudgetDto>.NotFound;
        if (!TryMonth(competence, out var month)) return EffectiveResult<EffectiveBudgetDto>.Invalid(Error("competence", "Use uma competência válida no formato yyyy-MM."));
        return EffectiveResult<EffectiveBudgetDto>.Ok(await BuildAsync(workspaceId, month, cancellationToken));
    }

    /// <summary>A mesma visão para outros consumidores (Visão Geral): mesma resolução, sem verificar o usuário de novo.</summary>
    public async Task<EffectiveBudgetDto> BuildAsync(Guid workspaceId, DateOnly month, CancellationToken cancellationToken)
    {
        var layers = await layerReader.ReadAsync(workspaceId, month, month.AddMonths(1), cancellationToken);
        var effective = EffectiveBudgetResolver.Resolve(workspaceId, layers, month);
        var names = await rules.CategoryNamesAsync(workspaceId, effective.Targets.Where(t => t.CategoryId != null).Select(t => t.CategoryId!.Value).ToList(), cancellationToken);
        var targets = effective.Targets.Select(t => new EffectiveTargetDto(t.Id, t.Target.ToString(), t.CategoryId,
            t.CategoryId is { } id && names.TryGetValue(id, out var name) ? (name.ParentName is null ? name.Name : $"{name.ParentName} / {name.Name}") : NameOf(t.Target),
            t.Amount is { } amount ? F(amount) : null, t.Origin, t.Removed, t.StandardFrom is { } from ? from.ToString("yyyy-MM") : null))
            .OrderBy(t => t.Target == nameof(BudgetRuleTarget.Category)).ThenBy(t => t.Target == nameof(BudgetRuleTarget.Category) ? t.Name : string.Empty, StringComparer.CurrentCultureIgnoreCase).ToList();
        var warnings = new List<string>();
        if (effective.TotalExpenses is null && effective.CardTarget is not null) warnings.Add("TotalExpensesRequiredForCeilings");
        if (effective.TotalExpenses is null && effective.CardTarget is null) warnings.Add("BudgetNotConfigured");
        warnings.AddRange(EffectiveBudgetResolver.Validate(effective, layers.CategoryParents).Select(e => e == EffectiveBudgetResolver.TotalBelowCardsMessage ? "TotalBelowCards" : "OverlappingCategories"));
        decimal? outside = effective.TotalExpenses is { } total && effective.CardTarget is { } cards ? Math.Max(0m, total - cards) : null;
        return new(month.ToString("yyyy-MM"), effective.BudgetId, layers.Changes.Any(c => c.Scope == BudgetRuleScope.FromCompetence && c.Action == BudgetRuleAction.Set),
            outside is { } value ? F(value) : null, targets, warnings);
    }

    public async Task<EffectiveResult<IReadOnlyList<BudgetRuleHistoryDto>>> HistoryAsync(Guid workspaceId, Guid userId, string? competence, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)) return EffectiveResult<IReadOnlyList<BudgetRuleHistoryDto>>.NotFound;
        DateOnly? month = null;
        if (!string.IsNullOrWhiteSpace(competence))
        {
            if (!TryMonth(competence, out var parsed)) return EffectiveResult<IReadOnlyList<BudgetRuleHistoryDto>>.Invalid(Error("competence", "Use uma competência válida no formato yyyy-MM."));
            month = parsed;
        }
        var rows = await rules.ListHistoryAsync(workspaceId, month, HistoryLimit, cancellationToken);
        var names = await rules.CategoryNamesAsync(workspaceId, rows.Where(r => r.CategoryId != null).Select(r => r.CategoryId!.Value).Distinct().ToList(), cancellationToken);
        IReadOnlyList<BudgetRuleHistoryDto> items = rows.Select(r => new BudgetRuleHistoryDto(r.Scope.ToString(), r.Target.ToString(), r.CategoryId,
            r.CategoryId is { } id && names.TryGetValue(id, out var n) ? n.Name : null, r.Competence.ToString("yyyy-MM"), r.Revision, r.Action.ToString(),
            r.Amount is { } amount ? F(amount) : null, r.Note, r.CreatedAt)).ToList();
        return EffectiveResult<IReadOnlyList<BudgetRuleHistoryDto>>.Ok(items);
    }

    // ---- Alteração --------------------------------------------------------------------------------------------------------------------------------------

    public async Task<EffectiveResult<EffectiveBudgetDto>> ChangeAsync(Guid workspaceId, Guid userId, BudgetChangeRequest? request, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken) || request is null) return EffectiveResult<EffectiveBudgetDto>.NotFound;
        var errors = new Dictionary<string, string[]>();
        if (!TryMonth(request.Competence, out var month)) errors["competence"] = ["Use uma competência válida no formato yyyy-MM."];
        if (!Enum.TryParse<BudgetRuleTarget>(request.Target, out var target) || !Enum.IsDefined(target)) errors["target"] = ["Meta inválida."];
        if (!Enum.TryParse<BudgetRuleAction>(request.Action, out var action) || !Enum.IsDefined(action)) errors["action"] = ["Ação inválida."];
        var thisMonth = request.Mode == "ThisMonth";
        if (!thisMonth && request.Mode != "ThisAndNext") errors["mode"] = ["Escolha \"somente este mês\" ou \"este mês e os próximos\"."];
        else if (!thisMonth && action == BudgetRuleAction.Restore) errors["action"] = ["Restaurar o padrão vale só para uma competência."];
        if (action == BudgetRuleAction.Set && !errors.ContainsKey("action"))
        {
            if (request.Amount is not { } amount || amount < 0m || amount > FinancialBudgetRuleChange.MaxAmount) errors["amount"] = ["Informe um valor da meta maior ou igual a zero."];
            else if (Money.Validate(amount, "Meta") is { } moneyError) errors["amount"] = [moneyError];
        }
        if (errors.Count == 0 && (target == BudgetRuleTarget.Category) != (request.CategoryId is not null)) errors["categoryId"] = ["Só a meta por categoria informa a categoria."];
        if (errors.Count > 0) return EffectiveResult<EffectiveBudgetDto>.Invalid(errors);

        IReadOnlyDictionary<Guid, Guid?> extraParents = new Dictionary<Guid, Guid?>();
        if (request.CategoryId is { } categoryId)
        {
            var category = await budgets.GetCategoryAsync(workspaceId, categoryId, cancellationToken);
            if (category is null) return EffectiveResult<EffectiveBudgetDto>.NotFound;
            if (!category.IsExpense) return EffectiveResult<EffectiveBudgetDto>.Invalid(Error("categoryId", "A meta deve referenciar uma categoria de despesa."));
            if (action == BudgetRuleAction.Set && !category.IsActive) return EffectiveResult<EffectiveBudgetDto>.Invalid(Error("categoryId", "A categoria está inativa."));
            var names = await rules.CategoryNamesAsync(workspaceId, [categoryId], cancellationToken);
            extraParents = names.ToDictionary(p => p.Key, p => p.Value.ParentId);
        }

        var layers = await layerReader.ReadAsync(workspaceId, month, month.AddMonths(ValidationHorizonMonths), cancellationToken);
        var current = EffectiveBudgetResolver.Resolve(workspaceId, layers, month).Find(target, request.CategoryId);
        var scope = thisMonth ? BudgetRuleScope.OnlyCompetence : BudgetRuleScope.FromCompetence;
        var activeException = LatestException(layers, target, request.CategoryId, month);

        if (action == BudgetRuleAction.Remove && current is not { Defined: true })
            return EffectiveResult<EffectiveBudgetDto>.Conflict("Não há meta definida para retirar nesta competência.");
        if (action == BudgetRuleAction.Restore && activeException is null)
            return EffectiveResult<EffectiveBudgetDto>.Conflict(current?.Origin == TargetOrigin.Historical
                ? "O valor desta competência é um registro mensal anterior, preservado: personalize o mês para substituí-lo."
                : "Esta competência não tem exceção para restaurar.");

        var now = timeProvider.GetUtcNow();
        var added = new List<FinancialBudgetRuleChange>
        {
            FinancialBudgetRuleChange.Create(workspaceId, scope, target, request.CategoryId, month, await rules.NextRevisionAsync(workspaceId, scope, target, request.CategoryId, month, cancellationToken),
                action, action == BudgetRuleAction.Set ? request.Amount : null, request.Note, userId, now),
        };
        // "Este mês e os próximos" vale também para o mês de partida: uma exceção ativa dele é desfeita (as exceções de meses posteriores continuam).
        if (!thisMonth && activeException is not null)
            added.Add(FinancialBudgetRuleChange.Create(workspaceId, BudgetRuleScope.OnlyCompetence, target, request.CategoryId, month,
                await rules.NextRevisionAsync(workspaceId, BudgetRuleScope.OnlyCompetence, target, request.CategoryId, month, cancellationToken), BudgetRuleAction.Restore, null, request.Note, userId, now));

        var problems = NewErrors(workspaceId, layers, added, month, extraParents);
        if (problems.Count > 0) return EffectiveResult<EffectiveBudgetDto>.Invalid(new Dictionary<string, string[]> { ["budget"] = problems.ToArray() });
        if (!await rules.TryAddAsync(added, cancellationToken)) return EffectiveResult<EffectiveBudgetDto>.Conflict(ConcurrentMessage);
        return EffectiveResult<EffectiveBudgetDto>.Ok(await BuildAsync(workspaceId, month, cancellationToken));
    }

    // ---- Usar o orçamento existente como referência de um novo padrão -----------------------------------------------------------------------------------

    public async Task<EffectiveResult<ReferencePreviewDto>> ReferencePreviewAsync(Guid workspaceId, Guid userId, string? source, string? from, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)) return EffectiveResult<ReferencePreviewDto>.NotFound;
        if (!TryMonth(source, out var sourceMonth)) return EffectiveResult<ReferencePreviewDto>.Invalid(Error("source", "Use uma competência de referência válida no formato yyyy-MM."));
        var start = sourceMonth.AddMonths(1);
        if (!string.IsNullOrWhiteSpace(from) && !TryMonth(from, out start)) return EffectiveResult<ReferencePreviewDto>.Invalid(Error("from", "Use uma competência de início válida no formato yyyy-MM."));
        var (items, warnings) = await ReferenceItemsAsync(workspaceId, sourceMonth, cancellationToken);
        return EffectiveResult<ReferencePreviewDto>.Ok(new(sourceMonth.ToString("yyyy-MM"), start.ToString("yyyy-MM"), items.Select(i => i.Dto).ToList(), warnings));
    }

    public async Task<EffectiveResult<EffectiveBudgetDto>> ApplyReferenceAsync(Guid workspaceId, Guid userId, ApplyReferenceRequest? request, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken) || request is null) return EffectiveResult<EffectiveBudgetDto>.NotFound;
        if (!TryMonth(request.Source, out var sourceMonth)) return EffectiveResult<EffectiveBudgetDto>.Invalid(Error("source", "Use uma competência de referência válida no formato yyyy-MM."));
        if (!TryMonth(request.From, out var start)) return EffectiveResult<EffectiveBudgetDto>.Invalid(Error("from", "Use uma competência de início válida no formato yyyy-MM."));
        if (request.Targets is not { Count: > 0 }) return EffectiveResult<EffectiveBudgetDto>.Invalid(Error("targets", "Escolha ao menos uma meta para o padrão."));

        // Os valores vêm do servidor (a referência é relida aqui), nunca do cliente.
        var (items, _) = await ReferenceItemsAsync(workspaceId, sourceMonth, cancellationToken);
        var chosen = new List<(BudgetRuleTarget Target, Guid? CategoryId, decimal Amount)>();
        foreach (var requested in request.Targets)
        {
            if (!Enum.TryParse<BudgetRuleTarget>(requested.Target, out var target)) return EffectiveResult<EffectiveBudgetDto>.Invalid(Error("targets", "Meta inválida."));
            var index = items.FindIndex(i => i.Target == target && i.CategoryId == requested.CategoryId);
            if (index < 0) return EffectiveResult<EffectiveBudgetDto>.Invalid(Error("targets", "A meta escolhida não está definida na competência de referência."));
            if (!chosen.Any(c => c.Target == target && c.CategoryId == requested.CategoryId)) chosen.Add((target, requested.CategoryId, items[index].Amount));
        }

        var layers = await layerReader.ReadAsync(workspaceId, start, start.AddMonths(ValidationHorizonMonths), cancellationToken);
        var now = timeProvider.GetUtcNow();
        var added = new List<FinancialBudgetRuleChange>();
        foreach (var (target, categoryId, amount) in chosen)
            added.Add(FinancialBudgetRuleChange.Create(workspaceId, BudgetRuleScope.FromCompetence, target, categoryId, start,
                await rules.NextRevisionAsync(workspaceId, BudgetRuleScope.FromCompetence, target, categoryId, start, cancellationToken), BudgetRuleAction.Set, amount,
                $"Referência: {sourceMonth:yyyy-MM}", userId, now));
        var names = await rules.CategoryNamesAsync(workspaceId, chosen.Where(c => c.CategoryId != null).Select(c => c.CategoryId!.Value).ToList(), cancellationToken);
        var problems = NewErrors(workspaceId, layers, added, start, names.ToDictionary(p => p.Key, p => p.Value.ParentId));
        if (problems.Count > 0) return EffectiveResult<EffectiveBudgetDto>.Invalid(new Dictionary<string, string[]> { ["budget"] = problems.ToArray() });
        if (!await rules.TryAddAsync(added, cancellationToken)) return EffectiveResult<EffectiveBudgetDto>.Conflict(ConcurrentMessage);
        return EffectiveResult<EffectiveBudgetDto>.Ok(await BuildAsync(workspaceId, start, cancellationToken));
    }

    private async Task<(List<(BudgetRuleTarget Target, Guid? CategoryId, decimal Amount, ReferenceItemDto Dto)> Items, IReadOnlyList<string> Warnings)> ReferenceItemsAsync(
        Guid workspaceId, DateOnly sourceMonth, CancellationToken cancellationToken)
    {
        var effective = (await BuildAsync(workspaceId, sourceMonth, cancellationToken));
        var items = effective.Targets.Where(t => t.Amount is not null).Select(t =>
        {
            var target = Enum.Parse<BudgetRuleTarget>(t.Target);
            var amount = decimal.Parse(t.Amount!, System.Globalization.CultureInfo.InvariantCulture);
            return (target, t.CategoryId, amount, new ReferenceItemDto(t.Target, t.CategoryId, t.Name, t.Amount!));
        }).ToList();
        var warnings = new List<string>();
        if (items.Any(i => i.target == BudgetRuleTarget.FixedExpenses)) warnings.Add("FixedExpensesNotOutsideCards");
        if (items.Any(i => i.target == BudgetRuleTarget.Category)) warnings.Add("CategoriesMayBeInsideCards");
        if (items.All(i => i.target != BudgetRuleTarget.TotalExpenses)) warnings.Add("TotalExpensesRequiredForCeilings");
        return (items, warnings);
    }

    // ---- Validação do estado resultante -----------------------------------------------------------------------------------------------------------------

    private static RuleChangeFact? LatestException(BudgetLayers layers, BudgetRuleTarget target, Guid? categoryId, DateOnly month)
    {
        var latest = layers.Changes.Where(c => c.Scope == BudgetRuleScope.OnlyCompetence && c.Target == target && c.CategoryId == categoryId && c.Competence == month)
            .OrderByDescending(c => c.Revision).FirstOrDefault();
        return latest is { Action: not BudgetRuleAction.Restore } ? latest : null;
    }

    /// <summary>
    /// Erros que a alteração INTRODUZ: confere a competência de partida e cada mudança de vigência ou exceção posterior (o padrão vale adiante), antes e depois. Um problema que já existia
    /// e que a alteração não causa não bloqueia a edição.
    /// </summary>
    private static List<string> NewErrors(Guid workspaceId, BudgetLayers before, IReadOnlyList<FinancialBudgetRuleChange> added, DateOnly from, IReadOnlyDictionary<Guid, Guid?> extraParents)
    {
        var parents = new Dictionary<Guid, Guid?>(before.CategoryParents);
        foreach (var (id, parent) in extraParents) parents[id] = parent;
        var baseline = before with { CategoryParents = parents };
        var after = baseline with
        {
            Changes = before.Changes.Concat(added.Select(c => new RuleChangeFact(c.Scope, c.Target, c.CategoryId, c.Competence, c.Revision, c.Action, c.Amount, c.CreatedAt))).ToList(),
        };
        return EffectiveBudgetResolver.ChangePoints(after, from)
            .SelectMany(point => EffectiveBudgetResolver.Validate(EffectiveBudgetResolver.Resolve(workspaceId, after, point), parents)
                .Except(EffectiveBudgetResolver.Validate(EffectiveBudgetResolver.Resolve(workspaceId, baseline, point), parents)))
            .Distinct().ToList();
    }

    private static bool TryMonth(string? value, out DateOnly month)
    {
        month = default;
        return !string.IsNullOrWhiteSpace(value) && FinancialBudgetService.TryParseCompetence(value, out month);
    }

    private static Dictionary<string, string[]> Error(string field, string message) => new() { [field] = [message] };

    private async Task<bool> IsParticipantAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is not null;
}

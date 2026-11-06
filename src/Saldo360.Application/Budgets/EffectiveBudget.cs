using System.Security.Cryptography;
using System.Text;
using Saldo360.Domain.Budgets;

namespace Saldo360.Application.Budgets;

// T12.1: orçamento mensal recorrente. UM resolvedor de metas efetivas (puro, sem banco) alimenta a página Orçamento, a execução, a Visão Geral, o consumo do orçamento, o Plano × Realizado e a
// projeção (Atual e Tetos): o mesmo mês e workspace mostram os mesmos limites em todas as áreas. Herdam-se só CONFIGURAÇÕES, nunca lançamentos, faturas ou despesas.

/// <summary>Origem do valor efetivo de uma meta numa competência.</summary>
public static class TargetOrigin
{
    /// <summary>Herdado do padrão vigente (a vigência está em <c>StandardFrom</c>).</summary>
    public const string Inherited = "Inherited";
    /// <summary>Personalizado neste mês (exceção da competência).</summary>
    public const string Customized = "Customized";
    /// <summary>Registro mensal anterior à recorrência (orçamento da competência, preservado).</summary>
    public const string Historical = "Historical";
    /// <summary>Nenhuma configuração: a meta não está definida (nunca zero).</summary>
    public const string Undefined = "Undefined";
}

public sealed record RuleChangeFact(BudgetRuleScope Scope, BudgetRuleTarget Target, Guid? CategoryId, DateOnly Competence, int Revision, BudgetRuleAction Action, decimal? Amount, DateTimeOffset CreatedAt);

/// <summary>Meta de um orçamento mensal já materializado. <c>Archived</c> = retirada intencionalmente naquela competência.</summary>
public sealed record LegacyTargetFact(BudgetRuleTarget Target, Guid? CategoryId, decimal Amount, bool Archived, Guid TargetId);

/// <summary>Registros do mês anteriores à recorrência: o orçamento mensal (se ativo) e o total de despesas agregado importado.</summary>
public sealed record LegacyMonthFact(DateOnly Competence, Guid? BudgetId, IReadOnlyList<LegacyTargetFact> Targets, decimal? TotalExpenses);

public sealed record BudgetLayers(IReadOnlyList<RuleChangeFact> Changes, IReadOnlyDictionary<DateOnly, LegacyMonthFact> Legacy, IReadOnlyDictionary<Guid, Guid?> CategoryParents)
{
    public static BudgetLayers Empty { get; } = new([], new Dictionary<DateOnly, LegacyMonthFact>(), new Dictionary<Guid, Guid?>());
}

/// <summary>
/// Meta efetiva. <c>Amount</c> nulo = sem valor (<c>Removed</c> distingue "retirada de propósito" de "nunca configurada"). <c>StandardFrom</c> = início da vigência do padrão de onde veio o valor.
/// <c>Id</c> é o da meta mensal histórica quando existe; senão, um identificador estável da chave (workspace, tipo, categoria).
/// </summary>
public sealed record EffectiveTarget(Guid Id, BudgetRuleTarget Target, Guid? CategoryId, decimal? Amount, string Origin, bool Removed, DateOnly? StandardFrom, Guid? LegacyTargetId)
{
    public bool Defined => Amount is not null;
}

public sealed record EffectiveBudgetMonth(DateOnly Competence, Guid? BudgetId, IReadOnlyList<EffectiveTarget> Targets)
{
    public EffectiveTarget? Find(BudgetRuleTarget target, Guid? categoryId = null) => Targets.FirstOrDefault(t => t.Target == target && t.CategoryId == categoryId);
    public decimal? Amount(BudgetRuleTarget target) => Find(target)?.Amount;
    public decimal? TotalExpenses => Amount(BudgetRuleTarget.TotalExpenses);
    public decimal? CardTarget => Amount(BudgetRuleTarget.AllCards);
    public decimal? FixedTarget => Amount(BudgetRuleTarget.FixedExpenses);
    public IEnumerable<EffectiveTarget> Categories => Targets.Where(t => t.Target == BudgetRuleTarget.Category && t.Defined);
}

public static class EffectiveBudgetResolver
{
    public const string TotalBelowCardsMessage = "O orçamento total não pode ser menor que a meta de todos os cartões.";
    public const string OverlappingCategoriesMessage = "Há meta de uma categoria e de uma subcategoria dela no mesmo mês: o mesmo gasto seria contado duas vezes.";

    private static readonly BudgetRuleTarget[] FixedKinds = [BudgetRuleTarget.TotalExpenses, BudgetRuleTarget.AllCards, BudgetRuleTarget.FixedExpenses];

    /// <summary>Identificador estável da meta recorrente (o mesmo em qualquer mês): permite abrir a composição de uma meta herdada.</summary>
    public static Guid StableId(Guid workspaceId, BudgetRuleTarget target, Guid? categoryId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"budget-rule|{workspaceId:N}|{target}|{categoryId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static EffectiveBudgetMonth Resolve(Guid workspaceId, BudgetLayers layers, DateOnly competence)
    {
        var legacy = layers.Legacy.GetValueOrDefault(competence);
        var keys = new HashSet<(BudgetRuleTarget, Guid?)>(FixedKinds.Select(k => (k, (Guid?)null)));
        foreach (var change in layers.Changes) keys.Add((change.Target, change.CategoryId));
        if (legacy is not null) foreach (var target in legacy.Targets) keys.Add((target.Target, target.CategoryId));

        var targets = keys.Select(key => ResolveKey(workspaceId, layers, legacy, competence, key.Item1, key.Item2))
            .Where(t => t.Origin != TargetOrigin.Undefined || FixedKinds.Contains(t.Target))
            .OrderBy(t => Array.IndexOf(FixedKinds, t.Target) is var index and >= 0 ? index : FixedKinds.Length).ThenBy(t => t.CategoryId).ToList();
        return new(competence, legacy?.BudgetId, targets);
    }

    private static EffectiveTarget ResolveKey(Guid workspaceId, BudgetLayers layers, LegacyMonthFact? legacy, DateOnly competence, BudgetRuleTarget target, Guid? categoryId)
    {
        var id = EffectiveBudgetResolver.StableId(workspaceId, target, categoryId);
        var mine = layers.Changes.Where(c => c.Target == target && c.CategoryId == categoryId).ToList();

        // 1. Ajuste explícito da competência: vale a maior revisão; "restaurar" desfaz a exceção.
        var exception = mine.Where(c => c.Scope == BudgetRuleScope.OnlyCompetence && c.Competence == competence).OrderByDescending(c => c.Revision).FirstOrDefault();
        if (exception is { Action: BudgetRuleAction.Set }) return new(id, target, categoryId, exception.Amount, TargetOrigin.Customized, false, null, null);
        if (exception is { Action: BudgetRuleAction.Remove }) return new(id, target, categoryId, null, TargetOrigin.Customized, true, null, null);

        // 2. Registro mensal histórico (anterior à recorrência), preservado como ajuste explícito daquela competência. Meta arquivada = retirada de propósito.
        if (legacy is not null)
        {
            if (target == BudgetRuleTarget.TotalExpenses)
            {
                if (legacy.TotalExpenses is { } total) return new(id, target, categoryId, total, TargetOrigin.Historical, false, null, null);
            }
            else if (legacy.Targets.FirstOrDefault(t => t.Target == target && t.CategoryId == categoryId) is { } old)
                return new(old.TargetId, target, categoryId, old.Archived ? null : old.Amount, TargetOrigin.Historical, old.Archived, null, old.TargetId);
        }

        // 3. Padrão vigente: a última alteração com vigência até a competência (maior competência; nela, a maior revisão). Retirada vale da vigência em diante.
        var standard = mine.Where(c => c.Scope == BudgetRuleScope.FromCompetence && c.Competence <= competence).OrderByDescending(c => c.Competence).ThenByDescending(c => c.Revision).FirstOrDefault();
        if (standard is { Action: BudgetRuleAction.Set }) return new(id, target, categoryId, standard.Amount, TargetOrigin.Inherited, false, standard.Competence, null);
        if (standard is { Action: BudgetRuleAction.Remove }) return new(id, target, categoryId, null, TargetOrigin.Inherited, true, standard.Competence, null);

        // 4. Nenhuma configuração.
        return new(id, target, categoryId, null, TargetOrigin.Undefined, false, null, null);
    }

    /// <summary>Validações da configuração efetiva de uma competência: total menor que cartões, e categoria junto da sua subcategoria (gasto contado duas vezes).</summary>
    public static IReadOnlyList<string> Validate(EffectiveBudgetMonth month, IReadOnlyDictionary<Guid, Guid?> categoryParents)
    {
        var errors = new List<string>();
        if (month.TotalExpenses is { } total && month.CardTarget is { } cards && total < cards) errors.Add(TotalBelowCardsMessage);
        var active = month.Categories.Select(t => t.CategoryId!.Value).ToHashSet();
        if (active.Any(id => categoryParents.GetValueOrDefault(id) is { } parent && active.Contains(parent))) errors.Add(OverlappingCategoriesMessage);
        return errors;
    }

    /// <summary>Competências em que a configuração efetiva pode mudar a partir de <paramref name="from"/>: a própria e cada início de vigência ou exceção posterior.</summary>
    public static IReadOnlyList<DateOnly> ChangePoints(BudgetLayers layers, DateOnly from) =>
        layers.Changes.Select(c => c.Competence).Where(c => c > from).Append(from).Distinct().OrderBy(c => c).ToList();
}

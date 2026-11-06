using Saldo360.Domain.Common;

namespace Saldo360.Domain.Budgets;

/// <summary>Qual meta a regra define. <c>TotalExpenses</c>, <c>AllCards</c> e <c>FixedExpenses</c> não têm categoria; <c>Category</c> exige a categoria.</summary>
public enum BudgetRuleTarget { TotalExpenses, AllCards, FixedExpenses, Category }

/// <summary><c>FromCompetence</c> = padrão: vale da competência em diante, até a próxima mudança. <c>OnlyCompetence</c> = exceção: vale só naquela competência.</summary>
public enum BudgetRuleScope { FromCompetence, OnlyCompetence }

/// <summary>
/// <c>Set</c> define o valor. <c>Remove</c> retira a meta (nesta competência ou da competência em diante) e impede que ela reapareça por herança. <c>Restore</c> só existe na exceção
/// e desfaz a exceção da competência, devolvendo-a ao padrão.
/// </summary>
public enum BudgetRuleAction { Set, Remove, Restore }

/// <summary>
/// T12.1: uma alteração das regras do orçamento recorrente. O log é append-only: nada é editado nem apagado, então o histórico de valores e de vigência fica preservado. O estado vigente de uma
/// meta numa competência é resolvido por <c>EffectiveBudgetResolver</c> (exceção da competência, depois padrão vigente, depois não definida). Uma revisão só pode existir uma vez por chave
/// (índice único), o que impede configurações duplicadas em alterações concorrentes.
/// </summary>
public sealed class FinancialBudgetRuleChange : Entity
{
    public const decimal MaxAmount = 1_000_000_000m;

    private FinancialBudgetRuleChange() { }

    public Guid WorkspaceId { get; private init; }
    public BudgetRuleScope Scope { get; private init; }
    public BudgetRuleTarget Target { get; private init; }
    public Guid? CategoryId { get; private init; }
    /// <summary>Primeiro dia do mês: início da vigência (padrão) ou a competência da exceção.</summary>
    public DateOnly Competence { get; private init; }
    /// <summary>Sequência das alterações da mesma chave (1, 2, 3…): vale a maior.</summary>
    public int Revision { get; private init; }
    public BudgetRuleAction Action { get; private init; }
    public decimal? Amount { get; private init; }
    public string? Note { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }

    public static FinancialBudgetRuleChange Create(Guid workspaceId, BudgetRuleScope scope, BudgetRuleTarget target, Guid? categoryId, DateOnly competence, int revision,
        BudgetRuleAction action, decimal? amount, string? note, Guid userId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Regra de orçamento inválida.");
        if (competence.Day != 1) throw new ArgumentException("A competência deve ser o primeiro dia do mês.");
        if (revision < 1) throw new ArgumentException("Revisão inválida.");
        if (!Enum.IsDefined(scope) || !Enum.IsDefined(target) || !Enum.IsDefined(action)) throw new ArgumentException("Regra de orçamento inválida.");
        if ((target == BudgetRuleTarget.Category) != (categoryId is not null)) throw new ArgumentException("Só a meta por categoria informa a categoria.");
        if (action == BudgetRuleAction.Restore && scope != BudgetRuleScope.OnlyCompetence) throw new ArgumentException("Só a exceção de uma competência pode ser restaurada ao padrão.");
        if (action == BudgetRuleAction.Set)
        {
            if (amount is not { } value || value < 0m || value > MaxAmount || Money.Validate(value, "Meta") is not null) throw new ArgumentException("Valor da meta inválido.");
        }
        else if (amount is not null) throw new ArgumentException("Só a definição de valor informa o valor.");
        return new()
        {
            WorkspaceId = workspaceId, Scope = scope, Target = target, CategoryId = categoryId, Competence = competence, Revision = revision, Action = action, Amount = amount,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 200)], CreatedAt = now, CreatedByUserId = userId,
        };
    }
}

using Saldo360.Domain.Budgets;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.MonthlyPlan;

/// <summary>Acrescenta entradas ao histórico do planejamento na MESMA unidade de trabalho da alteração (gravadas juntas no próximo salvamento).</summary>
public interface IPlanHistoryWriter
{
    void Stage(FinancialPlanHistoryEntry entry);
}

/// <summary>Tudo o que uma cópia de mês grava, numa só transação. <c>NewBudget</c> é nulo quando o orçamento de destino já existe.</summary>
public sealed record CopyBatch(
    IReadOnlyList<FinancialPlanLine> Lines, FinancialBudget? NewBudget, IReadOnlyList<FinancialBudgetTarget> Targets, IReadOnlyList<FinancialPlanHistoryEntry> History);

public interface IMonthlyPlanRepository
{
    /// <summary>Linhas ativas e limites por categoria ativos das competências de <paramref name="from"/> a <paramref name="through"/> (inclusive). Somente leitura.</summary>
    Task<PlanFacts> LoadAsync(Guid workspaceId, DateOnly from, DateOnly through, CancellationToken cancellationToken);

    /// <summary>Linha (rastreada para atualização) do orçamento, ativa ou arquivada.</summary>
    Task<FinancialPlanLine?> FindLineAsync(Guid workspaceId, Guid lineId, CancellationToken cancellationToken);

    /// <summary>Contas das linhas ativas das competências indicadas (para manter visíveis contas inativas com planejamento).</summary>
    Task<IReadOnlySet<Guid>> AccountsWithPlanAsync(Guid workspaceId, DateOnly from, DateOnly through, CancellationToken cancellationToken);

    void Add(FinancialPlanLine line);
    void Stage(FinancialPlanHistoryEntry entry);
    Task SaveAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FinancialPlanHistoryEntry>> HistoryAsync(Guid workspaceId, DateOnly competence, Guid? subjectId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<FinancialNonWorkingDay>> ListNonWorkingDaysAsync(Guid workspaceId, DateOnly from, DateOnly through, CancellationToken cancellationToken);
    Task<bool> AddNonWorkingDayAsync(FinancialNonWorkingDay day, CancellationToken cancellationToken);
    Task<bool> RemoveNonWorkingDayAsync(Guid workspaceId, Guid id, CancellationToken cancellationToken);

    /// <summary>Grava a cópia do planejamento. Falso se uma restrição de unicidade foi violada (cópia concorrente); nada é gravado.</summary>
    Task<bool> ApplyCopyAsync(CopyBatch batch, CancellationToken cancellationToken);
}

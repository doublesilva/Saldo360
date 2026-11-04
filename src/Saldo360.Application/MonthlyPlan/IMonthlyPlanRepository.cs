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

    // ---- PROJ-01: previsão de receita PJ, transferências PJ → PF e obrigações fixas por mês --------------------------------------------------------------

    /// <summary>Configuração (ativa ou não) e recebimentos previstos que caem entre <paramref name="from"/> e <paramref name="through"/>. Somente leitura.</summary>
    Task<IncomeForecastView> LoadIncomeForecastAsync(Guid workspaceId, DateOnly from, DateOnly through, DateOnly today, CancellationToken cancellationToken);

    Task UpsertIncomeForecastAsync(Guid workspaceId, IncomeForecastSettings settings, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task UpsertForecastMonthAsync(Guid workspaceId, IncomeForecastMonthOverride adjust, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> RemoveForecastMonthAsync(Guid workspaceId, DateOnly competence, CancellationToken cancellationToken);

    /// <summary>Repasses entre as duas contas por mês: previstos ainda sem transferência associada e realizados. Somente leitura.</summary>
    Task<IReadOnlyList<TransferMonthFlow>> TransferFlowsAsync(Guid workspaceId, Guid sourceAccountId, Guid destinationAccountId, DateOnly from, DateOnly through, CancellationToken cancellationToken);

    /// <summary>Repasse PJ → PF projetado pela automação (PROJ-06), no horizonte fixo de seis meses. Somente leitura: nada é gravado e nenhuma transferência é executada.</summary>
    Task<IReadOnlyList<ProjectedPjTransfer>> ProjectedPjTransfersAsync(Guid workspaceId, DateOnly today, CancellationToken cancellationToken);

    /// <summary>Despesas fixas ainda pendentes (previsão consolidada) da conta, por mês de vencimento.</summary>
    Task<IReadOnlyDictionary<DateOnly, decimal>> PendingFixedByMonthAsync(Guid workspaceId, Guid accountId, DateOnly from, DateOnly through, DateOnly today, CancellationToken cancellationToken);
}

public sealed record TransferMonthFlow(DateOnly Month, decimal PlannedPending, decimal Realized);

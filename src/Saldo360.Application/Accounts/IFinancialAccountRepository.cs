using Saldo360.Domain.Accounts;

namespace Saldo360.Application.Accounts;

public interface IFinancialAccountRepository
{
    Task AddAsync(FinancialAccount account, CancellationToken cancellationToken);

    /// <summary>A conta, se pertencer à família (rastreada para atualização).</summary>
    Task<FinancialAccount?> FindAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Executa <paramref name="work"/> numa transação com o workspace bloqueado (SELECT ... FOR UPDATE): operações que trocam a conta principal
    /// são serializadas e todas as gravações da operação confirmam ou desfazem juntas.
    /// </summary>
    Task<T> RunExclusiveAsync<T>(Guid workspaceId, Func<Task<T>> work, CancellationToken cancellationToken);

    /// <summary>A conta principal atual do workspace (rastreada), se houver.</summary>
    Task<FinancialAccount?> FindPrimaryAsync(Guid workspaceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FinancialAccountDto>> ListAsync(Guid workspaceId, bool includeInactive, CancellationToken cancellationToken);

    Task<FinancialAccountDto?> GetAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>Verifica se o usuário indicado é o proprietário do workspace pessoal.</summary>
    Task<bool> IsWorkspaceOwnerAsync(Guid workspaceId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>Saldo inicial configurado da conta (ativa ou não), se ela pertencer à família; null caso contrário.</summary>
    Task<AccountOpeningBalance?> GetOpeningBalanceAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken);
    /// <summary>
    /// Saldo inicial derivado da referência bancária (âncora) das contas que NÃO têm saldo inicial manual e cuja referência permite cálculo
    /// confiável. Contas sem referência utilizável não aparecem. O saldo inicial manual sempre prevalece e nunca é alterado por isto.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, AccountOpeningBalance>> GetAnchoredOpeningsAsync(Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>
    /// Somas não canceladas da conta com <paramref name="from"/> ≤ data ≤ <paramref name="to"/>: receitas e despesas
    /// (lançamentos) e transferências recebidas (destino) e enviadas (origem), agregadas no PostgreSQL sem carregar
    /// movimentações. Sem movimentações no período, todas são zero.
    /// </summary>
    Task<AccountTotals> SumMovementsAsync(
        Guid workspaceId, Guid accountId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>
    /// Somas das previsões PENDENTES da conta com DueDate ≤ <paramref name="through"/>, separando vencidas
    /// (DueDate &lt; <paramref name="baseDate"/>) das demais, agregadas no PostgreSQL. Pendente = não cancelada e sem
    /// lançamento efetivo ativo. Não filtra pela data do saldo inicial nem pela situação de conta/categoria.
    /// </summary>
    Task<PlannedTotals> SumPendingPlannedAsync(
        Guid workspaceId, Guid accountId, DateOnly baseDate, DateOnly through, CancellationToken cancellationToken);

    Task<CreditCardProjectionTotals> SumCreditCardObligationsAsync(Guid workspaceId, Guid accountId, DateOnly through, CancellationToken cancellationToken);

    /// <summary>
    /// Executa <paramref name="read"/> numa transação somente leitura REPEATABLE READ: todas as consultas enxergam o
    /// MESMO snapshot do banco. Uma realização (ou cancelamento) concorrente aparece inteira ou não aparece, então o
    /// lançamento efetivo e a previsão nunca são contados juntos nem ambos esquecidos.
    /// </summary>
    Task<T> ReadConsistentAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken);
}

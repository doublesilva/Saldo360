using Saldo360.Application.Transactions;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Transactions;

namespace Saldo360.Application.Planning;

public interface IFinancialPlannedTransactionRepository : IReferenceLookup
{
    Task AddAsync(FinancialPlannedTransaction planned, CancellationToken cancellationToken);

    Task<FinancialPlannedTransactionDto?> GetAsync(Guid workspaceId, Guid plannedId, CancellationToken cancellationToken);

    Task<PlannedTransactionPage> ListAsync(Guid workspaceId, PlannedTransactionFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Executa <paramref name="work"/> numa transação de banco com a linha da previsão bloqueada
    /// (<c>SELECT … FOR UPDATE</c>): realizar, editar e cancelar a previsão e cancelar o lançamento vinculado ficam
    /// serializados. Previsão inexistente na família → <paramref name="work"/> recebe null. Ao terminar, grava as
    /// alterações rastreadas e confirma; se <paramref name="work"/> lançar, desfaz tudo. Violação do índice de
    /// realização ativa única vira <see cref="RealizationConflictException"/>.
    /// </summary>
    Task<T> RunLockedAsync<T>(
        Guid workspaceId, Guid plannedId, Func<IPlannedTransactionLock?, Task<T>> work, CancellationToken cancellationToken);

    Task<bool> HasActiveReconciliationAsync(Guid workspaceId, Guid plannedId, CancellationToken cancellationToken);

    Task<bool> HasServiceBillConfirmationAsync(Guid workspaceId, Guid plannedId, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Previsão bloqueada e seu lançamento efetivo ativo, ambos carregados depois do bloqueio (dados atuais).</summary>
public interface IPlannedTransactionLock
{
    /// <summary>A previsão (rastreada para alteração).</summary>
    FinancialPlannedTransaction Planned { get; }

    /// <summary>Lançamento vinculado NÃO cancelado (rastreado), ou null se a previsão não está realizada.</summary>
    FinancialTransaction? ActiveRealization { get; }

    /// <summary>Inclui o lançamento efetivo novo, gravado na mesma transação.</summary>
    void AddRealization(FinancialTransaction transaction);
}

/// <summary>O banco recusou uma segunda realização ativa da mesma previsão (corrida perdida).</summary>
public sealed class RealizationConflictException(Exception inner)
    : Exception("A previsão já tem um lançamento efetivo ativo.", inner);

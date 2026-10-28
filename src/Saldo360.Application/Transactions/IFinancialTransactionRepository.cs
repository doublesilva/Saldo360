using Saldo360.Domain.Transactions;

namespace Saldo360.Application.Transactions;

public interface IFinancialTransactionRepository : IReferenceLookup
{
    Task AddAsync(FinancialTransaction transaction, CancellationToken cancellationToken);

    /// <summary>O lançamento, se pertencer à família (rastreado para atualização).</summary>
    Task<FinancialTransaction?> FindAsync(Guid workspaceId, Guid transactionId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<FinancialTransactionDto?> GetAsync(Guid workspaceId, Guid transactionId, CancellationToken cancellationToken);

    Task<TransactionPage> ListAsync(Guid workspaceId, TransactionFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Se o lançamento existe na família e a qual previsão está vinculado, sem rastrear a entidade (para decidir se
    /// o cancelamento precisa bloquear a previsão antes de carregá-la). null se não existir nesta família.
    /// </summary>
    Task<TransactionLink?> GetLinkAsync(Guid workspaceId, Guid transactionId, CancellationToken cancellationToken);

    /// <summary>
    /// Registra no rastro da integração que a categoria do lançamento importado foi escolhida pelo usuário (T09.3), para que as
    /// sincronizações seguintes nunca a sobrescrevam. Altera apenas entidades rastreadas; quem chama salva.
    /// </summary>
    Task RecordUserCategoryAsync(Guid workspaceId, Guid transactionId, Guid? categoryId, DateTimeOffset now, CancellationToken cancellationToken);
}

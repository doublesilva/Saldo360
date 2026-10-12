using Saldo360.Domain.Transactions;

namespace Saldo360.Application.Transactions;

public interface IFinancialTransactionRepository : IReferenceLookup
{
    Task AddAsync(FinancialTransaction transaction, CancellationToken cancellationToken);

    /// <summary>O lançamento, se pertencer à família (rastreado para atualização).</summary>
    Task<FinancialTransaction?> FindAsync(Guid familyId, Guid transactionId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<FinancialTransactionDto?> GetAsync(Guid familyId, Guid transactionId, CancellationToken cancellationToken);

    Task<TransactionPage> ListAsync(Guid familyId, TransactionFilter filter, CancellationToken cancellationToken);
}

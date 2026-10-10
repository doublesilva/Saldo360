using Saldo360.Domain.Transactions;

namespace Saldo360.Application.Transactions;

public interface IFinancialTransactionRepository
{
    Task AddAsync(FinancialTransaction transaction, CancellationToken cancellationToken);

    /// <summary>O lançamento, se pertencer à família (rastreado para atualização).</summary>
    Task<FinancialTransaction?> FindAsync(Guid familyId, Guid transactionId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<FinancialTransactionDto?> GetAsync(Guid familyId, Guid transactionId, CancellationToken cancellationToken);

    Task<TransactionPage> ListAsync(Guid familyId, TransactionFilter filter, CancellationToken cancellationToken);

    /// <summary>A conta, se pertencer à família; null caso contrário (inexistente ou de outra família).</summary>
    Task<AccountState?> GetAccountStateAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>A categoria, se pertencer à família; null caso contrário.</summary>
    Task<CategoryState?> GetCategoryStateAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken);
}

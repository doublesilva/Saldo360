using Saldo360.Application.Transactions;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.Planning;

public interface IFinancialPlannedTransactionRepository : IReferenceLookup
{
    Task AddAsync(FinancialPlannedTransaction planned, CancellationToken cancellationToken);

    /// <summary>A previsão, se pertencer à família (rastreada para atualização).</summary>
    Task<FinancialPlannedTransaction?> FindAsync(Guid familyId, Guid plannedId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<FinancialPlannedTransactionDto?> GetAsync(Guid familyId, Guid plannedId, CancellationToken cancellationToken);

    Task<PlannedTransactionPage> ListAsync(Guid familyId, PlannedTransactionFilter filter, CancellationToken cancellationToken);
}

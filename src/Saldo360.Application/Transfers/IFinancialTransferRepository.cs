using Saldo360.Domain.Transfers;

namespace Saldo360.Application.Transfers;

public interface IFinancialTransferRepository
{
    Task AddAsync(FinancialTransfer transfer, CancellationToken cancellationToken);

    /// <summary>A transferência, se pertencer à família (rastreada para cancelamento).</summary>
    Task<FinancialTransfer?> FindAsync(Guid familyId, Guid transferId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<FinancialTransferDto?> GetAsync(Guid familyId, Guid transferId, CancellationToken cancellationToken);

    Task<TransferPage> ListAsync(Guid familyId, TransferFilter filter, CancellationToken cancellationToken);

    /// <summary>Se a conta está ativa; null se ela não existir nesta família (inclusive se for de outra).</summary>
    Task<bool?> IsAccountActiveAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken);
}

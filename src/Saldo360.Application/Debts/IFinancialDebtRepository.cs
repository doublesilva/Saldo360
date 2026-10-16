using Saldo360.Domain.Debts;
namespace Saldo360.Application.Debts;
public interface IFinancialDebtRepository { Task AddAsync(FinancialDebt debt, CancellationToken ct); Task<FinancialDebt?> FindAsync(Guid familyId, Guid id, CancellationToken ct); Task<DebtDto?> GetAsync(Guid familyId, Guid id, CancellationToken ct); Task<IReadOnlyList<DebtDto>> ListAsync(Guid familyId, bool includeArchived, CancellationToken ct); Task SaveAsync(CancellationToken ct); }

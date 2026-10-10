using Saldo360.Domain.Accounts;

namespace Saldo360.Application.Accounts;

public interface IFinancialAccountRepository
{
    Task AddAsync(FinancialAccount account, CancellationToken cancellationToken);

    /// <summary>A conta, se pertencer à família (rastreada para atualização).</summary>
    Task<FinancialAccount?> FindAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FinancialAccountDto>> ListAsync(Guid familyId, bool includeInactive, CancellationToken cancellationToken);

    Task<FinancialAccountDto?> GetAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>Se o vínculo (FamilyMember.Id) pertence à família.</summary>
    Task<bool> IsMemberOfFamilyAsync(Guid familyId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>Saldo inicial configurado da conta (ativa ou não), se ela pertencer à família; null caso contrário.</summary>
    Task<AccountOpeningBalance?> GetOpeningBalanceAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Somas de receitas e despesas não canceladas da conta com <paramref name="from"/> ≤ data ≤ <paramref name="to"/>,
    /// agregadas no PostgreSQL (sem carregar os lançamentos). Sem lançamentos no período, ambas são zero.
    /// </summary>
    Task<AccountTotals> SumTransactionsAsync(
        Guid familyId, Guid accountId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

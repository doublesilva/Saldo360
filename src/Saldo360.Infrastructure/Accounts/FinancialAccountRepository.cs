using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Accounts;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Accounts;

internal sealed class FinancialAccountRepository(Saldo360DbContext db) : IFinancialAccountRepository
{
    private const string UnnamedMember = "Membro";

    public async Task AddAsync(FinancialAccount account, CancellationToken cancellationToken)
    {
        db.FinancialAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<FinancialAccount?> FindAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken) =>
        db.FinancialAccounts.SingleOrDefaultAsync(a => a.Id == accountId && a.FamilyId == familyId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public async Task<IReadOnlyList<FinancialAccountDto>> ListAsync(
        Guid familyId, bool includeInactive, CancellationToken cancellationToken)
    {
        var rows = await Project(db.FinancialAccounts
                .Where(a => a.FamilyId == familyId && (includeInactive || a.IsActive)))
            .ToListAsync(cancellationToken);

        // Poucas contas por família: ordena em memória (ativas primeiro, depois por nome).
        return rows
            .OrderByDescending(row => row.IsActive)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToDto)
            .ToList();
    }

    public async Task<FinancialAccountDto?> GetAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken)
    {
        var row = await Project(db.FinancialAccounts.Where(a => a.FamilyId == familyId && a.Id == accountId))
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToDto(row);
    }

    public Task<bool> IsMemberOfFamilyAsync(Guid familyId, Guid memberId, CancellationToken cancellationToken) =>
        db.FamilyMembers.AnyAsync(m => m.Id == memberId && m.FamilyId == familyId, cancellationToken);

    public Task<AccountOpeningBalance?> GetOpeningBalanceAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken) =>
        db.FinancialAccounts
            .Where(a => a.FamilyId == familyId && a.Id == accountId)
            .Select(a => new AccountOpeningBalance(a.OpeningBalance, a.OpeningBalanceDate))
            .SingleOrDefaultAsync(cancellationToken);

    // Duas consultas agregadas no PostgreSQL (numeric, sem perda), uma por tabela: SUM condicional por tipo
    // nos lançamentos e por papel da conta (destino/origem) nas transferências. O período é o desta conta, então
    // uma transferência pode entrar aqui e já estar no saldo inicial da outra ponta. Nada no período → zero.
    public async Task<AccountTotals> SumMovementsAsync(
        Guid familyId, Guid accountId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var transactions = await db.FinancialTransactions
            .Where(t => t.FamilyId == familyId && t.AccountId == accountId && !t.IsCancelled
                && t.TransactionDate >= from && t.TransactionDate <= to)
            .GroupBy(t => t.AccountId)
            .Select(g => new
            {
                Income = g.Sum(t => t.Type == CategoryType.Income ? t.Amount : 0m),
                Expense = g.Sum(t => t.Type == CategoryType.Expense ? t.Amount : 0m),
            })
            .SingleOrDefaultAsync(cancellationToken);

        var transfers = await db.FinancialTransfers
            .Where(t => t.FamilyId == familyId && (t.SourceAccountId == accountId || t.DestinationAccountId == accountId)
                && !t.IsCancelled && t.TransferDate >= from && t.TransferDate <= to)
            .GroupBy(t => t.FamilyId)
            .Select(g => new
            {
                In = g.Sum(t => t.DestinationAccountId == accountId ? t.Amount : 0m),
                Out = g.Sum(t => t.SourceAccountId == accountId ? t.Amount : 0m),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return new AccountTotals(
            transactions?.Income ?? 0m, transactions?.Expense ?? 0m, transfers?.In ?? 0m, transfers?.Out ?? 0m);
    }

    // Responsável: junção com o vínculo da mesma família e o nome de exibição do usuário.
    private IQueryable<AccountRow> Project(IQueryable<FinancialAccount> accounts) =>
        from account in accounts.AsNoTracking()
        join member in db.FamilyMembers
            on new { account.FamilyId, MemberId = account.ResponsibleMemberId }
            equals new { member.FamilyId, MemberId = (Guid?)member.Id } into responsibles
        from member in responsibles.DefaultIfEmpty()
        join user in db.Users on member.UserId equals user.Id into users
        from user in users.DefaultIfEmpty()
        select new AccountRow(
            account.Id,
            account.Name,
            account.Institution,
            account.Type,
            account.Scope,
            account.ResponsibleMemberId,
            user.DisplayName,
            account.OpeningBalance,
            account.OpeningBalanceDate,
            account.IsActive,
            account.Currency,
            account.CreatedAt);

    private static FinancialAccountDto ToDto(AccountRow row) => new(
        row.Id,
        row.Name,
        row.Institution,
        row.Type,
        row.Scope,
        row.ResponsibleMemberId is { } memberId ? new AccountResponsibleDto(memberId, row.ResponsibleName ?? UnnamedMember) : null,
        row.OpeningBalance,
        row.OpeningBalanceDate,
        row.IsActive,
        row.Currency,
        row.CreatedAt);

    private sealed record AccountRow(
        Guid Id,
        string Name,
        string? Institution,
        AccountType Type,
        FinancialScope Scope,
        Guid? ResponsibleMemberId,
        string? ResponsibleName,
        decimal? OpeningBalance,
        DateOnly? OpeningBalanceDate,
        bool IsActive,
        string Currency,
        DateTimeOffset CreatedAt);
}

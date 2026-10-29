using System.Data;
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

    public Task<FinancialAccount?> FindAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken) =>
        db.FinancialAccounts.SingleOrDefaultAsync(a => a.Id == accountId && a.WorkspaceId == workspaceId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public Task<FinancialAccount?> FindPrimaryAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        db.FinancialAccounts.SingleOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.IsPrimary, cancellationToken);

    // Mesmo padrão do motor de lançamentos: transação própria (ou a corrente) com o workspace bloqueado.
    public async Task<T> RunExclusiveAsync<T>(Guid workspaceId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id = {workspaceId} FOR UPDATE", cancellationToken);
            var result = await work();
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public async Task<IReadOnlyList<FinancialAccountDto>> ListAsync(
        Guid workspaceId, bool includeInactive, CancellationToken cancellationToken)
    {
        var rows = await Project(db.FinancialAccounts
                .Where(a => a.WorkspaceId == workspaceId && (includeInactive || a.IsActive)))
            .ToListAsync(cancellationToken);

        // Poucas contas por família: ordena em memória (ativas primeiro, depois por nome).
        return rows
            .OrderByDescending(row => row.IsActive)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToDto)
            .ToList();
    }

    public async Task<FinancialAccountDto?> GetAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken)
    {
        var row = await Project(db.FinancialAccounts.Where(a => a.WorkspaceId == workspaceId && a.Id == accountId))
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToDto(row);
    }

    public Task<bool> IsWorkspaceOwnerAsync(Guid workspaceId, Guid memberId, CancellationToken cancellationToken) =>
        db.FinancialWorkspaces.AnyAsync(w => w.OwnerUserId == memberId && w.Id == workspaceId && w.IsActive && w.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal, cancellationToken);

    public async Task<AccountOpeningBalance?> GetOpeningBalanceAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.FinancialAccounts.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.Id == accountId)
            .Select(a => new { a.OpeningBalance, a.OpeningBalanceDate }).SingleOrDefaultAsync(cancellationToken);
        if (account is null) return null;
        if (account.OpeningBalance is not null && account.OpeningBalanceDate is not null) return new AccountOpeningBalance(account.OpeningBalance, account.OpeningBalanceDate, "Manual");
        var anchor = await db.FinancialAccountBalanceAnchors.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId && a.AccountId == accountId && a.IsCurrent && a.Reliability != Saldo360.Domain.Accounts.BalanceAnchorReliability.Unreliable
                && a.DerivedOpeningAmount != null && a.DerivedOpeningDate != null)
            .Select(a => new { a.DerivedOpeningAmount, a.DerivedOpeningDate }).SingleOrDefaultAsync(cancellationToken);
        return anchor is null ? new AccountOpeningBalance(account.OpeningBalance, account.OpeningBalanceDate) : new AccountOpeningBalance(anchor.DerivedOpeningAmount, anchor.DerivedOpeningDate, "BalanceAnchor");
    }

    public async Task<IReadOnlyDictionary<Guid, AccountOpeningBalance>> GetAnchoredOpeningsAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var rows = await (from anchor in db.FinancialAccountBalanceAnchors.AsNoTracking()
                          join account in db.FinancialAccounts.AsNoTracking() on anchor.AccountId equals account.Id
                          where anchor.WorkspaceId == workspaceId && account.WorkspaceId == workspaceId && anchor.IsCurrent
                              && anchor.Reliability != Saldo360.Domain.Accounts.BalanceAnchorReliability.Unreliable && anchor.DerivedOpeningAmount != null && anchor.DerivedOpeningDate != null
                              && (account.OpeningBalance == null || account.OpeningBalanceDate == null)
                          select new { anchor.AccountId, anchor.DerivedOpeningAmount, anchor.DerivedOpeningDate }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.AccountId, r => new AccountOpeningBalance(r.DerivedOpeningAmount, r.DerivedOpeningDate, "BalanceAnchor"));
    }

    // Duas consultas agregadas no PostgreSQL (numeric, sem perda), uma por tabela: SUM condicional por tipo
    // nos lançamentos e por papel da conta (destino/origem) nas transferências. O período é o desta conta, então
    // uma transferência pode entrar aqui e já estar no saldo inicial da outra ponta. Nada no período → zero.
    public async Task<AccountTotals> SumMovementsAsync(
        Guid workspaceId, Guid accountId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var transactions = await db.FinancialTransactions
            .Where(t => t.WorkspaceId == workspaceId && t.AccountId == accountId && !t.IsCancelled
                && t.TransactionDate >= from && t.TransactionDate <= to)
            .GroupBy(t => t.AccountId)
            .Select(g => new
            {
                Income = g.Sum(t => t.Type == CategoryType.Income ? t.Amount : 0m),
                Expense = g.Sum(t => t.Type == CategoryType.Expense ? t.Amount : 0m),
            })
            .SingleOrDefaultAsync(cancellationToken);

        var transfers = await db.FinancialTransfers
            .Where(t => t.WorkspaceId == workspaceId && (t.SourceAccountId == accountId || t.DestinationAccountId == accountId)
                && !t.IsCancelled && t.TransferDate >= from && t.TransferDate <= to)
            .GroupBy(t => t.WorkspaceId)
            .Select(g => new
            {
                In = g.Sum(t => t.DestinationAccountId == accountId ? t.Amount : 0m),
                Out = g.Sum(t => t.SourceAccountId == accountId ? t.Amount : 0m),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return new AccountTotals(
            transactions?.Income ?? 0m, transactions?.Expense ?? 0m, transfers?.In ?? 0m, transfers?.Out ?? 0m);
    }

    // Uma consulta: SUM condicional por tipo e por vencimento (antes da data-base = vencida) sobre as previsões
    // pendentes. Pendente = não cancelada e sem lançamento ativo vinculado (NOT EXISTS, a mesma regra que deriva
    // "Realized"). Sem filtro por data do saldo inicial nem por situação de conta/categoria.
    public async Task<PlannedTotals> SumPendingPlannedAsync(
        Guid workspaceId, Guid accountId, DateOnly baseDate, DateOnly through, CancellationToken cancellationToken)
    {
        var totals = await db.FinancialPlannedTransactions
            .Where(p => p.WorkspaceId == workspaceId && p.AccountId == accountId && !p.IsCancelled && p.DueDate <= through
                && !db.FinancialTransactions.Any(t => t.WorkspaceId == p.WorkspaceId && t.PlannedTransactionId == p.Id && !t.IsCancelled)
                && !db.FinancialPlannedInvoiceReconciliations.Any(r => r.WorkspaceId == p.WorkspaceId && r.PlannedTransactionId == p.Id && r.RemovedAt == null))
            .GroupBy(p => p.AccountId)
            .Select(g => new PlannedTotals(
                g.Sum(p => p.DueDate < baseDate && p.Type == CategoryType.Income ? p.Amount : 0m),
                g.Sum(p => p.DueDate < baseDate && p.Type == CategoryType.Expense ? p.Amount : 0m),
                g.Sum(p => p.DueDate >= baseDate && p.Type == CategoryType.Income ? p.Amount : 0m),
                g.Sum(p => p.DueDate >= baseDate && p.Type == CategoryType.Expense ? p.Amount : 0m)))
            .SingleOrDefaultAsync(cancellationToken);
        return totals ?? new PlannedTotals(0m, 0m, 0m, 0m);
    }

    public async Task<CreditCardProjectionTotals> SumCreditCardObligationsAsync(Guid workspaceId, Guid accountId, DateOnly through, CancellationToken cancellationToken)
    {
        var obligations = await (from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                                 join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on installment.PurchaseId equals purchase.Id
                                 join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on installment.InvoiceId equals invoice.Id
                                 join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.WorkspaceId, CardId = invoice.CreditCardId } equals new { card.WorkspaceId, CardId = card.Id }
                                 where installment.WorkspaceId == workspaceId && installment.Status == Saldo360.Domain.CreditCards.CreditCardInstallmentStatus.Active
                                    && purchase.WorkspaceId == workspaceId && purchase.Status == Saldo360.Domain.CreditCards.CreditCardPurchaseStatus.Active
                                    && invoice.WorkspaceId == workspaceId && invoice.DueDate <= through
                                 group installment.Amount by new { invoice.Id, card.DefaultPaymentAccountId } into grouped
                                 select new { grouped.Key.Id, grouped.Key.DefaultPaymentAccountId, Total = grouped.Sum() }).ToListAsync(cancellationToken);
        var historicalObligations = await (from item in db.FinancialCreditCardImportedInstallments.AsNoTracking()
                                           join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on item.InvoiceId equals invoice.Id
                                           join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.WorkspaceId, CardId = invoice.CreditCardId } equals new { card.WorkspaceId, CardId = card.Id }
                                           where item.WorkspaceId == workspaceId && item.IsHistorical && item.Status == Saldo360.Domain.CreditCards.FinancialCreditCardImportedInstallmentStatus.Active
                                              && invoice.WorkspaceId == workspaceId && invoice.DueDate <= through
                                           group item.Amount by new { invoice.Id, card.DefaultPaymentAccountId } into grouped
                                           select new { grouped.Key.Id, grouped.Key.DefaultPaymentAccountId, Total = grouped.Sum() }).ToListAsync(cancellationToken);
        var adjustmentObligations = await (from item in db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
                                           join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on item.InvoiceId equals invoice.Id
                                           join card in db.FinancialCreditCards.AsNoTracking() on new { invoice.WorkspaceId, CardId = invoice.CreditCardId } equals new { card.WorkspaceId, CardId = card.Id }
                                           where item.WorkspaceId == workspaceId && item.Status == Saldo360.Domain.CreditCards.FinancialCreditCardInvoiceAdjustmentStatus.Active
                                              && invoice.WorkspaceId == workspaceId && invoice.DueDate <= through
                                           group item by new { invoice.Id, card.DefaultPaymentAccountId } into grouped
                                           select new { grouped.Key.Id, grouped.Key.DefaultPaymentAccountId, Total = grouped.Sum(item => item.Nature == Saldo360.Domain.CreditCards.FinancialCreditCardInvoiceAdjustmentNature.Debit ? item.Amount : -item.Amount) }).ToListAsync(cancellationToken);
        obligations = obligations.Concat(historicalObligations).Concat(adjustmentObligations)
            .GroupBy(item => new { item.Id, item.DefaultPaymentAccountId })
            .Select(group => new { group.Key.Id, group.Key.DefaultPaymentAccountId, Total = group.Sum(item => item.Total) })
            .ToList();
        var paid = await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(payment => payment.WorkspaceId == workspaceId && payment.Status == Saldo360.Domain.CreditCards.FinancialCreditCardInvoicePaymentStatus.Active)
            .GroupBy(payment => payment.InvoiceId).Select(group => new { Id = group.Key, Amount = group.Sum(payment => payment.Amount) }).ToListAsync(cancellationToken);
        var familyAccounts = await db.FinancialAccounts.AsNoTracking().Where(account => account.WorkspaceId == workspaceId).Select(account => account.Id).ToListAsync(cancellationToken);
        var paidByInvoice = paid.ToDictionary(item => item.Id, item => item.Amount);
        var outstanding = obligations.Where(item => item.DefaultPaymentAccountId == accountId).Sum(item => Math.Max(0m, item.Total - paidByInvoice.GetValueOrDefault(item.Id)));
        var unallocated = obligations.Any(item => item.DefaultPaymentAccountId is null || !familyAccounts.Contains(item.DefaultPaymentAccountId.Value));
        var known = await (from item in db.FinancialCreditCardImportedInstallments.AsNoTracking()
                           join card in db.FinancialCreditCards.AsNoTracking() on new { item.WorkspaceId, item.CreditCardId } equals new { card.WorkspaceId, CreditCardId = card.Id }
                           where item.WorkspaceId == workspaceId && item.InvoiceId == null && item.IsHistorical
                               && item.Status == Saldo360.Domain.CreditCards.FinancialCreditCardImportedInstallmentStatus.Active
                               && (item.DueDate == null || item.DueDate <= through)
                           select new { item.Amount, item.DueDate, card.DefaultPaymentAccountId }).ToListAsync(cancellationToken);
        outstanding += known.Where(x => x.DefaultPaymentAccountId == accountId && x.DueDate != null).Sum(x => x.Amount);
        unallocated |= known.Any(x => x.DueDate == null || x.DefaultPaymentAccountId == null || !familyAccounts.Contains(x.DefaultPaymentAccountId.Value));
        return new CreditCardProjectionTotals(outstanding, unallocated);
    }

    // REPEATABLE READ no PostgreSQL = um snapshot para a transação inteira (tirado na primeira consulta). Somente
    // leitura: nada é gravado, e não há conflito de serialização possível.
    public async Task<T> ReadConsistentAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await read();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var result = await read();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    // Responsável: junção com o vínculo da mesma família e o nome de exibição do usuário.
    private IQueryable<AccountRow> Project(IQueryable<FinancialAccount> accounts) =>
        from account in accounts.AsNoTracking()
        join user in db.Users on account.ResponsibleUserId equals (Guid?)user.Id into users
        from user in users.DefaultIfEmpty()
        join link in db.PluggyAccountLinks on account.Id equals link.FinancialAccountId into links
        from link in links.DefaultIfEmpty()
        select new AccountRow(
            account.Id,
            account.Name,
            account.Institution,
            account.Type,
            account.Scope,
            account.ResponsibleUserId,
            user.DisplayName,
            account.OpeningBalance,
            account.OpeningBalanceDate,
            account.IsActive,
            account.Currency,
            account.CreatedAt,
            account.IsPrimary,
            link == null ? null : new AccountIntegrationDto(link.Id, link.ConnectionId, link.DisplayName, link.InstitutionName, link.Status.ToString(), link.LastSyncedAt,
                link.ImportFrom, link.ObservedBalance, link.ObservedAt));

    private static FinancialAccountDto ToDto(AccountRow row) => new(
        row.Id,
        row.Name,
        row.Institution,
        row.Type,
        row.Scope,
        row.ResponsibleUserId is { } memberId ? new AccountResponsibleDto(memberId, row.ResponsibleName ?? UnnamedMember) : null,
        row.OpeningBalance,
        row.OpeningBalanceDate,
        row.IsActive,
        row.Currency,
        row.CreatedAt,
        row.Integration is null ? AccountOrigins.Manual : AccountOrigins.Pluggy,
        row.Integration,
        row.IsPrimary);

    private sealed record AccountRow(
        Guid Id,
        string Name,
        string? Institution,
        AccountType Type,
        FinancialScope Scope,
        Guid? ResponsibleUserId,
        string? ResponsibleName,
        decimal? OpeningBalance,
        DateOnly? OpeningBalanceDate,
        bool IsActive,
        string Currency,
        DateTimeOffset CreatedAt,
        bool IsPrimary,
        AccountIntegrationDto? Integration);
}

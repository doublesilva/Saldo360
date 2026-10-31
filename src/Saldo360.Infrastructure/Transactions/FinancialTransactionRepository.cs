using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Transactions;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Transactions;

internal sealed class FinancialTransactionRepository(Saldo360DbContext db) : IFinancialTransactionRepository
{
    public async Task AddAsync(FinancialTransaction transaction, CancellationToken cancellationToken)
    {
        db.FinancialTransactions.Add(transaction);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<FinancialTransaction?> FindAsync(Guid workspaceId, Guid transactionId, CancellationToken cancellationToken) =>
        db.FinancialTransactions.SingleOrDefaultAsync(t => t.Id == transactionId && t.WorkspaceId == workspaceId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public Task<FinancialTransactionDto?> GetAsync(Guid workspaceId, Guid transactionId, CancellationToken cancellationToken) =>
        Project(db.FinancialTransactions.Where(t => t.WorkspaceId == workspaceId && t.Id == transactionId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<TransactionPage> ListAsync(Guid workspaceId, TransactionFilter filter, CancellationToken cancellationToken)
    {
        if (filter.IncludeTransfers) return await ListWithTransfersAsync(workspaceId, filter, cancellationToken);
        var query = db.FinancialTransactions.Where(t => t.WorkspaceId == workspaceId);
        if (!filter.IncludeCancelled)
        {
            query = query.Where(t => !t.IsCancelled);
        }

        if (filter.From is { } from)
        {
            query = query.Where(t => t.TransactionDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(t => t.TransactionDate <= to);
        }

        if (filter.AccountId is { } accountId)
        {
            query = query.Where(t => t.AccountId == accountId);
        }

        if (filter.CategoryId is { } categoryId)
        {
            // Categoria principal: os lançamentos diretos e os das suas subcategorias, cada registro uma só vez (a classificação é um único categoryId).
            // Subcategoria: só os dela.
            query = query.Where(t => t.CategoryId == categoryId
                || db.FinancialCategories.Any(c => c.WorkspaceId == workspaceId && c.Id == t.CategoryId && c.ParentCategoryId == categoryId));
        }

        if (filter.Nature is { } nature)
        {
            query = query.Where(t => t.Nature == nature);
        }

        if (filter.Type is { } type)
        {
            query = query.Where(t => t.Type == type);
        }

        if (filter.ExcludeInvoicePayments)
        {
            // O pagamento da fatura (manual ou do banco) não é uma despesa a mais: o consumo já está nas compras do cartão.
            query = query.Where(t => !db.FinancialCreditCardInvoicePayments.Any(p => p.WorkspaceId == workspaceId && p.TransactionId == t.Id && p.Status == FinancialCreditCardInvoicePaymentStatus.Active));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return new TransactionPage(items, filter.Page, filter.PageSize, total);
    }

    /// <summary>
    /// Extrato com as transferências como lançamentos (opt-in): cada transferência entre contas próprias aparece duas vezes, saída na origem e entrada no destino, lidas direto
    /// do registro canônico <c>FinancialTransfer</c> (nada é duplicado no banco). Receita/Despesa continuam só no que já eram; categoria e natureza não se aplicam a transferência.
    /// Paginação por mescla: cada fonte entrega até page*pageSize linhas na ordem comum (data, Id decrescentes) e a página é o recorte da mescla.
    /// </summary>
    private async Task<TransactionPage> ListWithTransfersAsync(Guid workspaceId, TransactionFilter filter, CancellationToken ct)
    {
        var take = filter.Page * filter.PageSize;
        var plain = filter with { IncludeTransfers = false, Page = 1, PageSize = take };
        var includeTransferEnds = filter.TransfersOnly || (filter.Type is null && filter.CategoryId is null && filter.Nature is null && !filter.ExcludeInvoicePayments);
        var total = 0;
        var merged = new List<FinancialTransactionDto>();
        if (!filter.TransfersOnly)
        {
            var page = await ListAsync(workspaceId, plain, ct);
            merged.AddRange(page.Items);
            total += page.TotalCount;
        }
        if (includeTransferEnds)
        {
            var transfers = db.FinancialTransfers.AsNoTracking().Where(t => t.WorkspaceId == workspaceId);
            if (!filter.IncludeCancelled) transfers = transfers.Where(t => !t.IsCancelled);
            if (filter.From is { } from) transfers = transfers.Where(t => t.TransferDate >= from);
            if (filter.To is { } to) transfers = transfers.Where(t => t.TransferDate <= to);
            if (filter.AccountId is { } accountId) transfers = transfers.Where(t => t.SourceAccountId == accountId || t.DestinationAccountId == accountId);
            var count = await transfers.CountAsync(ct);
            total += filter.AccountId is null ? count * 2 : count;
            var rows = await (from t in transfers.OrderByDescending(t => t.TransferDate).ThenByDescending(t => t.Id).Take(take)
                              join s in db.FinancialAccounts on new { t.WorkspaceId, Id = t.SourceAccountId } equals new { s.WorkspaceId, s.Id }
                              join d in db.FinancialAccounts on new { t.WorkspaceId, Id = t.DestinationAccountId } equals new { d.WorkspaceId, d.Id }
                              select new
                              {
                                  t.Id, t.Amount, t.TransferDate, t.Description, t.IsCancelled, t.Currency, t.CreatedAt, t.CancelledAt,
                                  SourceId = s.Id, SourceName = s.Name, SourceActive = s.IsActive, DestinationId = d.Id, DestinationName = d.Name, DestinationActive = d.IsActive,
                                  FromBank = db.PluggyPostings.Any(p => p.FinancialTransferId == t.Id),
                              }).ToListAsync(ct);
            foreach (var r in rows)
            {
                var origin = r.FromBank ? FinancialTransactionOrigin.PluggyImport : FinancialTransactionOrigin.Manual;
                var tail = string.IsNullOrWhiteSpace(r.Description) ? string.Empty : $" · {r.Description}";
                if (filter.AccountId is null || filter.AccountId == r.DestinationId)
                    merged.Add(new(r.Id, Saldo360.Domain.Categories.CategoryType.Income, r.Amount, r.TransferDate, $"Transferência recebida de {r.SourceName}{tail}",
                        new TransactionReferenceDto(r.DestinationId, r.DestinationName, r.DestinationActive), null, r.IsCancelled, r.Currency, r.CreatedAt, null, r.CancelledAt, null, origin,
                        Kind: "TransferIn", TransferId: r.Id, CounterpartAccountName: r.SourceName));
                if (filter.AccountId is null || filter.AccountId == r.SourceId)
                    merged.Add(new(r.Id, Saldo360.Domain.Categories.CategoryType.Expense, r.Amount, r.TransferDate, $"Transferência enviada para {r.DestinationName}{tail}",
                        new TransactionReferenceDto(r.SourceId, r.SourceName, r.SourceActive), null, r.IsCancelled, r.Currency, r.CreatedAt, null, r.CancelledAt, null, origin,
                        Kind: "TransferOut", TransferId: r.Id, CounterpartAccountName: r.DestinationName));
            }
        }
        var items = merged.OrderByDescending(i => i.TransactionDate).ThenByDescending(i => i.Id).ThenBy(i => i.Kind, StringComparer.Ordinal)
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList();
        return new TransactionPage(items, filter.Page, filter.PageSize, total);
    }

    public Task<TransactionLink?> GetLinkAsync(Guid workspaceId, Guid transactionId, CancellationToken cancellationToken) =>
        db.FinancialTransactions
            .AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && t.Id == transactionId)
            .Select(t => new TransactionLink(t.PlannedTransactionId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task RecordUserCategoryAsync(Guid workspaceId, Guid transactionId, Guid? categoryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var posting = await db.PluggyPostings.SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.FinancialTransactionId == transactionId, cancellationToken);
        posting?.SetCategory(categoryId, Saldo360.Domain.Pluggy.PluggyCategorySource.User, now);
    }

    public Task<AccountState?> GetAccountStateAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken) =>
        db.FinancialAccounts
            .Where(a => a.WorkspaceId == workspaceId && a.Id == accountId)
            .Select(a => new AccountState(a.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<CategoryState?> GetCategoryStateAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories
            .Where(c => c.WorkspaceId == workspaceId && c.Id == categoryId)
            .Select(c => new CategoryState(c.Type, c.IsActive && (c.ParentCategoryId == null
                || db.FinancialCategories.Any(p => p.Id == c.ParentCategoryId && p.IsActive))))
            .SingleOrDefaultAsync(cancellationToken);

    // Conta e categoria pela mesma família (as FKs garantem que existem). Ordena antes de projetar: data
    // decrescente e Id decrescente, estável entre páginas.
    private IQueryable<FinancialTransactionDto> Project(IQueryable<FinancialTransaction> transactions) =>
        from t in transactions.AsNoTracking()
        join a in db.FinancialAccounts on new { t.WorkspaceId, Id = t.AccountId } equals new { a.WorkspaceId, a.Id }
        join category in db.FinancialCategories on new { t.WorkspaceId, Id = t.CategoryId } equals new { category.WorkspaceId, Id = (Guid?)category.Id } into categories
        from c in categories.DefaultIfEmpty()
        orderby t.TransactionDate descending, t.Id descending
        select new FinancialTransactionDto(
            t.Id,
            t.Type,
            t.Amount,
            t.TransactionDate,
            t.Description,
            new TransactionReferenceDto(a.Id, a.Name, a.IsActive),
            c == null ? null : new TransactionReferenceDto(c.Id, c.Name, c.IsActive, db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.Name).FirstOrDefault()),
            t.IsCancelled,
            t.Currency,
            t.CreatedAt,
            t.UpdatedAt,
            t.CancelledAt,
            t.PlannedTransactionId,
            t.Origin,
            // Origem da categoria (só lançamentos importados têm rastro): Pluggy, Rule, AI ou User.
            db.PluggyPostings.Where(p => p.FinancialTransactionId == t.Id).Select(p => (Saldo360.Domain.Pluggy.PluggyCategorySource?)p.CategorySource).FirstOrDefault(),
            db.PluggyPostings.Where(p => p.FinancialTransactionId == t.Id).Select(p => p.CategoryNeedsReview).FirstOrDefault(),
            t.Nature,
            db.FinancialBankRefunds.Where(l => l.RefundTransactionId == t.Id && l.CancelledAt == null).Select(l => (Guid?)l.OriginalTransactionId).FirstOrDefault(),
            db.FinancialBankRefunds.Where(l => l.RefundTransactionId == t.Id && l.CancelledAt == null)
                .Join(db.FinancialTransactions, l => l.OriginalTransactionId, o => o.Id, (l, o) => o.Description).FirstOrDefault(),
            db.FinancialBankRefunds.Where(l => l.RefundTransactionId == t.Id && l.CancelledAt == null)
                .Join(db.FinancialTransactions, l => l.OriginalTransactionId, o => o.Id, (l, o) => (DateOnly?)o.TransactionDate).FirstOrDefault(),
            t.Origin == FinancialTransactionOrigin.PluggyRefund ? db.PluggyPostings.Where(p => p.FinancialTransactionId == t.Id).Select(p => (Guid?)p.Id).FirstOrDefault() : null,
            t.Type == Saldo360.Domain.Categories.CategoryType.Expense
                ? (db.FinancialBankRefunds.Where(l => l.OriginalTransactionId == t.Id && l.CancelledAt == null)
                    .Join(db.FinancialTransactions.Where(r => !r.IsCancelled), l => l.RefundTransactionId, r => r.Id, (l, r) => (decimal?)l.Amount).Sum() ?? 0m)
                : 0m);
}

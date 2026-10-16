using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Planning;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Persistence.Configurations;

namespace Saldo360.Infrastructure.Planning;

internal sealed class FinancialPlannedTransactionRepository(Saldo360DbContext db) : IFinancialPlannedTransactionRepository
{
    public async Task AddAsync(FinancialPlannedTransaction planned, CancellationToken cancellationToken)
    {
        db.FinancialPlannedTransactions.Add(planned);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<FinancialPlannedTransactionDto?> GetAsync(Guid familyId, Guid plannedId, CancellationToken cancellationToken) =>
        Project(db.FinancialPlannedTransactions.Where(p => p.FamilyId == familyId && p.Id == plannedId)).SingleOrDefaultAsync(cancellationToken);

    public async Task<PlannedTransactionPage> ListAsync(Guid familyId, PlannedTransactionFilter filter, CancellationToken cancellationToken)
    {
        var query = db.FinancialPlannedTransactions.Where(p => p.FamilyId == familyId);

        // Situação derivada: pendentes sempre; realizadas (lançamento ativo vinculado) e canceladas só se pedidas.
        var includeRealized = filter.IncludeRealized;
        var includeCancelled = filter.IncludeCancelled;
        query = query.Where(p => p.IsCancelled
            ? includeCancelled
            : includeRealized || !db.FinancialTransactions.Any(t =>
                t.FamilyId == p.FamilyId && t.PlannedTransactionId == p.Id && !t.IsCancelled));

        if (filter.From is { } from)
        {
            query = query.Where(p => p.DueDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(p => p.DueDate <= to);
        }

        if (filter.AccountId is { } accountId)
        {
            query = query.Where(p => p.AccountId == accountId);
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        if (filter.Type is { } type)
        {
            query = query.Where(p => p.Type == type);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return new PlannedTransactionPage(items, filter.Page, filter.PageSize, total);
    }

    public async Task<T> RunLockedAsync<T>(
        Guid familyId, Guid plannedId, Func<IPlannedTransactionLock?, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Bloqueia a linha da previsão até o fim da transação. Realizar, editar e cancelar a previsão e cancelar
            // o lançamento vinculado passam todos por aqui, então ficam em fila. Em READ COMMITTED, as leituras
            // seguintes (feitas depois de obter o bloqueio) já enxergam o que a operação anterior gravou.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM financial_planned_transactions WHERE id = {plannedId} AND family_id = {familyId} FOR UPDATE",
                cancellationToken);

            var planned = await db.FinancialPlannedTransactions
                .SingleOrDefaultAsync(p => p.Id == plannedId && p.FamilyId == familyId, cancellationToken);
            Lock? locked = null;
            if (planned is not null)
            {
                var active = await db.FinancialTransactions.SingleOrDefaultAsync(
                    t => t.FamilyId == familyId && t.PlannedTransactionId == plannedId && !t.IsCancelled, cancellationToken);
                locked = new Lock(db, planned, active);
            }

            var result = await work(locked);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: FinancialTransactionConfiguration.ActiveRealizationIndex,
        })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            throw new RealizationConflictException(exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<AccountState?> GetAccountStateAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken) =>
        db.FinancialAccounts
            .Where(a => a.FamilyId == familyId && a.Id == accountId)
            .Select(a => new AccountState(a.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<CategoryState?> GetCategoryStateAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories
            .Where(c => c.FamilyId == familyId && c.Id == categoryId)
            .Select(c => new CategoryState(c.Type, c.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<bool> HasActiveReconciliationAsync(Guid familyId, Guid plannedId, CancellationToken cancellationToken) =>
        db.FinancialPlannedInvoiceReconciliations.AnyAsync(item => item.FamilyId == familyId && item.PlannedTransactionId == plannedId && item.RemovedAt == null, cancellationToken)
        .ContinueWith(async card => card.Result || await db.FinancialDebtInstallmentReconciliations.AnyAsync(item => item.FamilyId == familyId && item.PlannedTransactionId == plannedId && item.RemovedAt == null, cancellationToken), cancellationToken).Unwrap();

    // Conta e categoria pela mesma família (as FKs garantem que existem) e o lançamento efetivo ATIVO, se houver
    // (o índice único garante no máximo um, então a junção não duplica linhas). Ordena antes de projetar: data
    // prevista crescente e Id crescente, estável entre páginas.
    private IQueryable<FinancialPlannedTransactionDto> Project(IQueryable<FinancialPlannedTransaction> planned) =>
        from p in planned.AsNoTracking()
        join a in db.FinancialAccounts on new { p.FamilyId, Id = p.AccountId } equals new { a.FamilyId, a.Id }
        join c in db.FinancialCategories on new { p.FamilyId, Id = p.CategoryId } equals new { c.FamilyId, c.Id }
        join r in db.FinancialTransactions.Where(t => !t.IsCancelled)
            on new { p.FamilyId, Id = (Guid?)p.Id } equals new { r.FamilyId, Id = r.PlannedTransactionId } into realizations
        from r in realizations.DefaultIfEmpty()
        orderby p.DueDate, p.Id
        select new FinancialPlannedTransactionDto(
            p.Id,
            p.Type,
            p.Amount,
            p.DueDate,
            p.Description,
            new TransactionReferenceDto(a.Id, a.Name, a.IsActive),
            new TransactionReferenceDto(c.Id, c.Name, c.IsActive),
            p.IsCancelled
                ? PlannedTransactionStatus.Cancelled
                : r != null ? PlannedTransactionStatus.Realized : PlannedTransactionStatus.Pending,
            r != null ? new PlannedRealizationDto(r.Id, r.Amount, r.TransactionDate) : null,
            p.IsCancelled,
            p.Currency,
            p.CreatedAt,
            p.UpdatedAt,
            p.CancelledAt);

    private sealed class Lock(Saldo360DbContext db, FinancialPlannedTransaction planned, FinancialTransaction? active)
        : IPlannedTransactionLock
    {
        public FinancialPlannedTransaction Planned => planned;

        public FinancialTransaction? ActiveRealization => active;

        public void AddRealization(FinancialTransaction transaction) => db.FinancialTransactions.Add(transaction);
    }
}

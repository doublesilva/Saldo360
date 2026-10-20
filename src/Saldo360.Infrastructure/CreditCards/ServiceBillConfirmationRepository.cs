using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class ServiceBillConfirmationRepository(Saldo360DbContext db) : IServiceBillConfirmationRepository
{
    public async Task<ServiceBillConfirmationStoreResult> ConfirmAsync(Guid workspaceId, Guid documentId, Guid userId,
        Guid occurrenceId, Guid recurrenceId, string competence, decimal confirmedAmount, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorized = await db.FinancialWorkspaces.AsNoTracking()
            .AnyAsync(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal, cancellationToken);
        if (!authorized) return new(ServiceBillConfirmationStoreStatus.Invalid);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Serializa double-submit do mesmo documento. A segunda chamada lê o estado já confirmado
        // depois que a primeira libera o lock e retorna o resultado idempotente.
        var item = await db.FinancialDocumentInboxItems
            .FromSqlInterpolated($"SELECT * FROM financial_document_inbox_items WHERE workspace_id = {workspaceId} AND id = {documentId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null) return new(ServiceBillConfirmationStoreStatus.Invalid);
        if (item.ServiceBillOccurrenceId is { } existing)
        {
            var status = existing == occurrenceId && item.ServiceBillConfirmedAmount == confirmedAmount
                ? ServiceBillConfirmationStoreStatus.AlreadyConfirmed : ServiceBillConfirmationStoreStatus.Conflict;
            return Existing(status, item);
        }
        if (await db.FinancialDocumentInboxItems.AnyAsync(x => x.WorkspaceId == workspaceId && x.ServiceBillOccurrenceId == occurrenceId, cancellationToken))
            return new(ServiceBillConfirmationStoreStatus.Conflict);
        var materialization = await db.FinancialRecurrenceMaterializations.SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId
            && x.PlannedTransactionId == occurrenceId && x.RecurrenceId == recurrenceId, cancellationToken);
        var planned = await db.FinancialPlannedTransactions.SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == occurrenceId, cancellationToken);
        if (materialization is null || planned is null || planned.Type != CategoryType.Expense || planned.IsCancelled
            || planned.DueDate.ToString("yyyy-MM") != competence
            || await db.FinancialTransactions.AnyAsync(x => x.WorkspaceId == workspaceId && x.PlannedTransactionId == occurrenceId && !x.IsCancelled, cancellationToken))
            return new(ServiceBillConfirmationStoreStatus.Invalid);
        var projected = planned.Amount;
        planned.ConfirmAmount(confirmedAmount, userId, now);
        item.ConfirmServiceBill(occurrenceId, recurrenceId, competence, projected, confirmedAmount, now);
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); }
        catch (DbUpdateException) { await transaction.RollbackAsync(cancellationToken); return new(ServiceBillConfirmationStoreStatus.Conflict); }
        return new(ServiceBillConfirmationStoreStatus.Confirmed, occurrenceId, recurrenceId, competence, projected, confirmedAmount, now);
    }

    private static ServiceBillConfirmationStoreResult Existing(ServiceBillConfirmationStoreStatus status,
        Saldo360.Domain.CreditCards.FinancialDocumentInboxItem item) => new(status, item.ServiceBillOccurrenceId,
            item.ServiceBillRecurrenceId, item.ServiceBillCompetence, item.ServiceBillProjectedAmount,
            item.ServiceBillConfirmedAmount, item.ServiceBillConfirmedAt);
}

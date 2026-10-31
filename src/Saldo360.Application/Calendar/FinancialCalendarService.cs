using Saldo360.Application.Workspaces;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Calendar;

/// <summary>Consulta unificada da agenda para qualquer integrante da família.</summary>
public sealed class FinancialCalendarService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialCalendarRepository calendar,
    Saldo360.Application.Transfers.IFinancialTransferRepository transferRepository,
    TimeProvider timeProvider)
{
    public async Task<FinancialCalendarResult> ListAsync(
        Guid workspaceId,
        Guid userId,
        FinancialCalendarQuery query,
        CancellationToken cancellationToken)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is null)
        {
            return FinancialCalendarResult.NotFoundResult;
        }

        var errors = new Dictionary<string, string[]>();
        if (query.Through < query.From)
        {
            errors["through"] = ["A data final deve ser posterior ou igual à inicial."];
        }

        CategoryType? type = null;
        if (query.Type is not null)
        {
            if (CategoryTypes.TryParse(query.Type, out var parsed))
            {
                type = parsed;
            }
            else
            {
                errors["type"] = ["Tipo inválido. Use Income ou Expense."];
            }
        }

        if (errors.Count > 0)
        {
            return new FinancialCalendarResult(null, errors, false);
        }

        var filter = new FinancialCalendarFilter(
            query.From,
            query.Through,
            query.AccountId,
            type,
            BusinessCalendar.Today(timeProvider.GetUtcNow()));
        var snapshot = await calendar.ListAsync(workspaceId, filter, cancellationToken);
        var items = snapshot.Items;
        var summary = snapshot.Summary;
        // Transferências realizadas (leitor central dos registros canônicos): somas à parte e, com includeTransfers, as pontas como itens da Agenda.
        var transfers = new List<Saldo360.Application.Transfers.FinancialTransferDto>();
        for (var page = 1; page <= 20; page++)
        {
            var result = await transferRepository.ListAsync(workspaceId, new(query.From, query.Through, query.AccountId, false, page, 100), cancellationToken);
            transfers.AddRange(result.Items);
            if (transfers.Count >= result.TotalCount || result.Items.Count == 0) break;
        }
        decimal received = 0m, sent = 0m, internalMoved = 0m;
        var extra = new List<FinancialCalendarItemDto>();
        foreach (var t in transfers)
        {
            if (query.AccountId is { } account)
            {
                if (t.DestinationAccount.Id == account) { received += t.Amount; extra.Add(Item(t, true)); }
                if (t.SourceAccount.Id == account) { sent += t.Amount; extra.Add(Item(t, false)); }
            }
            else
            {
                internalMoved += t.Amount;
                extra.Add(Item(t, true));
                extra.Add(Item(t, false));
            }
        }
        summary = summary with { TransfersReceived = received, TransfersSent = sent, TransfersInternal = internalMoved };
        if (query.IncludeTransfers && type is null)
        {
            items = items.Concat(extra).OrderBy(i => i.Date).ThenBy(i => i.Source == FinancialCalendarSource.Transfer ? 1 : 0).ThenBy(i => i.Id).ToList();
        }

        return new FinancialCalendarResult(
            new FinancialCalendar(query.From, query.Through, items, summary),
            null,
            false);

        static FinancialCalendarItemDto Item(Saldo360.Application.Transfers.FinancialTransferDto t, bool incoming) => new(
            t.Id, FinancialCalendarSource.Transfer, FinancialCalendarStatus.Realized, incoming ? CategoryType.Income : CategoryType.Expense, t.Amount, t.TransferDate,
            incoming ? $"Transferência recebida de {t.SourceAccount.Name}" : $"Transferência enviada para {t.DestinationAccount.Name}",
            incoming ? new Saldo360.Application.Transactions.TransactionReferenceDto(t.DestinationAccount.Id, t.DestinationAccount.Name, t.DestinationAccount.IsActive)
                     : new Saldo360.Application.Transactions.TransactionReferenceDto(t.SourceAccount.Id, t.SourceAccount.Name, t.SourceAccount.IsActive),
            null, "BRL", null);
    }
}

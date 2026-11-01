using Saldo360.Application.Workspaces;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Calendar;

/// <summary>Consulta unificada da agenda para qualquer integrante da família.</summary>
public sealed class FinancialCalendarService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialCalendarRepository calendar,
    Saldo360.Application.Transfers.IFinancialTransferRepository transferRepository,
    Saldo360.Application.Transfers.IFinancialPlannedTransferRepository plannedTransferRepository,
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
        // Conjunto da perspectiva: a conta filtrada ou, sem filtro, todas as contas do orçamento (inclusive inativas), que são as pontas de qualquer transferência.
        IReadOnlySet<Guid> perspective = query.AccountId is { } selectedAccount
            ? new HashSet<Guid> { selectedAccount }
            : transfers.SelectMany(t => new[] { t.SourceAccount.Id, t.DestinationAccount.Id }).ToHashSet();
        foreach (var t in transfers)
        {
            switch (Saldo360.Application.Transfers.TransferPerspective.Classify(t.SourceAccount.Id, t.DestinationAccount.Id, perspective))
            {
                case Saldo360.Application.Transfers.TransferRelation.Inbound: received += t.Amount; extra.Add(Item(t, true)); break;
                case Saldo360.Application.Transfers.TransferRelation.Outbound: sent += t.Amount; extra.Add(Item(t, false)); break;
                case Saldo360.Application.Transfers.TransferRelation.Internal:
                    internalMoved += t.Amount; // neutralizada nos totais; continua visível nas duas pontas
                    extra.Add(Item(t, true)); extra.Add(Item(t, false));
                    break;
            }
        }
        // Transferências previstas pendentes (ainda não realizadas): as duas pontas na Agenda, separadas da transferência realizada.
        var plannedItems = new List<FinancialCalendarItemDto>();
        decimal plannedReceived = 0m, plannedSent = 0m;
        foreach (var p in (await plannedTransferRepository.ListAsync(workspaceId, query.From, query.Through, false, filter.Today, cancellationToken))
                     .Where(p => p.Status is Saldo360.Application.Transfers.PlannedTransferStatus.Planned or Saldo360.Application.Transfers.PlannedTransferStatus.Overdue))
        {
            var status = p.Status == Saldo360.Application.Transfers.PlannedTransferStatus.Overdue ? FinancialCalendarStatus.Overdue : FinancialCalendarStatus.Pending;
            if (query.AccountId is null || query.AccountId == p.DestinationAccount.Id) { plannedItems.Add(PlannedItem(p, status, true)); if (query.AccountId is not null) plannedReceived += p.Amount; }
            if (query.AccountId is null || query.AccountId == p.SourceAccount.Id) { plannedItems.Add(PlannedItem(p, status, false)); if (query.AccountId is not null) plannedSent += p.Amount; }
        }
        summary = summary with { TransfersReceived = received, TransfersSent = sent, TransfersInternal = internalMoved, PlannedTransfersReceived = plannedReceived, PlannedTransfersSent = plannedSent };
        // Totais de CAIXA realizados (mesma definição de Contas e Visão Geral): todos os créditos e débitos efetivos (devolução entra como crédito de caixa e volta ao débito bruto,
        // pois RealizedExpense já a abate) mais as transferências que cruzam o conjunto. Previstos e transferências previstas ficam de fora. Com filtro de tipo, transferências não entram.
        var crossing = type is null;
        summary = summary with
        {
            TotalInflows = summary.RealizedIncome + summary.RealizedRefunds + (crossing ? received : 0m),
            TotalOutflows = summary.RealizedExpense + summary.RealizedRefunds + (crossing ? sent : 0m),
        };
        if (query.IncludeTransfers && type is null)
        {
            items = items.Concat(extra).Concat(plannedItems).OrderBy(i => i.Date).ThenBy(i => i.Source == FinancialCalendarSource.Transfer ? 1 : 0).ThenBy(i => i.Id).ToList();
        }

        return new FinancialCalendarResult(
            new FinancialCalendar(query.From, query.Through, items, summary),
            null,
            false);

        static FinancialCalendarItemDto PlannedItem(Saldo360.Application.Transfers.PlannedTransferDto p, FinancialCalendarStatus status, bool incoming) => new(
            p.Id, FinancialCalendarSource.Transfer, status, incoming ? CategoryType.Income : CategoryType.Expense, p.Amount, p.DueDate,
            incoming ? $"Transferência prevista recebida de {p.SourceAccount.Name}" : $"Transferência prevista enviada para {p.DestinationAccount.Name}",
            incoming ? new Saldo360.Application.Transactions.TransactionReferenceDto(p.DestinationAccount.Id, p.DestinationAccount.Name, p.DestinationAccount.IsActive)
                     : new Saldo360.Application.Transactions.TransactionReferenceDto(p.SourceAccount.Id, p.SourceAccount.Name, p.SourceAccount.IsActive),
            null, "BRL", null, PlannedTransferId: p.Id);

        static FinancialCalendarItemDto Item(Saldo360.Application.Transfers.FinancialTransferDto t, bool incoming) => new(
            t.Id, FinancialCalendarSource.Transfer, FinancialCalendarStatus.Realized, incoming ? CategoryType.Income : CategoryType.Expense, t.Amount, t.TransferDate,
            incoming ? $"Transferência recebida de {t.SourceAccount.Name}" : $"Transferência enviada para {t.DestinationAccount.Name}",
            incoming ? new Saldo360.Application.Transactions.TransactionReferenceDto(t.DestinationAccount.Id, t.DestinationAccount.Name, t.DestinationAccount.IsActive)
                     : new Saldo360.Application.Transactions.TransactionReferenceDto(t.SourceAccount.Id, t.SourceAccount.Name, t.SourceAccount.IsActive),
            null, "BRL", null);
    }
}

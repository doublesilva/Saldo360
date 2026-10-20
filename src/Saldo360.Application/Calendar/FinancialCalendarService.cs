using Saldo360.Application.Workspaces;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Calendar;

/// <summary>Consulta unificada da agenda para qualquer integrante da família.</summary>
public sealed class FinancialCalendarService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialCalendarRepository calendar,
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
        return new FinancialCalendarResult(
            new FinancialCalendar(query.From, query.Through, snapshot.Items, snapshot.Summary),
            null,
            false);
    }
}

using Saldo360.Application.Families;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.Planning;

/// <summary>
/// Receitas e despesas previstas únicas. Owner e Member têm o mesmo acesso. Mesmas regras de conta e categoria dos
/// lançamentos (<see cref="ReferenceRules"/>), mas a data prevista pode ser futura. Nada aqui cria lançamento nem
/// mexe no saldo realizado; previsão vencida continua pendente.
/// </summary>
public sealed class FinancialPlannedTransactionService(
    IFamilyRepository families,
    IFinancialPlannedTransactionRepository planned,
    TimeProvider timeProvider)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public async Task<PlannedTransactionResult> CreateAsync(
        Guid familyId, Guid userId, PlannedTransactionInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
        {
            return PlannedTransactionResult.NotFound;
        }

        var (details, errors) = await ValidateAsync(familyId, input, current: null, cancellationToken);
        if (details is null)
        {
            return PlannedTransactionResult.Invalid(errors);
        }

        var entity = FinancialPlannedTransaction.Create(familyId, details, userId, timeProvider.GetUtcNow());
        await planned.AddAsync(entity, cancellationToken);
        return PlannedTransactionResult.Ok((await planned.GetAsync(familyId, entity.Id, cancellationToken))!);
    }

    public async Task<FinancialPlannedTransactionDto?> GetAsync(
        Guid familyId, Guid plannedId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(familyId, userId, cancellationToken)
            ? await planned.GetAsync(familyId, plannedId, cancellationToken)
            : null;

    public async Task<PlannedTransactionListResult> ListAsync(
        Guid familyId, Guid userId, PlannedTransactionQuery query, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
        {
            return PlannedTransactionListResult.NotFoundResult;
        }

        var errors = new Dictionary<string, string[]>();
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;
        if (page < 1)
        {
            errors["page"] = ["A página deve ser 1 ou maior."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"O tamanho da página deve estar entre 1 e {MaxPageSize}."];
        }

        if (query.From > query.To)
        {
            errors["from"] = ["A data inicial deve ser anterior ou igual à final."];
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
            return new PlannedTransactionListResult(null, errors, false);
        }

        var filter = new PlannedTransactionFilter(
            query.From, query.To, query.AccountId, query.CategoryId, type, query.IncludeCancelled, page, pageSize);
        return new PlannedTransactionListResult(await planned.ListAsync(familyId, filter, cancellationToken), null, false);
    }

    /// <summary>Substitui os dados. Família, criador e cancelamento não mudam; cancelada dá 409.</summary>
    public async Task<PlannedTransactionResult> UpdateAsync(
        Guid familyId, Guid plannedId, Guid userId, PlannedTransactionInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken)
            || await planned.FindAsync(familyId, plannedId, cancellationToken) is not { } entity)
        {
            return PlannedTransactionResult.NotFound;
        }

        if (entity.IsCancelled)
        {
            return PlannedTransactionResult.Cancelled;
        }

        var (details, errors) = await ValidateAsync(familyId, input, entity, cancellationToken);
        if (details is null)
        {
            return PlannedTransactionResult.Invalid(errors);
        }

        entity.Update(details, userId, timeProvider.GetUtcNow());
        await planned.SaveChangesAsync(cancellationToken);
        return PlannedTransactionResult.Ok((await planned.GetAsync(familyId, entity.Id, cancellationToken))!);
    }

    /// <summary>Cancela preservando o registro. Idempotente: cancelar de novo devolve o mesmo estado.</summary>
    public async Task<PlannedTransactionResult> CancelAsync(
        Guid familyId, Guid plannedId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken)
            || await planned.FindAsync(familyId, plannedId, cancellationToken) is not { } entity)
        {
            return PlannedTransactionResult.NotFound;
        }

        if (entity.Cancel(userId, timeProvider.GetUtcNow()))
        {
            await planned.SaveChangesAsync(cancellationToken);
        }

        return PlannedTransactionResult.Ok((await planned.GetAsync(familyId, entity.Id, cancellationToken))!);
    }

    private async Task<bool> IsParticipantAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is not null;

    private async Task<(PlannedTransactionDetails? Details, Dictionary<string, string[]> Errors)> ValidateAsync(
        Guid familyId, PlannedTransactionInput input, FinancialPlannedTransaction? current, CancellationToken cancellationToken)
    {
        var details = PlannedTransactionDetails.TryCreate(
            input.AccountId, input.CategoryId, input.Type, input.Description, input.Amount, input.DueDate, out var errors);

        await ReferenceRules.ValidateAsync(
            planned, familyId, input.AccountId, input.CategoryId, input.Type, current?.AccountId, current?.CategoryId,
            errors, cancellationToken);

        return (errors.Count == 0 ? details : null, errors);
    }
}

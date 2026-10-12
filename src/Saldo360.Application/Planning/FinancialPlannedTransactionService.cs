using Saldo360.Application.Families;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Transactions;

namespace Saldo360.Application.Planning;

/// <summary>
/// Receitas e despesas previstas únicas. Owner e Member têm o mesmo acesso. Mesmas regras de conta e categoria dos
/// lançamentos (<see cref="ReferenceRules"/>), mas a data prevista pode ser futura. Previsão não mexe no saldo
/// realizado; vencida continua pendente. Só <see cref="RealizeAsync"/> cria lançamento efetivo, vinculado.
/// Editar, cancelar e realizar rodam com a previsão bloqueada (ver
/// <see cref="IFinancialPlannedTransactionRepository.RunLockedAsync{T}"/>).
/// </summary>
public sealed class FinancialPlannedTransactionService(
    IFamilyRepository families,
    IFinancialPlannedTransactionRepository planned,
    IFinancialTransactionRepository transactions,
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
            query.From, query.To, query.AccountId, query.CategoryId, type, query.IncludeRealized, query.IncludeCancelled, page, pageSize);
        return new PlannedTransactionListResult(await planned.ListAsync(familyId, filter, cancellationToken), null, false);
    }

    /// <summary>
    /// Substitui os dados de uma previsão pendente. Família, criador e cancelamento não mudam. Cancelada ou realizada
    /// dá 409 (verificado com a previsão bloqueada, então não corre com uma realização simultânea).
    /// </summary>
    public async Task<PlannedTransactionResult> UpdateAsync(
        Guid familyId, Guid plannedId, Guid userId, PlannedTransactionInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
        {
            return PlannedTransactionResult.NotFound;
        }

        var failure = await planned.RunLockedAsync<PlannedTransactionResult?>(familyId, plannedId, async locked =>
        {
            if (locked is null)
            {
                return PlannedTransactionResult.NotFound;
            }

            if (locked.Planned.IsCancelled)
            {
                return PlannedTransactionResult.Conflict(PlannedTransactionResult.CancelledMessage);
            }

            if (locked.ActiveRealization is not null)
            {
                return PlannedTransactionResult.Conflict(PlannedTransactionResult.RealizedMessage);
            }

            var (details, errors) = await ValidateAsync(familyId, input, locked.Planned, cancellationToken);
            if (details is null)
            {
                return PlannedTransactionResult.Invalid(errors);
            }

            locked.Planned.Update(details, userId, timeProvider.GetUtcNow());
            return null;
        }, cancellationToken);

        return failure ?? PlannedTransactionResult.Ok((await planned.GetAsync(familyId, plannedId, cancellationToken))!);
    }

    /// <summary>
    /// Cancela preservando o registro. Idempotente: cancelar de novo devolve o mesmo estado. Realizada dá 409: antes
    /// cancela-se o lançamento efetivo.
    /// </summary>
    public async Task<PlannedTransactionResult> CancelAsync(
        Guid familyId, Guid plannedId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
        {
            return PlannedTransactionResult.NotFound;
        }

        var failure = await planned.RunLockedAsync<PlannedTransactionResult?>(familyId, plannedId, locked =>
        {
            if (locked is null)
            {
                return Task.FromResult<PlannedTransactionResult?>(PlannedTransactionResult.NotFound);
            }

            if (locked.ActiveRealization is not null)
            {
                return Task.FromResult<PlannedTransactionResult?>(PlannedTransactionResult.Conflict(PlannedTransactionResult.RealizedMessage));
            }

            locked.Planned.Cancel(userId, timeProvider.GetUtcNow());
            return Task.FromResult<PlannedTransactionResult?>(null);
        }, cancellationToken);

        return failure ?? PlannedTransactionResult.Ok((await planned.GetAsync(familyId, plannedId, cancellationToken))!);
    }

    /// <summary>
    /// Realiza a previsão: cria UM lançamento efetivo com conta, categoria, tipo e descrição da previsão e o valor e a
    /// data informados (valor pode diferir; data não futura em America/Sao_Paulo), vinculado à previsão, com o
    /// usuário atual como autor. Os valores previstos não mudam. Tudo com a previsão bloqueada, numa transação.
    /// Repetição com o mesmo valor e data devolve o lançamento existente (nada muda); com outro valor ou data, 409.
    /// Cancelada, 409. Conta ou categoria da previsão inativas: 400 orientando a ajustar a previsão.
    /// </summary>
    public async Task<RealizeResult> RealizeAsync(
        Guid familyId, Guid plannedId, Guid userId, decimal? amount, DateOnly? transactionDate, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
        {
            return new RealizeResult(RealizeAccess.NotFound, null, null, null);
        }

        (RealizeAccess Access, Guid? TransactionId, IDictionary<string, string[]>? Errors, string? Message) outcome;
        try
        {
            outcome = await RealizeLockedAsync(familyId, plannedId, userId, amount, transactionDate, cancellationToken);
        }
        catch (RealizationConflictException)
        {
            // Outra realização venceu a corrida no índice único: agora ela é a existente (mesmos dados) ou conflito.
            outcome = await RealizeLockedAsync(familyId, plannedId, userId, amount, transactionDate, cancellationToken);
        }

        var transaction = outcome.TransactionId is { } id ? await transactions.GetAsync(familyId, id, cancellationToken) : null;
        return new RealizeResult(outcome.Access, transaction, outcome.Errors, outcome.Message);
    }

    private Task<(RealizeAccess, Guid?, IDictionary<string, string[]>?, string?)> RealizeLockedAsync(
        Guid familyId, Guid plannedId, Guid userId, decimal? amount, DateOnly? transactionDate, CancellationToken cancellationToken) =>
        planned.RunLockedAsync<(RealizeAccess, Guid?, IDictionary<string, string[]>?, string?)>(familyId, plannedId, async locked =>
        {
            if (locked is null)
            {
                return (RealizeAccess.NotFound, null, null, null);
            }

            if (locked.Planned.IsCancelled)
            {
                return (RealizeAccess.Conflict, null, null, RealizeResult.CancelledMessage);
            }

            if (locked.ActiveRealization is { } existing)
            {
                return existing.Amount == amount && existing.TransactionDate == transactionDate
                    ? (RealizeAccess.Existing, existing.Id, null, null)
                    : (RealizeAccess.Conflict, null, null, RealizeResult.AlreadyRealizedMessage);
            }

            var now = timeProvider.GetUtcNow();
            var details = locked.Planned.ToRealizationDetails(amount, transactionDate, BusinessCalendar.Today(now), out var errors);
            await ValidateReferencesForRealizationAsync(familyId, locked.Planned, errors, cancellationToken);
            if (details is null || errors.Count > 0)
            {
                return (RealizeAccess.Invalid, null, errors, null);
            }

            var transaction = FinancialTransaction.CreateRealization(familyId, details, locked.Planned.Id, userId, now);
            locked.AddRealization(transaction);
            return (RealizeAccess.Created, transaction.Id, null, null);
        }, cancellationToken);

    // Realizar exige conta e categoria ATIVAS (não vale a regra de "manter inativa" da edição): a orientação é
    // ajustar a previsão antes.
    private async Task ValidateReferencesForRealizationAsync(
        Guid familyId, FinancialPlannedTransaction entity, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        if (await planned.GetAccountStateAsync(familyId, entity.AccountId, cancellationToken) is { IsActive: false })
        {
            errors[ReferenceRules.AccountField] = ["A conta da previsão está inativa. Edite a previsão e escolha uma conta ativa antes de realizar."];
        }

        if (await planned.GetCategoryStateAsync(familyId, entity.CategoryId, cancellationToken) is { IsActive: false })
        {
            errors[ReferenceRules.CategoryField] = ["A categoria da previsão está inativa. Edite a previsão e escolha uma categoria ativa antes de realizar."];
        }
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

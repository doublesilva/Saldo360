using Saldo360.Application.Families;
using Saldo360.Application.Planning;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transactions;

namespace Saldo360.Application.Transactions;

/// <summary>
/// Lançamentos únicos realizados. Owner e Member têm o mesmo acesso. Família vem da rota, autor da sessão.
/// Conta e categoria precisam ser da família; novas escolhas precisam estar ativas, mas uma edição pode manter
/// a conta ou categoria inativa que o lançamento já usava. Referência de outra família recebe a mesma mensagem
/// de uma inexistente. Lançamento que realiza uma previsão não é editável, e cancelá-lo reabre a previsão.
/// </summary>
public sealed class FinancialTransactionService(
    IFamilyRepository families,
    IFinancialTransactionRepository transactions,
    IFinancialPlannedTransactionRepository planned,
    TimeProvider timeProvider)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public async Task<TransactionResult> CreateAsync(
        Guid familyId, Guid userId, TransactionInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
        {
            return TransactionResult.NotFound;
        }

        var (details, errors) = await ValidateAsync(familyId, input, current: null, cancellationToken);
        if (details is null)
        {
            return TransactionResult.Invalid(errors);
        }

        var transaction = FinancialTransaction.Create(familyId, details, userId, timeProvider.GetUtcNow());
        await transactions.AddAsync(transaction, cancellationToken);
        return TransactionResult.Ok((await transactions.GetAsync(familyId, transaction.Id, cancellationToken))!);
    }

    public async Task<FinancialTransactionDto?> GetAsync(
        Guid familyId, Guid transactionId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(familyId, userId, cancellationToken)
            ? await transactions.GetAsync(familyId, transactionId, cancellationToken)
            : null;

    public async Task<TransactionListResult> ListAsync(
        Guid familyId, Guid userId, TransactionQuery query, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
        {
            return TransactionListResult.NotFoundResult;
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
            return new TransactionListResult(null, errors, false);
        }

        var filter = new TransactionFilter(
            query.From, query.To, query.AccountId, query.CategoryId, type, query.IncludeCancelled, page, pageSize);
        return new TransactionListResult(await transactions.ListAsync(familyId, filter, cancellationToken), null, false);
    }

    /// <summary>Substitui os dados. Família, criador e cancelamento não mudam; cancelado dá 409.</summary>
    public async Task<TransactionResult> UpdateAsync(
        Guid familyId, Guid transactionId, Guid userId, TransactionInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken)
            || await transactions.FindAsync(familyId, transactionId, cancellationToken) is not { } transaction)
        {
            return TransactionResult.NotFound;
        }

        if (transaction.IsCancelled)
        {
            return TransactionResult.Cancelled;
        }

        if (transaction.PlannedTransactionId is not null)
        {
            return TransactionResult.Linked;
        }

        var (details, errors) = await ValidateAsync(familyId, input, transaction, cancellationToken);
        if (details is null)
        {
            return TransactionResult.Invalid(errors);
        }

        transaction.Update(details, userId, timeProvider.GetUtcNow());
        await transactions.SaveChangesAsync(cancellationToken);
        return TransactionResult.Ok((await transactions.GetAsync(familyId, transaction.Id, cancellationToken))!);
    }

    /// <summary>Cancela preservando o registro. Idempotente: cancelar de novo devolve o mesmo estado.</summary>
    public async Task<TransactionResult> CancelAsync(
        Guid familyId, Guid transactionId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken)
            || await transactions.GetLinkAsync(familyId, transactionId, cancellationToken) is not { } link)
        {
            return TransactionResult.NotFound;
        }

        if (link.PlannedTransactionId is { } plannedId)
        {
            // Lançamento que realiza uma previsão: cancela com a previsão bloqueada, serializado com realizar,
            // editar e cancelar a previsão. Como "realizada" é derivado do lançamento ativo, a previsão volta a
            // pendente nesta mesma transação. O lançamento é carregado só depois do bloqueio (dados atuais), então
            // repetir o cancelamento de um lançamento antigo não toca uma realização nova.
            await planned.RunLockedAsync(familyId, plannedId, async _ =>
            {
                var linked = await transactions.FindAsync(familyId, transactionId, cancellationToken);
                return linked!.Cancel(userId, timeProvider.GetUtcNow());
            }, cancellationToken);
        }
        else if (await transactions.FindAsync(familyId, transactionId, cancellationToken) is { } transaction
            && transaction.Cancel(userId, timeProvider.GetUtcNow()))
        {
            await transactions.SaveChangesAsync(cancellationToken);
        }

        return TransactionResult.Ok((await transactions.GetAsync(familyId, transactionId, cancellationToken))!);
    }

    private async Task<bool> IsParticipantAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is not null;

    private async Task<(TransactionDetails? Details, Dictionary<string, string[]> Errors)> ValidateAsync(
        Guid familyId, TransactionInput input, FinancialTransaction? current, CancellationToken cancellationToken)
    {
        var today = BusinessCalendar.Today(timeProvider.GetUtcNow());
        var details = TransactionDetails.TryCreate(
            input.AccountId, input.CategoryId, input.Type, input.Description, input.Amount, input.TransactionDate,
            today, out var errors);

        await ReferenceRules.ValidateAsync(
            transactions, familyId, input.AccountId, input.CategoryId, input.Type, current?.AccountId, current?.CategoryId,
            errors, cancellationToken);

        return (errors.Count == 0 ? details : null, errors);
    }
}

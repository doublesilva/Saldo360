using Saldo360.Application.Families;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transfers;

namespace Saldo360.Application.Transfers;

/// <summary>
/// Transferências realizadas entre contas da família. Owner e Member têm o mesmo acesso. Origem e destino
/// precisam ser contas distintas, da família e ativas na criação; conta de outra família recebe a mesma
/// mensagem de uma inexistente. Não há edição: corrige-se cancelando e cadastrando de novo.
/// </summary>
public sealed class FinancialTransferService(
    IFamilyRepository families,
    IFinancialTransferRepository transfers,
    TimeProvider timeProvider)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public async Task<TransferResult> CreateAsync(Guid familyId, Guid userId, TransferInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
        {
            return TransferResult.NotFound;
        }

        var today = BusinessCalendar.Today(timeProvider.GetUtcNow());
        var details = TransferDetails.TryCreate(
            input.SourceAccountId, input.DestinationAccountId, input.Amount, input.TransferDate, input.Description,
            today, out var errors);

        await ValidateAccountAsync(familyId, input.SourceAccountId, "sourceAccountId", "origem", errors, cancellationToken);
        await ValidateAccountAsync(familyId, input.DestinationAccountId, "destinationAccountId", "destino", errors, cancellationToken);

        if (details is null || errors.Count > 0)
        {
            return TransferResult.Invalid(errors);
        }

        var transfer = FinancialTransfer.Create(familyId, details, userId, timeProvider.GetUtcNow());
        await transfers.AddAsync(transfer, cancellationToken);
        return TransferResult.Ok((await transfers.GetAsync(familyId, transfer.Id, cancellationToken))!);
    }

    public async Task<FinancialTransferDto?> GetAsync(Guid familyId, Guid transferId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(familyId, userId, cancellationToken)
            ? await transfers.GetAsync(familyId, transferId, cancellationToken)
            : null;

    public async Task<TransferListResult> ListAsync(Guid familyId, Guid userId, TransferQuery query, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
        {
            return TransferListResult.NotFoundResult;
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

        if (errors.Count > 0)
        {
            return new TransferListResult(null, errors, false);
        }

        var filter = new TransferFilter(query.From, query.To, query.AccountId, query.IncludeCancelled, page, pageSize);
        return new TransferListResult(await transfers.ListAsync(familyId, filter, cancellationToken), null, false);
    }

    /// <summary>Cancela preservando o registro. Idempotente: cancelar de novo devolve o mesmo estado.</summary>
    public async Task<TransferResult> CancelAsync(Guid familyId, Guid transferId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken)
            || await transfers.FindAsync(familyId, transferId, cancellationToken) is not { } transfer)
        {
            return TransferResult.NotFound;
        }

        if (transfer.Cancel(userId, timeProvider.GetUtcNow()))
        {
            await transfers.SaveChangesAsync(cancellationToken);
        }

        return TransferResult.Ok((await transfers.GetAsync(familyId, transfer.Id, cancellationToken))!);
    }

    private async Task<bool> IsParticipantAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is not null;

    private async Task ValidateAccountAsync(
        Guid familyId, Guid? accountId, string field, string role, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        if (accountId is not { } id || id == Guid.Empty || errors.ContainsKey(field))
        {
            return;
        }

        switch (await transfers.IsAccountActiveAsync(familyId, id, cancellationToken))
        {
            case null:
                errors[field] = [$"Conta de {role} inválida."];
                break;
            case false:
                errors[field] = [$"A conta de {role} está inativa. Escolha uma conta ativa."];
                break;
        }
    }
}

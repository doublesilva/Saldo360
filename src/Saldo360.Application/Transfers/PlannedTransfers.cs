using Saldo360.Application.Workspaces;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transfers;

namespace Saldo360.Application.Transfers;

/// <summary>Situação de uma previsão: Planned (prevista), Overdue (prevista com data anterior a hoje), Realized (associada a uma transferência) ou Cancelled.</summary>
public static class PlannedTransferStatus
{
    public const string Planned = "Planned", Overdue = "Overdue", Realized = "Realized", Cancelled = "Cancelled";

    public static string Of(bool cancelled, Guid? realizedTransferId, DateOnly dueDate, DateOnly today) =>
        cancelled ? Cancelled : realizedTransferId is not null ? Realized : dueDate < today ? Overdue : Planned;
}

/// <summary>Transferência realizada que poderia cumprir a previsão (mesmas contas, não cancelada, ainda sem previsão associada, até 7 dias de distância): a associação é decisão do usuário.</summary>
public sealed record PlannedTransferCandidateDto(Guid TransferId, DateOnly TransferDate, decimal Amount, string Description);

public sealed record PlannedTransferDto(
    Guid Id, TransferAccountDto SourceAccount, TransferAccountDto DestinationAccount, decimal Amount, DateOnly DueDate, string Description, string Status,
    Guid? RealizedTransferId, string? LinkOrigin, DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt, IReadOnlyList<PlannedTransferCandidateDto> Candidates);

public sealed record PlannedTransferInput(Guid? SourceAccountId, Guid? DestinationAccountId, decimal? Amount, DateOnly? DueDate, string? Description);

public interface IFinancialPlannedTransferRepository
{
    Task AddAsync(FinancialPlannedTransfer planned, CancellationToken ct);
    Task<FinancialPlannedTransfer?> FindAsync(Guid workspaceId, Guid id, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    Task<IReadOnlyList<PlannedTransferDto>> ListAsync(Guid workspaceId, DateOnly from, DateOnly to, bool includeCancelled, DateOnly today, CancellationToken ct);
    Task<PlannedTransferDto?> GetAsync(Guid workspaceId, Guid id, DateOnly today, CancellationToken ct);
    /// <summary>A transferência realizada (não cancelada) da família, com origem e destino, para validar uma associação manual.</summary>
    Task<(Guid Source, Guid Destination, bool IsCancelled)?> FindTransferAsync(Guid workspaceId, Guid transferId, CancellationToken ct);
    /// <summary>Já existe previsão associada a esta transferência?</summary>
    Task<bool> IsTransferLinkedAsync(Guid workspaceId, Guid transferId, CancellationToken ct);
}

public enum PlannedTransferOutcome { Ok, NotFound, Invalid, Conflict }

public sealed record PlannedTransferResult(PlannedTransferOutcome Outcome, PlannedTransferDto? Value = null, IDictionary<string, string[]>? Errors = null, string? Message = null);

/// <summary>
/// Transferências previstas (compromissos futuros entre contas próprias). Não geram lançamento nem transferência e nunca alteram o saldo realizado: só a projeção. A associação com a
/// transferência realizada é automática apenas quando é inequívoca (ver o reconciliador da infraestrutura) e, na dúvida, fica para o usuário confirmar.
/// </summary>
public sealed class FinancialPlannedTransferService(
    IFinancialWorkspaceRepository workspaces, IFinancialPlannedTransferRepository planned, IFinancialTransferRepository transfers, TimeProvider time)
{
    public async Task<PlannedTransferResult> CreateAsync(Guid workspaceId, Guid userId, PlannedTransferInput input, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return new(PlannedTransferOutcome.NotFound);
        var today = BusinessCalendar.Today(time.GetUtcNow());
        // A data prevista pode ser hoje, futura ou já passada (atrasada): a regra de "não futura" é só da transferência realizada.
        var details = TransferDetails.TryCreate(input.SourceAccountId, input.DestinationAccountId, input.Amount, input.DueDate, input.Description, today, out var errors, allowFutureDate: true);
        if (errors.Remove("transferDate")) errors["dueDate"] = ["Informe a data prevista."];
        foreach (var (field, id, label) in new[] { ("sourceAccountId", input.SourceAccountId, "origem"), ("destinationAccountId", input.DestinationAccountId, "destino") })
        {
            if (id is not { } accountId || accountId == Guid.Empty) continue;
            var active = await transfers.IsAccountActiveAsync(workspaceId, accountId, ct);
            if (active is null) errors[field] = [$"Conta de {label} não encontrada."];
            else if (active == false) errors[field] = [$"A conta de {label} está inativa."];
        }
        if (details is null || errors.Count > 0) return new(PlannedTransferOutcome.Invalid, Errors: errors);
        var entity = FinancialPlannedTransfer.Create(workspaceId, details, userId, time.GetUtcNow());
        await planned.AddAsync(entity, ct);
        return new(PlannedTransferOutcome.Ok, await planned.GetAsync(workspaceId, entity.Id, today, ct));
    }

    public async Task<IReadOnlyList<PlannedTransferDto>?> ListAsync(Guid workspaceId, Guid userId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct) =>
        await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null ? null : await planned.ListAsync(workspaceId, from, to, includeCancelled, BusinessCalendar.Today(time.GetUtcNow()), ct);

    public async Task<PlannedTransferResult> CancelAsync(Guid workspaceId, Guid userId, Guid id, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null || await planned.FindAsync(workspaceId, id, ct) is not { } entity) return new(PlannedTransferOutcome.NotFound);
        if (entity.RealizedTransferId is not null)
            return new(PlannedTransferOutcome.Conflict, Message: "Esta previsão já foi realizada. Para desfazer, cancele a transferência realizada ou desfaça a associação.");
        if (entity.Cancel(userId, time.GetUtcNow())) await planned.SaveChangesAsync(ct);
        return new(PlannedTransferOutcome.Ok, await planned.GetAsync(workspaceId, id, BusinessCalendar.Today(time.GetUtcNow()), ct));
    }

    /// <summary>Associação manual escolhida pelo usuário: mesmas contas, transferência não cancelada e ainda sem previsão. O valor pode diferir (decisão do usuário).</summary>
    public async Task<PlannedTransferResult> LinkAsync(Guid workspaceId, Guid userId, Guid id, Guid? transferId, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null || await planned.FindAsync(workspaceId, id, ct) is not { } entity) return new(PlannedTransferOutcome.NotFound);
        if (transferId is null || transferId == Guid.Empty) return new(PlannedTransferOutcome.Invalid, Errors: new Dictionary<string, string[]> { ["transferId"] = ["Informe a transferência realizada."] });
        if (await planned.FindTransferAsync(workspaceId, transferId.Value, ct) is not { } transfer) return new(PlannedTransferOutcome.NotFound);
        if (entity.IsCancelled) return new(PlannedTransferOutcome.Conflict, Message: "Previsão cancelada não pode ser associada.");
        if (transfer.IsCancelled || transfer.Source != entity.SourceAccountId || transfer.Destination != entity.DestinationAccountId)
            return new(PlannedTransferOutcome.Invalid, Errors: new Dictionary<string, string[]> { ["transferId"] = ["A transferência precisa estar ativa e ter a mesma origem e o mesmo destino da previsão."] });
        if (entity.RealizedTransferId == transferId) return new(PlannedTransferOutcome.Ok, await planned.GetAsync(workspaceId, id, BusinessCalendar.Today(time.GetUtcNow()), ct));
        if (entity.RealizedTransferId is not null || await planned.IsTransferLinkedAsync(workspaceId, transferId.Value, ct))
            return new(PlannedTransferOutcome.Conflict, Message: "A previsão ou a transferência já está associada.");
        entity.Link(transferId.Value, PlannedTransferLinkOrigin.UserConfirmed, time.GetUtcNow());
        await planned.SaveChangesAsync(ct);
        return new(PlannedTransferOutcome.Ok, await planned.GetAsync(workspaceId, id, BusinessCalendar.Today(time.GetUtcNow()), ct));
    }

    /// <summary>Desfaz a associação: a previsão volta a ser prevista e a projetar; a transferência realizada não muda.</summary>
    public async Task<PlannedTransferResult> UnlinkAsync(Guid workspaceId, Guid userId, Guid id, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null || await planned.FindAsync(workspaceId, id, ct) is not { } entity) return new(PlannedTransferOutcome.NotFound);
        if (entity.Release()) await planned.SaveChangesAsync(ct);
        return new(PlannedTransferOutcome.Ok, await planned.GetAsync(workspaceId, id, BusinessCalendar.Today(time.GetUtcNow()), ct));
    }
}

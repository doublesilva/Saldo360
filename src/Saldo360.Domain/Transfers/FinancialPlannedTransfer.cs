using Saldo360.Domain.Common;

namespace Saldo360.Domain.Transfers;

public enum PlannedTransferLinkOrigin { Inferred, UserConfirmed }

/// <summary>
/// Transferência PREVISTA entre duas contas próprias: um compromisso futuro (ou atrasado) de mover dinheiro, com origem, destino, valor e data. Não é lançamento nem altera o saldo
/// realizado: enquanto pendente entra só na projeção (saída na origem e entrada no destino, neutra no consolidado das duas contas). Quando a transferência acontece, a previsão é
/// ASSOCIADA ao registro canônico <see cref="FinancialTransfer"/> (<see cref="RealizedTransferId"/>) e deixa de projetar, evitando duplicidade. Cancelar preserva o registro.
/// Situação: prevista (sem transferência associada, não cancelada), realizada (associada) ou cancelada; "vencida" é prevista com data anterior a hoje.
/// </summary>
public sealed class FinancialPlannedTransfer : Entity
{
    public const int DescriptionMaxLength = 200;
    public const string DefaultCurrency = "BRL";

    private FinancialPlannedTransfer()
    {
        Description = string.Empty;
        Currency = DefaultCurrency;
    }

    public Guid WorkspaceId { get; private init; }
    public Guid SourceAccountId { get; private init; }
    public Guid DestinationAccountId { get; private init; }
    public decimal Amount { get; private init; }
    public DateOnly DueDate { get; private init; }
    public string Description { get; private init; }
    public string Currency { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }
    public bool IsCancelled { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }

    /// <summary>Transferência realizada que cumpre esta previsão (nulo = ainda não realizada).</summary>
    public Guid? RealizedTransferId { get; private set; }
    public PlannedTransferLinkOrigin? LinkOrigin { get; private set; }
    public DateTimeOffset? LinkedAt { get; private set; }

    public bool IsPending => !IsCancelled && RealizedTransferId is null;

    public static FinancialPlannedTransfer Create(Guid workspaceId, TransferDetails details, Guid userId, DateTimeOffset now) =>
        new()
        {
            WorkspaceId = workspaceId, SourceAccountId = details.SourceAccountId, DestinationAccountId = details.DestinationAccountId, Amount = details.Amount,
            DueDate = details.TransferDate, Description = details.Description, CreatedAt = now, CreatedByUserId = userId,
        };

    /// <summary>Cancela a previsão (idempotente). Previsão já realizada não se cancela aqui: cancela-se a transferência realizada.</summary>
    public bool Cancel(Guid userId, DateTimeOffset now)
    {
        if (IsCancelled) return false;
        if (RealizedTransferId is not null) throw new InvalidOperationException("Previsão já realizada não pode ser cancelada.");
        IsCancelled = true;
        CancelledAt = now;
        CancelledByUserId = userId;
        return true;
    }

    /// <summary>Associa a previsão à transferência realizada. Idempotente para a mesma transferência; previsão cancelada não se associa.</summary>
    public bool Link(Guid transferId, PlannedTransferLinkOrigin origin, DateTimeOffset now)
    {
        if (IsCancelled) throw new InvalidOperationException("Previsão cancelada não pode ser associada.");
        if (RealizedTransferId == transferId) return false;
        if (RealizedTransferId is not null) throw new InvalidOperationException("Previsão já associada a outra transferência.");
        RealizedTransferId = transferId;
        LinkOrigin = origin;
        LinkedAt = now;
        return true;
    }

    /// <summary>Libera a associação (a transferência realizada foi cancelada ou o usuário desfez): a previsão volta a projetar.</summary>
    public bool Release()
    {
        if (RealizedTransferId is null) return false;
        RealizedTransferId = null;
        LinkOrigin = null;
        LinkedAt = null;
        return true;
    }
}

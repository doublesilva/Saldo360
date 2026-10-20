using Saldo360.Domain.Common;

namespace Saldo360.Domain.Transfers;

/// <summary>
/// Transferência já realizada entre duas contas da mesma família: um único registro com origem e destino, sem
/// categoria e sem gerar receita ou despesa. No saldo, soma no destino e subtrai na origem. Vale entre PF e PJ
/// em qualquer sentido e não é bloqueada por saldo insuficiente (a origem pode ficar negativa). Não é editada:
/// para corrigir, cancela-se (sem exclusão física) e cadastra-se de novo.
/// </summary>
public sealed class FinancialTransfer : Entity
{
    public const int DescriptionMaxLength = 200;

    /// <summary>Única moeda suportada nesta etapa.</summary>
    public const string DefaultCurrency = "BRL";

    private FinancialTransfer()
    {
        Description = string.Empty;
        Currency = DefaultCurrency;
    }

    public Guid WorkspaceId { get; private init; }

    public Guid SourceAccountId { get; private init; }

    public Guid DestinationAccountId { get; private init; }

    /// <summary>Sempre positivo, até duas casas decimais.</summary>
    public decimal Amount { get; private init; }

    /// <summary>Dia civil da transferência (America/Sao_Paulo), sem horário; não futuro.</summary>
    public DateOnly TransferDate { get; private init; }

    public string Description { get; private init; }

    public string Currency { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public Guid CreatedByUserId { get; private init; }

    public bool IsCancelled { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public static FinancialTransfer Create(Guid workspaceId, TransferDetails details, Guid createdByUserId, DateTimeOffset now) =>
        new()
        {
            WorkspaceId = workspaceId,
            SourceAccountId = details.SourceAccountId,
            DestinationAccountId = details.DestinationAccountId,
            Amount = details.Amount,
            TransferDate = details.TransferDate,
            Description = details.Description,
            CreatedAt = now,
            CreatedByUserId = createdByUserId,
        };

    /// <summary>Cancela preservando o registro. Idempotente: false se já estava cancelada (nada muda).</summary>
    public bool Cancel(Guid userId, DateTimeOffset now)
    {
        if (IsCancelled)
        {
            return false;
        }

        IsCancelled = true;
        CancelledAt = now;
        CancelledByUserId = userId;
        return true;
    }
}

/// <summary>
/// Dados da transferência já validados em formato. Se as contas são da família e estão ativas é verificado pela
/// aplicação (e pelas FKs compostas no banco).
/// </summary>
public sealed record TransferDetails
{
    private TransferDetails(Guid sourceAccountId, Guid destinationAccountId, decimal amount, DateOnly transferDate, string description)
    {
        SourceAccountId = sourceAccountId;
        DestinationAccountId = destinationAccountId;
        Amount = amount;
        TransferDate = transferDate;
        Description = description;
    }

    public Guid SourceAccountId { get; }

    public Guid DestinationAccountId { get; }

    public decimal Amount { get; }

    public DateOnly TransferDate { get; }

    public string Description { get; }

    /// <summary>
    /// Valida origem e destino (informados e distintos), valor (positivo, até duas casas, dentro do limite, sem
    /// arredondar), data (não futura em relação a <paramref name="today"/>) e descrição (obrigatória, até 200).
    /// </summary>
    public static TransferDetails? TryCreate(
        Guid? sourceAccountId, Guid? destinationAccountId, decimal? amount, DateOnly? transferDate, string? description,
        DateOnly today, out Dictionary<string, string[]> errors)
    {
        errors = [];
        var trimmedDescription = description?.Trim() ?? string.Empty;

        if (sourceAccountId is null || sourceAccountId == Guid.Empty)
        {
            errors["sourceAccountId"] = ["Informe a conta de origem."];
        }

        if (destinationAccountId is null || destinationAccountId == Guid.Empty)
        {
            errors["destinationAccountId"] = ["Informe a conta de destino."];
        }
        else if (destinationAccountId == sourceAccountId)
        {
            errors["destinationAccountId"] = ["A conta de destino deve ser diferente da origem."];
        }

        if (amount is null)
        {
            errors["amount"] = ["Informe o valor."];
        }
        else if (amount <= 0)
        {
            errors["amount"] = ["O valor deve ser maior que zero."];
        }
        else if (Money.Validate(amount.Value, "O valor") is { } amountError)
        {
            errors["amount"] = [amountError];
        }

        if (transferDate is null)
        {
            errors["transferDate"] = ["Informe a data da transferência."];
        }
        else if (transferDate > today)
        {
            errors["transferDate"] = ["A data não pode ser futura: registre só transferências já realizadas."];
        }

        if (trimmedDescription.Length == 0)
        {
            errors["description"] = ["Informe a descrição."];
        }
        else if (trimmedDescription.Length > FinancialTransfer.DescriptionMaxLength)
        {
            errors["description"] = [$"A descrição deve ter no máximo {FinancialTransfer.DescriptionMaxLength} caracteres."];
        }

        return errors.Count == 0
            ? new TransferDetails(sourceAccountId!.Value, destinationAccountId!.Value, amount!.Value, transferDate!.Value, trimmedDescription)
            : null;
    }
}

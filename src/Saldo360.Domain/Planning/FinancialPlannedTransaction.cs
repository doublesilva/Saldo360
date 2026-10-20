using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transactions;

namespace Saldo360.Domain.Planning;

/// <summary>
/// Receita ou despesa prevista única: valor que se espera receber ou pagar em <see cref="DueDate"/>. Fica separada
/// das movimentações realizadas: não gera lançamento, não entra no saldo realizado e, vencida, continua pendente
/// (não é realizada automaticamente). O valor é sempre positivo; o tipo dá o sentido. Sem parcelas nem
/// recorrência. Não é excluída fisicamente: é cancelada, e cancelada não pode ser editada.
/// </summary>
public sealed class FinancialPlannedTransaction : Entity
{
    public const int DescriptionMaxLength = 200;

    /// <summary>Única moeda suportada nesta etapa.</summary>
    public const string DefaultCurrency = "BRL";

    private FinancialPlannedTransaction()
    {
        Description = string.Empty;
        Currency = DefaultCurrency;
    }

    public Guid WorkspaceId { get; private init; }

    public Guid AccountId { get; private set; }

    public Guid CategoryId { get; private set; }

    /// <summary>Receita ou despesa; o mesmo tipo da categoria (garantido também por FK).</summary>
    public CategoryType Type { get; private set; }

    public string Description { get; private set; }

    /// <summary>Sempre positivo, até duas casas decimais.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Data prevista de recebimento/pagamento (dia civil, sem horário). Passada, hoje ou futura.</summary>
    public DateOnly DueDate { get; private set; }

    public string Currency { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public Guid CreatedByUserId { get; private init; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public bool IsCancelled { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public static FinancialPlannedTransaction Create(Guid workspaceId, PlannedTransactionDetails details, Guid createdByUserId, DateTimeOffset now)
    {
        var planned = new FinancialPlannedTransaction
        {
            WorkspaceId = workspaceId,
            CreatedAt = now,
            CreatedByUserId = createdByUserId,
        };
        planned.Apply(details);
        return planned;
    }

    /// <summary>Substitui os dados. Família, criador e cancelamento não mudam; cancelada não pode ser editada.</summary>
    public void Update(PlannedTransactionDetails details, Guid userId, DateTimeOffset now)
    {
        if (IsCancelled)
        {
            throw new InvalidOperationException("Previsão cancelada não pode ser editada.");
        }

        Apply(details);
        UpdatedAt = now;
        UpdatedByUserId = userId;
    }

    /// <summary>
    /// Dados do lançamento efetivo que realiza esta previsão: conta, categoria, tipo e descrição da previsão, com o
    /// valor e a data efetivos informados (valor pode diferir do previsto; data não pode ser futura em relação a
    /// <paramref name="today"/>). Os valores previstos (<see cref="Amount"/>, <see cref="DueDate"/>) não mudam.
    /// </summary>
    public TransactionDetails? ToRealizationDetails(
        decimal? amount, DateOnly? transactionDate, DateOnly today, out Dictionary<string, string[]> errors) =>
        TransactionDetails.TryCreate(AccountId, CategoryId, Type.ToString(), Description, amount, transactionDate, today, out errors);

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

    /// <summary>Confirma o valor oficial desta obrigação pendente sem realizá-la ou alterar sua data/referências.</summary>
    public void ConfirmAmount(decimal amount, Guid userId, DateTimeOffset now)
    {
        if (IsCancelled) throw new InvalidOperationException("Previsão cancelada não pode ter valor confirmado.");
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "O valor confirmado deve ser maior que zero.");
        if (Money.Validate(amount, "O valor confirmado") is { } error) throw new ArgumentOutOfRangeException(nameof(amount), error);
        Amount = amount;
        UpdatedAt = now;
        UpdatedByUserId = userId;
    }

    private void Apply(PlannedTransactionDetails details)
    {
        AccountId = details.AccountId;
        CategoryId = details.CategoryId;
        Type = details.Type;
        Description = details.Description;
        Amount = details.Amount;
        DueDate = details.DueDate;
    }
}

/// <summary>
/// Dados da previsão já validados em formato. Conta e categoria (família, situação e tipo) são verificadas pela
/// aplicação e pelas FKs compostas no banco.
/// </summary>
public sealed record PlannedTransactionDetails
{
    private PlannedTransactionDetails(
        Guid accountId, Guid categoryId, CategoryType type, string description, decimal amount, DateOnly dueDate)
    {
        AccountId = accountId;
        CategoryId = categoryId;
        Type = type;
        Description = description;
        Amount = amount;
        DueDate = dueDate;
    }

    public Guid AccountId { get; }

    public Guid CategoryId { get; }

    public CategoryType Type { get; }

    public string Description { get; }

    public decimal Amount { get; }

    public DateOnly DueDate { get; }

    /// <summary>
    /// Valida presença das referências, tipo (Income/Expense exatos), descrição (obrigatória, até 200, sem espaços
    /// nas pontas), valor (positivo, até duas casas, dentro do limite, sem arredondar) e data prevista (obrigatória;
    /// passada, hoje ou futura).
    /// </summary>
    public static PlannedTransactionDetails? TryCreate(
        Guid? accountId, Guid? categoryId, string? type, string? description, decimal? amount, DateOnly? dueDate,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        var trimmedDescription = description?.Trim() ?? string.Empty;

        if (accountId is null || accountId == Guid.Empty)
        {
            errors["accountId"] = ["Informe a conta."];
        }

        if (categoryId is null || categoryId == Guid.Empty)
        {
            errors["categoryId"] = ["Informe a categoria."];
        }

        if (!CategoryTypes.TryParse(type, out var parsedType))
        {
            errors["type"] = ["Tipo inválido. Use Income (receita) ou Expense (despesa)."];
        }

        if (trimmedDescription.Length == 0)
        {
            errors["description"] = ["Informe a descrição."];
        }
        else if (trimmedDescription.Length > FinancialPlannedTransaction.DescriptionMaxLength)
        {
            errors["description"] = [$"A descrição deve ter no máximo {FinancialPlannedTransaction.DescriptionMaxLength} caracteres."];
        }

        if (amount is null)
        {
            errors["amount"] = ["Informe o valor."];
        }
        else if (amount <= 0)
        {
            errors["amount"] = ["O valor deve ser maior que zero. Use o tipo despesa para saídas, não valor negativo."];
        }
        else if (Money.Validate(amount.Value, "O valor") is { } amountError)
        {
            errors["amount"] = [amountError];
        }

        if (dueDate is null)
        {
            errors["dueDate"] = ["Informe a data prevista."];
        }

        return errors.Count == 0
            ? new PlannedTransactionDetails(accountId!.Value, categoryId!.Value, parsedType, trimmedDescription, amount!.Value, dueDate!.Value)
            : null;
    }
}

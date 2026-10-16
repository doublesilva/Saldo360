using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Domain.Transactions;

/// <summary>
/// Lançamento único já realizado (recebimento ou pagamento efetivo) em uma conta da família. O valor é sempre
/// positivo: o tipo diz o sentido (receita soma, despesa subtrai). A data é o dia civil do recebimento ou
/// pagamento, sem horário, e não pode ser futura em America/Sao_Paulo. Pode ser anterior ao saldo inicial
/// da conta (histórico), caso em que não entra no cálculo de saldo (ver docs/saldo-inicial.md).
/// Não é excluído fisicamente: é cancelado, e cancelado não pode ser editado.
/// <para>
/// Pode ser a realização de uma previsão (<see cref="PlannedTransactionId"/>). Nesse caso não é editável: para
/// corrigir, cancela-se (a previsão volta a pendente) e realiza-se de novo. O vínculo nunca muda.
/// </para>
/// </summary>
public sealed class FinancialTransaction : Entity
{
    public const int DescriptionMaxLength = 200;

    /// <summary>Única moeda suportada nesta etapa.</summary>
    public const string DefaultCurrency = "BRL";

    private FinancialTransaction()
    {
        Description = string.Empty;
        Currency = DefaultCurrency;
    }

    public Guid FamilyId { get; private init; }

    public Guid AccountId { get; private set; }

    public Guid? CategoryId { get; private set; }

    public FinancialTransactionOrigin Origin { get; private init; }

    /// <summary>Receita ou despesa; precisa ser o mesmo tipo da categoria (garantido também por FK).</summary>
    public CategoryType Type { get; private set; }

    public string Description { get; private set; }

    /// <summary>Sempre positivo, até duas casas decimais.</summary>
    public decimal Amount { get; private set; }

    public DateOnly TransactionDate { get; private set; }

    public string Currency { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public Guid CreatedByUserId { get; private init; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public bool IsCancelled { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    /// <summary>Previsão realizada por este lançamento (mesma família); null = lançamento avulso. Imutável.</summary>
    public Guid? PlannedTransactionId { get; private init; }

    public static FinancialTransaction Create(Guid familyId, TransactionDetails details, Guid createdByUserId, DateTimeOffset now) =>
        Create(familyId, details, plannedTransactionId: null, createdByUserId, now);

    public static FinancialTransaction CreateInvoicePayment(Guid familyId, Guid accountId, string description, decimal amount, DateOnly date, Guid createdByUserId, DateTimeOffset now) =>
        new FinancialTransaction
        {
            FamilyId = familyId, CreatedAt = now, CreatedByUserId = createdByUserId,
            Origin = FinancialTransactionOrigin.CreditCardInvoicePayment,
            CategoryId = null,
            AccountId = accountId, Type = CategoryType.Expense, Description = description,
            Amount = amount, TransactionDate = date, Currency = DefaultCurrency,
        };

    public static FinancialTransaction CreateDebtInstallmentPayment(Guid familyId, Guid accountId, string description, decimal amount, DateOnly date, Guid createdByUserId, DateTimeOffset now) =>
        new FinancialTransaction
        {
            FamilyId = familyId, CreatedAt = now, CreatedByUserId = createdByUserId,
            Origin = FinancialTransactionOrigin.DebtInstallmentPayment,
            CategoryId = null, AccountId = accountId, Type = CategoryType.Expense,
            Description = description, Amount = amount, TransactionDate = date, Currency = DefaultCurrency,
        };

    /// <summary>Lançamento efetivo que realiza a previsão <paramref name="plannedTransactionId"/>.</summary>
    public static FinancialTransaction CreateRealization(
        Guid familyId, TransactionDetails details, Guid plannedTransactionId, Guid createdByUserId, DateTimeOffset now) =>
        Create(familyId, details, (Guid?)plannedTransactionId, createdByUserId, now);

    private static FinancialTransaction Create(
        Guid familyId, TransactionDetails details, Guid? plannedTransactionId, Guid createdByUserId, DateTimeOffset now)
    {
        var transaction = new FinancialTransaction
        {
            FamilyId = familyId,
            PlannedTransactionId = plannedTransactionId,
            CreatedAt = now,
            CreatedByUserId = createdByUserId,
            Origin = plannedTransactionId is null ? FinancialTransactionOrigin.Manual : FinancialTransactionOrigin.PlannedRealization,
        };
        transaction.Apply(details);
        return transaction;
    }

    /// <summary>Substitui os dados do lançamento. Família, criador e cancelamento não mudam.</summary>
    public void Update(TransactionDetails details, Guid userId, DateTimeOffset now)
    {
        if (IsCancelled)
        {
            throw new InvalidOperationException("Lançamento cancelado não pode ser editado.");
        }

        if (PlannedTransactionId is not null)
        {
            throw new InvalidOperationException("Lançamento vinculado a uma previsão não pode ser editado.");
        }

        Apply(details);
        UpdatedAt = now;
        UpdatedByUserId = userId;
    }

    /// <summary>Cancela preservando o registro. Idempotente: false se já estava cancelado (nada muda).</summary>
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

    private void Apply(TransactionDetails details)
    {
        AccountId = details.AccountId;
        CategoryId = details.CategoryId;
        Type = details.Type;
        Description = details.Description;
        Amount = details.Amount;
        TransactionDate = details.TransactionDate;
    }
}

public enum FinancialTransactionOrigin
{
    Manual,
    PlannedRealization,
    CreditCardInvoicePayment,
    DebtInstallmentPayment,
}

/// <summary>
/// Dados do lançamento já validados em formato (ver <see cref="TryCreate"/>). Se conta e categoria pertencem à
/// família, estão ativas e combinam com o tipo é verificado pela aplicação (e pelas FKs no banco).
/// </summary>
public sealed record TransactionDetails
{
    private TransactionDetails(
        Guid accountId, Guid categoryId, CategoryType type, string description, decimal amount, DateOnly transactionDate)
    {
        AccountId = accountId;
        CategoryId = categoryId;
        Type = type;
        Description = description;
        Amount = amount;
        TransactionDate = transactionDate;
    }

    public Guid AccountId { get; }

    public Guid CategoryId { get; }

    public CategoryType Type { get; }

    public string Description { get; }

    public decimal Amount { get; }

    public DateOnly TransactionDate { get; }

    /// <summary>
    /// Valida presença das referências, tipo (Income/Expense exatos), descrição (obrigatória, até 200, sem espaços
    /// nas pontas), valor (positivo, até duas casas, dentro do limite, sem arredondar) e data (não futura em
    /// relação a <paramref name="today"/>, o dia civil em America/Sao_Paulo).
    /// </summary>
    public static TransactionDetails? TryCreate(
        Guid? accountId, Guid? categoryId, string? type, string? description, decimal? amount, DateOnly? transactionDate,
        DateOnly today, out Dictionary<string, string[]> errors)
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
        else if (trimmedDescription.Length > FinancialTransaction.DescriptionMaxLength)
        {
            errors["description"] = [$"A descrição deve ter no máximo {FinancialTransaction.DescriptionMaxLength} caracteres."];
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

        if (transactionDate is null)
        {
            errors["transactionDate"] = ["Informe a data do lançamento."];
        }
        else if (transactionDate > today)
        {
            errors["transactionDate"] = ["A data não pode ser futura: registre só movimentações já realizadas."];
        }

        return errors.Count == 0
            ? new TransactionDetails(accountId!.Value, categoryId!.Value, parsedType, trimmedDescription, amount!.Value, transactionDate!.Value)
            : null;
    }
}

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

    /// <summary>Natureza da despesa; a realização a leva ao lançamento. Receita é sempre <see cref="ExpenseNature.Undefined"/>.</summary>
    public ExpenseNature Nature { get; private set; }

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

    /// <summary>Quando preenchida, esta previsão é a EXCEÇÃO de uma competência da recorrência: seu valor substitui (nunca soma) o da recorrência naquele mês.</summary>
    public Guid? ExceptionRecurrenceId { get; private set; }

    /// <summary>Primeiro dia do mês da competência substituída (sempre junto de <see cref="ExceptionRecurrenceId"/>).</summary>
    public DateOnly? ExceptionCompetence { get; private set; }

    /// <summary>Competência anterior à implantação sem evidência de pagamento: fica "A revisar", fora de atraso e do saldo projetado até ser conciliada ou reconhecida.</summary>
    public bool NeedsHistoricalReview { get; private set; }

    /// <summary>
    /// Valor real confirmado da competência (conta/boleto recebido). Prevalece sobre <see cref="Amount"/> (ajuste da competência) e sobre o valor-base da recorrência;
    /// não é pagamento: a obrigação continua pendente até ser paga. Null = nunca confirmado (exceções antigas continuam sendo apenas ajustes).
    /// </summary>
    public decimal? ConfirmedAmount { get; private set; }

    public DateTimeOffset? AmountConfirmedAt { get; private set; }

    public Guid? AmountConfirmedByUserId { get; private set; }

    /// <summary>Origem do valor confirmado. Null com valor confirmado = confirmação manual da PROJ-04 (única que existia).</summary>
    public AmountConfirmationSource? ConfirmationSource { get; private set; }

    /// <summary>Transação bancária (<c>PluggyImportedTransaction.Id</c>) que informou o valor, quando a origem é Pluggy automática ou vínculo manual. Sem FK de propósito: o reset do workspace apaga em qualquer ordem.</summary>
    public Guid? AmountSourceTransactionId { get; private set; }

    /// <summary>Valor vigente da previsão: confirmado, quando existe; senão o previsto/ajustado.</summary>
    public decimal EffectiveAmount => ConfirmedAmount ?? Amount;

    /// <summary>Confirmação feita pelo usuário (valor manual ou vínculo manual): nunca sobrescrita nem revertida por sincronização.</summary>
    public bool IsManuallyConfirmed => ConfirmedAmount != null && ConfirmationSource != AmountConfirmationSource.PluggyAuto;

    public bool IsAutomaticallyConfirmed => ConfirmedAmount != null && ConfirmationSource == AmountConfirmationSource.PluggyAuto;

    /// <summary>Registra (ou corrige) o valor real confirmado desta competência pelo usuário. Não altera <see cref="Amount"/>, a data nem as referências.</summary>
    public void ConfirmCompetenceAmount(decimal amount, Guid userId, DateTimeOffset now) =>
        Confirm(amount, userId, now, AmountConfirmationSource.Manual, null);

    /// <summary>
    /// Aplica o valor informado pelo banco. <c>PluggyAuto</c> (sem usuário) nunca sobrescreve confirmação manual; <c>ManualLink</c> exige o usuário que vinculou.
    /// Atualizar a mesma origem com outro valor ou outra transação é a correção automática.
    /// </summary>
    public void ConfirmFromBank(decimal amount, Guid? userId, DateTimeOffset now, AmountConfirmationSource source, Guid transactionId)
    {
        if (source == AmountConfirmationSource.Manual) throw new ArgumentOutOfRangeException(nameof(source), "Use ConfirmCompetenceAmount para a confirmação manual.");
        if (source == AmountConfirmationSource.ManualLink && userId is null) throw new ArgumentException("O vínculo manual exige o usuário.", nameof(userId));
        if (source == AmountConfirmationSource.PluggyAuto && IsManuallyConfirmed) throw new InvalidOperationException("Confirmação manual não é sobrescrita por sincronização.");
        Confirm(amount, userId, now, source, transactionId);
    }

    /// <summary>Desfaz só a confirmação automática (fonte definitivamente invalidada). Confirmação manual ou vínculo manual nunca é desfeito. Retorna se algo mudou.</summary>
    public bool RevertAutomaticConfirmation()
    {
        if (!IsAutomaticallyConfirmed) return false;
        ConfirmedAmount = null; AmountConfirmedAt = null; AmountConfirmedByUserId = null; ConfirmationSource = null; AmountSourceTransactionId = null;
        return true;
    }

    private void Confirm(decimal amount, Guid? userId, DateTimeOffset now, AmountConfirmationSource source, Guid? transactionId)
    {
        if (IsCancelled) throw new InvalidOperationException("Previsão cancelada não pode ter valor real confirmado.");
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "O valor confirmado deve ser maior que zero.");
        if (Money.Validate(amount, "O valor confirmado") is { } error) throw new ArgumentOutOfRangeException(nameof(amount), error);
        ConfirmedAmount = amount; AmountConfirmedAt = now; AmountConfirmedByUserId = userId; ConfirmationSource = source; AmountSourceTransactionId = transactionId;
    }

    public void MarkAsCompetenceException(Guid recurrenceId, DateOnly competence)
    {
        ExceptionRecurrenceId = recurrenceId;
        ExceptionCompetence = new DateOnly(competence.Year, competence.Month, 1);
    }

    public void MarkHistoricalReview() => NeedsHistoricalReview = true;

    /// <summary>Ação explícita do usuário: reconhece a pendência histórica como dívida real (passa a contar como pendente/vencida).</summary>
    public void ConfirmHistoricalPending(Guid userId, DateTimeOffset now)
    {
        if (IsCancelled) throw new InvalidOperationException("Previsão cancelada não pode ser reconhecida como pendente.");
        NeedsHistoricalReview = false; UpdatedAt = now; UpdatedByUserId = userId;
    }

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
        TransactionDetails.TryCreate(AccountId, CategoryId, Type.ToString(), Description, amount, transactionDate, today, out errors, Nature.ToString());

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
        Nature = details.Type == CategoryType.Expense ? details.Nature : ExpenseNature.Undefined;
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
        Guid accountId, Guid categoryId, CategoryType type, string description, decimal amount, DateOnly dueDate, ExpenseNature nature)
    {
        Nature = nature;
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

    public ExpenseNature Nature { get; }

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
        out Dictionary<string, string[]> errors, string? expenseNature = null)
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

        var nature = ExpenseNature.Undefined;
        if (!errors.ContainsKey("type") && !ExpenseNatures.TryResolve(expenseNature, parsedType, out nature, out var natureError))
        {
            errors["expenseNature"] = [natureError!];
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
            ? new PlannedTransactionDetails(accountId!.Value, categoryId!.Value, parsedType, trimmedDescription, amount!.Value, dueDate!.Value, nature)
            : null;
    }
}

/// <summary>Quem estabeleceu o valor confirmado da competência: o usuário (valor manual), a sincronização Pluggy (correspondência segura automática) ou o usuário vinculando a uma transação bancária.</summary>
public enum AmountConfirmationSource { Manual, PluggyAuto, ManualLink }

using Saldo360.Domain.Common;

namespace Saldo360.Domain.Accounts;

/// <summary>
/// Conta financeira usada pela família (corrente, poupança ou dinheiro), PF ou PJ.
/// Sem responsável, é uma conta conjunta. Todas as contas são visíveis a todos os integrantes:
/// o responsável não define privacidade. Não guarda agência, número nem credenciais bancárias,
/// e não é excluída fisicamente (só desativada).
/// <para>
/// O saldo inicial é opcional: valor e data de referência existem juntos ou não existem. O valor é o
/// saldo no INÍCIO da data de referência, antes de qualquer movimentação desse dia (ver
/// docs/saldo-inicial.md). Não é o saldo atual.
/// </para>
/// </summary>
public sealed class FinancialAccount : Entity
{
    public const int NameMaxLength = 100;
    public const int InstitutionMaxLength = 100;

    /// <summary>Única moeda suportada nesta etapa.</summary>
    public const string DefaultCurrency = "BRL";

    private FinancialAccount()
    {
        Name = string.Empty;
        Currency = DefaultCurrency;
    }

    public Guid FamilyId { get; private init; }

    public string Name { get; private set; }

    public string? Institution { get; private set; }

    public AccountType Type { get; private set; }

    public FinancialScope Scope { get; private set; }

    /// <summary>Vínculo (FamilyMember.Id) do responsável, da mesma família; null = conta conjunta.</summary>
    public Guid? ResponsibleMemberId { get; private set; }

    /// <summary>Saldo no início de <see cref="OpeningBalanceDate"/>; pode ser negativo. Null = não informado.</summary>
    public decimal? OpeningBalance { get; private set; }

    /// <summary>Data de referência do saldo inicial (sem horário). Null se e somente se o saldo for null.</summary>
    public DateOnly? OpeningBalanceDate { get; private set; }

    public bool IsActive { get; private set; }

    public string Currency { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public Guid CreatedByUserId { get; private init; }

    public static FinancialAccount Create(Guid familyId, AccountDetails details, Guid createdByUserId, DateTimeOffset now)
    {
        var account = new FinancialAccount
        {
            FamilyId = familyId,
            IsActive = true,
            CreatedAt = now,
            CreatedByUserId = createdByUserId,
        };
        account.Apply(details);
        return account;
    }

    /// <summary>Atualiza os campos cadastrais e a situação. Família, criador e moeda não mudam.</summary>
    public void Update(AccountDetails details, bool isActive)
    {
        Apply(details);
        IsActive = isActive;
    }

    private void Apply(AccountDetails details)
    {
        Name = details.Name;
        Institution = details.Institution;
        Type = details.Type;
        Scope = details.Scope;
        ResponsibleMemberId = details.ResponsibleMemberId;
        OpeningBalance = details.OpeningBalance?.Amount;
        OpeningBalanceDate = details.OpeningBalance?.Date;
    }
}

/// <summary>Campos cadastrais já validados (ver <see cref="AccountDetails.TryCreate"/>).</summary>
public sealed record AccountDetails
{
    private AccountDetails(
        string name, string? institution, AccountType type, FinancialScope scope, Guid? responsibleMemberId,
        OpeningBalance? openingBalance)
    {
        Name = name;
        Institution = institution;
        Type = type;
        Scope = scope;
        ResponsibleMemberId = responsibleMemberId;
        OpeningBalance = openingBalance;
    }

    public string Name { get; }

    public string? Institution { get; }

    public AccountType Type { get; }

    public FinancialScope Scope { get; }

    public Guid? ResponsibleMemberId { get; }

    /// <summary>Saldo inicial com data de referência; null = não informado.</summary>
    public OpeningBalance? OpeningBalance { get; }

    /// <summary>
    /// Valida nome (obrigatório, até 100), instituição (opcional, até 100), os valores textuais de tipo e
    /// escopo, que precisam ser exatamente um dos nomes permitidos, e o saldo inicial (valor e data juntos ou
    /// ambos ausentes). Espaços nas pontas são removidos.
    /// </summary>
    public static AccountDetails? TryCreate(
        string? name, string? institution, string? type, string? scope, Guid? responsibleMemberId,
        decimal? openingBalance, DateOnly? openingBalanceDate,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        var trimmedName = name?.Trim() ?? string.Empty;
        var trimmedInstitution = string.IsNullOrWhiteSpace(institution) ? null : institution.Trim();

        if (trimmedName.Length == 0)
        {
            errors["name"] = ["Informe o nome da conta."];
        }
        else if (trimmedName.Length > FinancialAccount.NameMaxLength)
        {
            errors["name"] = [$"O nome deve ter no máximo {FinancialAccount.NameMaxLength} caracteres."];
        }

        if (trimmedInstitution is { Length: > FinancialAccount.InstitutionMaxLength })
        {
            errors["institution"] = [$"A instituição deve ter no máximo {FinancialAccount.InstitutionMaxLength} caracteres."];
        }

        if (!AccountTypes.TryParse(type, out var parsedType))
        {
            errors["type"] = ["Tipo inválido. Use Checking, Savings ou Cash."];
        }

        if (!FinancialScopes.TryParse(scope, out var parsedScope))
        {
            errors["financialScope"] = ["Escopo inválido. Use PF ou PJ."];
        }

        if (responsibleMemberId == Guid.Empty)
        {
            errors["responsibleMemberId"] = ["Responsável inválido."];
        }

        var parsedBalance = Accounts.OpeningBalance.TryCreate(openingBalance, openingBalanceDate, errors);

        return errors.Count == 0
            ? new AccountDetails(trimmedName, trimmedInstitution, parsedType, parsedScope, responsibleMemberId, parsedBalance)
            : null;
    }
}

/// <summary>
/// Saldo da conta no início de <see cref="Date"/>, antes das movimentações desse dia. Pode ser negativo
/// (limite já utilizado); não representa limite de crédito disponível. Centavos exatos, sem arredondamento.
/// </summary>
public sealed record OpeningBalance
{
    /// <summary>Casas decimais aceitas (centavos).</summary>
    public const int MaxScale = 2;

    /// <summary>
    /// Limite absoluto (exclusivo): 13 dígitos inteiros + 2 decimais = 15 dígitos significativos, que o
    /// frontend representa sem perda em um number do JavaScript.
    /// </summary>
    public const decimal Limit = 10_000_000_000_000m;

    private OpeningBalance(decimal amount, DateOnly date)
    {
        Amount = amount;
        Date = date;
    }

    public decimal Amount { get; }

    public DateOnly Date { get; }

    /// <summary>
    /// Ambos ausentes → null sem erro. Só um informado, mais de duas casas decimais (inclusive zeros à direita,
    /// como 1.500, que não são arredondados) ou valor fora do limite → erro em <paramref name="errors"/> e null.
    /// </summary>
    internal static OpeningBalance? TryCreate(decimal? amount, DateOnly? date, Dictionary<string, string[]> errors)
    {
        switch (amount, date)
        {
            case (null, null):
                return null;
            case (null, _):
                errors["openingBalance"] = ["Informe o valor do saldo inicial ou remova a data de referência."];
                return null;
            case (_, null):
                errors["openingBalanceDate"] = ["Informe a data de referência do saldo inicial."];
                return null;
        }

        if (amount.Value.Scale > MaxScale)
        {
            errors["openingBalance"] = ["O saldo inicial deve ter no máximo duas casas decimais."];
            return null;
        }

        if (Math.Abs(amount.Value) >= Limit)
        {
            errors["openingBalance"] = ["O saldo inicial deve ter no máximo 13 dígitos antes da vírgula."];
            return null;
        }

        return new OpeningBalance(amount.Value, date.Value);
    }
}

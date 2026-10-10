using Saldo360.Domain.Common;

namespace Saldo360.Domain.Accounts;

/// <summary>
/// Conta financeira usada pela família (corrente, poupança ou dinheiro), PF ou PJ.
/// Sem responsável, é uma conta conjunta. Todas as contas são visíveis a todos os integrantes:
/// o responsável não define privacidade. Não guarda agência, número nem credenciais bancárias,
/// e não é excluída fisicamente (só desativada).
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
    }
}

/// <summary>Campos cadastrais já validados (ver <see cref="AccountDetails.TryCreate"/>).</summary>
public sealed record AccountDetails
{
    private AccountDetails(string name, string? institution, AccountType type, FinancialScope scope, Guid? responsibleMemberId)
    {
        Name = name;
        Institution = institution;
        Type = type;
        Scope = scope;
        ResponsibleMemberId = responsibleMemberId;
    }

    public string Name { get; }

    public string? Institution { get; }

    public AccountType Type { get; }

    public FinancialScope Scope { get; }

    public Guid? ResponsibleMemberId { get; }

    /// <summary>
    /// Valida nome (obrigatório, até 100), instituição (opcional, até 100) e os valores textuais de tipo e
    /// escopo, que precisam ser exatamente um dos nomes permitidos. Espaços nas pontas são removidos.
    /// </summary>
    public static AccountDetails? TryCreate(
        string? name, string? institution, string? type, string? scope, Guid? responsibleMemberId,
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

        return errors.Count == 0
            ? new AccountDetails(trimmedName, trimmedInstitution, parsedType, parsedScope, responsibleMemberId)
            : null;
    }
}

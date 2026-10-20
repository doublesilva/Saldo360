using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public sealed class FinancialCreditCard : Entity
{
    public const int NameMaxLength = 100;
    public const int IssuerMaxLength = 100;
    public const int LastFourDigitsLength = 4;

    private FinancialCreditCard() { Name = string.Empty; Issuer = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public string Name { get; private set; }
    public string Issuer { get; private set; }
    public string? LastFourDigits { get; private set; }
    public Guid? HolderUserId { get; private set; }
    public decimal CreditLimit { get; private set; }
    public int ClosingDay { get; private set; }
    public int DueDay { get; private set; }
    public Guid? DefaultPaymentAccountId { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid CreatedByUserId { get; private init; }
    public Guid UpdatedByUserId { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public Guid? ArchivedByUserId { get; private set; }

    public static FinancialCreditCard Create(Guid workspaceId, CreditCardDetails details, Guid userId, DateTimeOffset now) => new()
    {
        WorkspaceId = workspaceId, CreatedAt = now, UpdatedAt = now, CreatedByUserId = userId, UpdatedByUserId = userId,
        Name = details.Name, Issuer = details.Issuer, LastFourDigits = details.LastFourDigits, HolderUserId = details.HolderUserId,
        CreditLimit = details.CreditLimit, ClosingDay = details.ClosingDay, DueDay = details.DueDay,
        DefaultPaymentAccountId = details.DefaultPaymentAccountId,
    };

    public void Update(CreditCardDetails details, Guid userId, DateTimeOffset now)
    {
        Name = details.Name; Issuer = details.Issuer; LastFourDigits = details.LastFourDigits; HolderUserId = details.HolderUserId;
        CreditLimit = details.CreditLimit; ClosingDay = details.ClosingDay; DueDay = details.DueDay;
        DefaultPaymentAccountId = details.DefaultPaymentAccountId; UpdatedAt = now; UpdatedByUserId = userId;
    }

    public void Archive(Guid userId, DateTimeOffset now)
    {
        if (IsArchived) return;
        IsArchived = true; ArchivedAt = now; ArchivedByUserId = userId; UpdatedAt = now; UpdatedByUserId = userId;
    }
}

public sealed record CreditCardDetails(
    string Name, string Issuer, string? LastFourDigits, Guid? HolderUserId, decimal CreditLimit, int ClosingDay, int DueDay, Guid? DefaultPaymentAccountId)
{
    public static CreditCardDetails? TryCreate(
        string? name, string? issuer, Guid? holderUserId, decimal? creditLimit, int? closingDay, int? dueDay,
        Guid? defaultPaymentAccountId, out Dictionary<string, string[]> errors)
        => TryCreate(name, issuer, null, holderUserId, creditLimit, closingDay, dueDay, defaultPaymentAccountId, out errors);

    public static CreditCardDetails? TryCreate(
        string? name, string? issuer, string? lastFourDigits, Guid? holderUserId, decimal? creditLimit, int? closingDay, int? dueDay,
        Guid? defaultPaymentAccountId, out Dictionary<string, string[]> errors)
    {
        errors = [];
        var normalizedName = name?.Trim() ?? string.Empty;
        var normalizedIssuer = issuer?.Trim() ?? string.Empty;
        var normalizedLastFourDigits = string.IsNullOrWhiteSpace(lastFourDigits) ? null : lastFourDigits.Trim();
        if (normalizedName.Length == 0) errors["name"] = ["Informe o nome do cartão."];
        else if (normalizedName.Length > FinancialCreditCard.NameMaxLength) errors["name"] = [$"O nome deve ter no máximo {FinancialCreditCard.NameMaxLength} caracteres."];
        if (normalizedIssuer.Length == 0) errors["issuer"] = ["Informe a instituição emissora."];
        else if (normalizedIssuer.Length > FinancialCreditCard.IssuerMaxLength) errors["issuer"] = [$"A instituição deve ter no máximo {FinancialCreditCard.IssuerMaxLength} caracteres."];
        if (normalizedLastFourDigits is not null && (normalizedLastFourDigits.Length != FinancialCreditCard.LastFourDigitsLength || normalizedLastFourDigits.Any(character => character is < '0' or > '9')))
            errors["lastFourDigits"] = ["Os últimos quatro dígitos devem conter exatamente 4 algarismos."];
        if (creditLimit is null) errors["creditLimit"] = ["Informe o limite de crédito."];
        else if (creditLimit < 0) errors["creditLimit"] = ["O limite de crédito não pode ser negativo."];
        else if (Money.Validate(creditLimit.Value, "O limite de crédito") is { } error) errors["creditLimit"] = [error];
        ValidateDay(closingDay, "closingDay", "fechamento", errors);
        ValidateDay(dueDay, "dueDay", "vencimento", errors);
        return errors.Count == 0 ? new(normalizedName, normalizedIssuer, normalizedLastFourDigits, holderUserId, creditLimit!.Value, closingDay!.Value, dueDay!.Value, defaultPaymentAccountId) : null;

        static void ValidateDay(int? day, string field, string label, Dictionary<string, string[]> target)
        {
            if (day is null or < 1 or > 31) target[field] = [$"O dia de {label} deve estar entre 1 e 31."];
        }
    }
}

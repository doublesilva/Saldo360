namespace Saldo360.Domain.Accounts;

/// <summary>Tipo da conta. Persistido pelo nome.</summary>
public enum AccountType
{
    Checking,
    Savings,
    Cash,
}

/// <summary>Pessoa física ou jurídica. Persistido pelo nome.</summary>
public enum FinancialScope
{
    PF,
    PJ,
}

/// <summary>Conversão estrita: só os nomes exatos (sensível à caixa); números e outros textos são recusados.</summary>
public static class AccountTypes
{
    public static IReadOnlyList<string> Names { get; } = Enum.GetNames<AccountType>();

    public static bool TryParse(string? value, out AccountType type) => StrictEnum.TryParse(value, out type);
}

public static class FinancialScopes
{
    public static IReadOnlyList<string> Names { get; } = Enum.GetNames<FinancialScope>();

    public static bool TryParse(string? value, out FinancialScope scope) => StrictEnum.TryParse(value, out scope);
}

internal static class StrictEnum
{
    public static bool TryParse<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        if (value is null || !Enum.GetNames<T>().Contains(value, StringComparer.Ordinal))
        {
            return false;
        }

        result = Enum.Parse<T>(value);
        return true;
    }
}

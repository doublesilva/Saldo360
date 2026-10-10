namespace Saldo360.Domain.Common;

/// <summary>
/// Regras monetárias comuns (BRL): centavos exatos, sem arredondamento. Valores com mais de duas casas
/// decimais, inclusive zeros à direita como 1.500, são recusados, não ajustados.
/// </summary>
public static class Money
{
    /// <summary>Casas decimais aceitas (centavos).</summary>
    public const int MaxScale = 2;

    /// <summary>
    /// Limite absoluto (exclusivo): 13 dígitos inteiros + 2 decimais = 15 dígitos significativos, que o
    /// frontend representa sem perda.
    /// </summary>
    public const decimal Limit = 10_000_000_000_000m;

    /// <summary>Mensagem de erro para escala ou limite inválidos, ou null se o valor for aceitável.</summary>
    /// <param name="subject">Sujeito da mensagem, ex.: "O saldo inicial", "O valor".</param>
    public static string? Validate(decimal amount, string subject)
    {
        if (amount.Scale > MaxScale)
        {
            return $"{subject} deve ter no máximo duas casas decimais.";
        }

        return Math.Abs(amount) >= Limit ? $"{subject} deve ter no máximo 13 dígitos antes da vírgula." : null;
    }

    /// <summary>
    /// Texto decimal exato com duas casas e ponto ("1300.00", "-0.30"), para trafegar valores sem passar por
    /// ponto flutuante. Não arredonda: um valor com centavos fracionados é erro de programação.
    /// </summary>
    public static string Format(decimal amount)
    {
        if (decimal.Round(amount, MaxScale) != amount)
        {
            throw new ArgumentException($"Valor com mais de {MaxScale} casas decimais: {amount}.", nameof(amount));
        }

        return amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }
}
